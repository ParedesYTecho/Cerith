#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class PresentMonTelemetryProviderTests
{
    private static readonly MetricId FpsId = new("game.fps.display");
    private static readonly MetricId FrameTimeId = new("game.frametime.display");
    private static readonly MetricId P1Id = new("game.fps.display.p01");
    private static readonly MetricId ApplicationId = new("game.session.application");

    [TestMethod]
    public async Task RegistersAndPublishesTheFourMeasuredDisplayedMetrics()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 144.5, 6.92,
            P1State: MetricState.Fresh, P1Fps: 35.25));
        using var provider = new PresentMonTelemetryProvider(registry, bus, Environment.ProcessId,
            process => { Assert.AreEqual(Environment.ProcessId, process.Id); return capture; });

        var (fps, frameTime, p1, application) = await PublishAndReadAsync(provider, bus);

        AssertSnapshot(registry, fps, FpsId, 144.5, "Displayed FPS", "FPS", "rate");
        AssertSnapshot(registry, frameTime, FrameTimeId, 6.92, "Displayed Frame Time", "ms", "duration");
        AssertSnapshot(registry, p1, P1Id, 35.25, "Displayed FPS P1 (10 s)", "FPS", "rate");
        Assert.IsTrue(registry.TryGet(P1Id, out var descriptor));
        StringAssert.Contains(descriptor!.Description!, "linearly interpolated 99th percentile");
        StringAssert.Contains(descriptor.Description!, "4096 frame samples");
        Assert.AreEqual("PresentMon", fps.Source);
        AssertApplication(application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        Assert.IsTrue(registry.TryGet(ApplicationId, out var applicationDescriptor));
        Assert.AreEqual("Selected Game", applicationDescriptor!.DisplayName);
        Assert.AreEqual("text", applicationDescriptor.SemanticType);
        Assert.AreEqual(MetricValueKind.String, applicationDescriptor.ValueKind);
        Assert.IsNull(applicationDescriptor.Unit);
        Assert.AreEqual(1, capture.PollCount);

        provider.Dispose();
        Assert.IsTrue(capture.IsDisposed);
    }

    [TestMethod]
    public async Task P1WarmupDoesNotInvalidateFreshAverageMetrics()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 144, 6.94));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId, _ => capture);

        var (fps, frameTime, p1, application) = await PublishAndReadAsync(provider, bus);

        Assert.AreEqual(MetricState.Fresh, fps.State);
        Assert.AreEqual(MetricState.Fresh, frameTime.State);
        AssertValueless(p1, P1Id, MetricState.Unavailable);
        AssertApplication(application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
    }

    [TestMethod]
    public async Task P1InvalidValuesDoNotInvalidateTheAverageMetrics()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, 144, 6.94, P1State: MetricState.Fresh, P1Fps: double.NaN),
            new PresentMonReading(MetricState.Fresh, 144, 6.94, P1State: MetricState.Fresh, P1Fps: double.PositiveInfinity),
            new PresentMonReading(MetricState.Fresh, 144, 6.94, P1State: MetricState.Fresh, P1Fps: 0));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId, _ => capture);

        for (var i = 0; i < 3; i++)
        {
            var (fps, frameTime, p1, application) = await PublishAndReadAsync(provider, bus);
            Assert.AreEqual(MetricState.Fresh, fps.State);
            Assert.AreEqual(MetricState.Fresh, frameTime.State);
            AssertValueless(p1, P1Id, MetricState.Error);
            Assert.AreEqual("invalid_reading", p1.ErrorCode);
            AssertApplication(application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        }
    }

    [TestMethod]
    public async Task P1QueryErrorsAreScopedToP1()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, 144, 6.94,
                P1State: MetricState.Error, P1ErrorCode: "presentmon_api_failure"),
            new PresentMonReading(MetricState.Fresh, 144, 6.94,
                P1State: MetricState.Error, P1ErrorCode: "multiple_swap_chains"));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId, _ => capture);

        foreach (var errorCode in new[] { "presentmon_api_failure", "multiple_swap_chains" })
        {
            var (fps, frameTime, p1, application) = await PublishAndReadAsync(provider, bus);
            Assert.AreEqual(MetricState.Fresh, fps.State);
            Assert.AreEqual(MetricState.Fresh, frameTime.State);
            AssertValueless(p1, P1Id, MetricState.Error);
            Assert.AreEqual(errorCode, p1.ErrorCode);
            AssertApplication(application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        }
    }

    [TestMethod]
    public async Task MissingTargetPublishesStoppedWithoutOpeningCapture()
    {
        using var bus = new MetricBus(1);
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, null,
            _ => throw new AssertFailedException("A missing target must not open PresentMon."));

        var (fps, frameTime, p1, application) = await PublishAndReadAsync(provider, bus);

        AssertValueless(fps, FpsId, MetricState.Stopped);
        AssertValueless(frameTime, FrameTimeId, MetricState.Stopped);
        AssertValueless(p1, P1Id, MetricState.Stopped);
        AssertValueless(application, ApplicationId, MetricState.Stopped);
    }

    [TestMethod]
    public async Task UnavailableAndApiFailureNeverPublishValues()
    {
        using var unavailableBus = new MetricBus(1);
        using var unavailableCapture = new FakeCapture(new PresentMonReading(MetricState.Unavailable));
        using var unavailableProvider = new PresentMonTelemetryProvider(new MetricRegistry(), unavailableBus, Environment.ProcessId,
            _ => unavailableCapture);
        var unavailable = await PublishAndReadAsync(unavailableProvider, unavailableBus);
        AssertValueless(unavailable.Fps, FpsId, MetricState.Unavailable);
        AssertValueless(unavailable.FrameTime, FrameTimeId, MetricState.Unavailable);
        AssertValueless(unavailable.P1, P1Id, MetricState.Unavailable);
        AssertApplication(unavailable.Application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);

        using var errorBus = new MetricBus(1);
        using var errorCapture = new FakeCapture(new PresentMonReading(MetricState.Error,
            ErrorCode: "presentmon_api_failure", P1State: MetricState.Error,
            P1ErrorCode: "presentmon_api_failure"));
        using var errorProvider = new PresentMonTelemetryProvider(new MetricRegistry(), errorBus, Environment.ProcessId,
            _ => errorCapture);
        var error = await PublishAndReadAsync(errorProvider, errorBus);
        AssertValueless(error.Fps, FpsId, MetricState.Error);
        AssertValueless(error.FrameTime, FrameTimeId, MetricState.Error);
        AssertValueless(error.P1, P1Id, MetricState.Error);
        Assert.AreEqual("presentmon_api_failure", error.Fps.ErrorCode);
        Assert.AreEqual("presentmon_api_failure", error.P1.ErrorCode);
        AssertApplication(error.Application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        Assert.IsTrue(errorCapture.IsDisposed);
    }

    [TestMethod]
    public async Task UnavailableApiFailureDisposesCaptureBeforeRetry()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6))
        {
            PollError = new PresentMonCaptureException(MetricState.Unavailable, "presentmon_unavailable")
        };
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId, _ => capture);

        var (fps, frameTime, p1, application) = await PublishAndReadAsync(provider, bus);

        AssertValueless(fps, FpsId, MetricState.Unavailable);
        AssertValueless(frameTime, FrameTimeId, MetricState.Unavailable);
        AssertValueless(p1, P1Id, MetricState.Unavailable);
        Assert.IsTrue(capture.IsDisposed);
        AssertApplication(application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
    }

    [TestMethod]
    public async Task NonFiniteOrZeroFreshAverageValuesBecomeErrorWithoutPublishingThem()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, double.NaN, 10),
            new PresentMonReading(MetricState.Fresh, 60, double.PositiveInfinity),
            new PresentMonReading(MetricState.Fresh, 0, 16.6));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId, _ => capture);

        for (var i = 0; i < 3; i++)
        {
            var (fps, frameTime, p1, application) = await PublishAndReadAsync(provider, bus);
            AssertValueless(fps, FpsId, MetricState.Error);
            AssertValueless(frameTime, FrameTimeId, MetricState.Error);
            Assert.AreEqual("invalid_reading", fps.ErrorCode);
            AssertValueless(p1, P1Id, MetricState.Unavailable);
            AssertApplication(application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        }
    }

    [TestMethod]
    public async Task CancellationPreventsCaptureAndBusWork()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId, _ => capture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await provider.PublishNextAsync(cancellation.Token);
            Assert.Fail("A canceled provider poll must stop before touching the capture.");
        }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, capture.PollCount);
    }

    [TestMethod]
    public async Task StoppedCaptureClearsAllFreshValuesAndIsNotReopened()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, 60, 16.6, P1State: MetricState.Fresh, P1Fps: 25),
            new PresentMonReading(MetricState.Stopped, P1State: MetricState.Stopped));
        var openCount = 0;
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId, _ =>
        {
            openCount++;
            return capture;
        });

        var fresh = await PublishAndReadAsync(provider, bus);
        Assert.AreEqual(MetricState.Fresh, fresh.Fps.State);
        Assert.AreEqual(MetricState.Fresh, fresh.FrameTime.State);
        Assert.AreEqual(MetricState.Fresh, fresh.P1.State);

        var pmStopped = await PublishAndReadAsync(provider, bus);
        AssertValueless(pmStopped.Fps, FpsId, MetricState.Unavailable);
        AssertValueless(pmStopped.FrameTime, FrameTimeId, MetricState.Unavailable);
        AssertValueless(pmStopped.P1, P1Id, MetricState.Unavailable);
        AssertApplication(pmStopped.Application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        Assert.IsTrue(capture.IsDisposed);

        var stoppedAgain = await PublishAndReadAsync(provider, bus);
        AssertValueless(stoppedAgain.Fps, FpsId, MetricState.Unavailable);
        AssertValueless(stoppedAgain.FrameTime, FrameTimeId, MetricState.Unavailable);
        AssertValueless(stoppedAgain.P1, P1Id, MetricState.Unavailable);
        AssertApplication(stoppedAgain.Application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        Assert.AreEqual(1, openCount);
    }

    [TestMethod]
    public async Task ApplicationMetadataFailuresDoNotInvalidateFrameMetrics()
    {
        foreach (var (readName, expectedState, expectedCode) in new (Func<Process, string?>, MetricState, string?)[]
        {
            (_ => throw new System.ComponentModel.Win32Exception(), MetricState.Unavailable, null),
            (_ => "C:\\private\\game.exe", MetricState.Error, "invalid_application_name"),
            (_ => throw new InvalidOperationException(), MetricState.Error, "application_metadata_failure")
        })
        {
            using var bus = new MetricBus(1);
            using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6));
            using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId,
                _ => capture, readName);

            var result = await PublishAndReadAsync(provider, bus);

            Assert.AreEqual(MetricState.Fresh, result.Fps.State);
            Assert.AreEqual(MetricState.Fresh, result.FrameTime.State);
            Assert.AreEqual(expectedState, result.Application.State);
            Assert.IsNull(result.Application.Value);
            Assert.AreEqual(expectedCode, result.Application.ErrorCode);
        }
    }

    [TestMethod]
    public async Task PresentMonCaptureRetriesReuseTheSelectedProcessHandle()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6));
        Process? firstProcess = null;
        var openCount = 0;
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId,
            process =>
            {
                openCount++;
                if (firstProcess is null)
                {
                    firstProcess = process;
                    throw new PresentMonCaptureException(MetricState.Unavailable, "presentmon_unavailable");
                }
                Assert.AreSame(firstProcess, process);
                return capture;
            });

        var unavailable = await PublishAndReadAsync(provider, bus);
        AssertValueless(unavailable.Fps, FpsId, MetricState.Unavailable);
        AssertApplication(unavailable.Application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);

        await Task.Delay(TimeSpan.FromMilliseconds(5200));
        var recovered = await PublishAndReadAsync(provider, bus);
        Assert.AreEqual(MetricState.Fresh, recovered.Fps.State);
        AssertApplication(recovered.Application, MetricState.Fresh, Process.GetCurrentProcess().ProcessName);
        Assert.AreEqual(2, openCount);
    }

    [TestMethod]
    public async Task FailedInitialIdentityAcquisitionNeverReopensTheSelectedPid()
    {
        using var bus = new MetricBus(1);
        var processLookups = 0;
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId,
            _ => throw new AssertFailedException("Capture must not open without a retained process identity."),
            getProcessById: processId =>
            {
                Assert.AreEqual(Environment.ProcessId, processId);
                processLookups++;
                var process = Process.GetProcessById(processId);
                if (processLookups == 1) process.Dispose();
                return process;
            });

        var failed = await PublishAndReadAsync(provider, bus);
        AssertValueless(failed.Fps, FpsId, MetricState.Error);
        Assert.AreEqual("target_identity_failed", failed.Fps.ErrorCode);
        AssertValueless(failed.Application, ApplicationId, MetricState.Error);
        Assert.AreEqual("target_identity_failed", failed.Application.ErrorCode);

        await Task.Delay(TimeSpan.FromMilliseconds(5200));
        var afterRetryWindow = await PublishAndReadAsync(provider, bus);
        AssertValueless(afterRetryWindow.Fps, FpsId, MetricState.Error);
        Assert.AreEqual("target_identity_failed", afterRetryWindow.Fps.ErrorCode);
        AssertValueless(afterRetryWindow.Application, ApplicationId, MetricState.Error);
        Assert.AreEqual("target_identity_failed", afterRetryWindow.Application.ErrorCode);
        Assert.AreEqual(1, processLookups);
    }

    [TestMethod]
    public async Task ExitedTargetStopsSessionAndIsNeverReopenedByPid()
    {
        using var target = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c timeout /t 60 /nobreak >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new AssertFailedException("Could not start the controlled target process.");
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6));
        var openCount = 0;
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, target.Id, process =>
        {
            Assert.AreEqual(target.Id, process.Id);
            openCount++;
            return capture;
        });

        var fresh = await PublishAndReadAsync(provider, bus);
        Assert.AreEqual(MetricState.Fresh, fresh.Application.State);
        Assert.AreEqual("cmd", fresh.Application.Value!.Value.AsString(), true);

        target.Kill(entireProcessTree: true);
        await target.WaitForExitAsync();

        var stopped = await PublishAndReadAsync(provider, bus);
        AssertValueless(stopped.Fps, FpsId, MetricState.Stopped);
        AssertValueless(stopped.FrameTime, FrameTimeId, MetricState.Stopped);
        AssertValueless(stopped.P1, P1Id, MetricState.Stopped);
        AssertValueless(stopped.Application, ApplicationId, MetricState.Stopped);
        Assert.AreEqual(1, openCount);
        Assert.IsTrue(capture.IsDisposed);
        var stoppedAgain = await PublishAndReadAsync(provider, bus);
        AssertValueless(stoppedAgain.Application, ApplicationId, MetricState.Stopped);
        Assert.AreEqual(1, openCount);
    }

    [TestMethod]
    public void P1WarmupRequiresTenSecondsAndRecentDisplayEvidence()
    {
        var first = 10_000L;
        var tenSeconds = Stopwatch.Frequency * 10L;
        var twoSeconds = Stopwatch.Frequency * 2L;

        Assert.IsFalse(PresentMonCapture.HasP1Window(first, first + tenSeconds - 1, first + tenSeconds - 1));
        Assert.IsTrue(PresentMonCapture.HasP1Window(first, first + tenSeconds, first + tenSeconds));
        Assert.IsTrue(PresentMonCapture.HasP1Window(first, first + tenSeconds - twoSeconds,
            first + tenSeconds));
        Assert.IsFalse(PresentMonCapture.HasP1Window(first, first + tenSeconds - twoSeconds - 1,
            first + tenSeconds));
        Assert.IsFalse(PresentMonCapture.HasP1Window(0, first, first + tenSeconds));
    }

    [TestMethod]
    public void DynamicQueryRowsUsePresentMonSixteenByteStride()
    {
        Assert.AreEqual(16UL, PresentMonCapture.GetDynamicQueryRowStride(sizeof(double)));
        Assert.AreEqual(16UL, PresentMonCapture.GetDynamicQueryRowStride(16));
        Assert.AreEqual(32UL, PresentMonCapture.GetDynamicQueryRowStride(17));
    }

    [TestMethod]
    public void P1RegistrationRetryWaitsForDelayAndOnlyRetriesTransientFailures()
    {
        Assert.IsTrue(PresentMonCapture.IsP1RegistrationRetryable(MetricState.Error, "presentmon_api_failure"));
        Assert.IsTrue(PresentMonCapture.IsP1RegistrationRetryable(MetricState.Unavailable, "presentmon_unavailable"));
        Assert.IsFalse(PresentMonCapture.IsP1RegistrationRetryable(MetricState.Error, "presentmon_query_layout"));
        Assert.IsFalse(PresentMonCapture.IsP1RegistrationRetryable(MetricState.Unavailable, null));
        Assert.IsFalse(PresentMonCapture.IsP1RegistrationRetryDue(99, 100));
        Assert.IsTrue(PresentMonCapture.IsP1RegistrationRetryDue(100, 100));
    }

    private static async Task<(MetricSnapshot Fps, MetricSnapshot FrameTime, MetricSnapshot P1, MetricSnapshot Application)> PublishAndReadAsync(
        PresentMonTelemetryProvider provider, MetricBus bus)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fps = ReadNextAsync(bus, FpsId, cancellation.Token);
        var frameTime = ReadNextAsync(bus, FrameTimeId, cancellation.Token);
        var p1 = ReadNextAsync(bus, P1Id, cancellation.Token);
        var application = ReadNextAsync(bus, ApplicationId, cancellation.Token);
        await provider.PublishNextAsync(cancellation.Token);
        return (await fps, await frameTime, await p1, await application);
    }

    private static async Task<MetricSnapshot> ReadNextAsync(
        MetricBus bus, MetricId id, CancellationToken cancellationToken)
    {
        await foreach (var snapshot in bus.Subscribe(id, cancellationToken)) return snapshot;
        throw new AssertFailedException("Metric subscription ended before a snapshot arrived.");
    }

    private static void AssertSnapshot(MetricRegistry registry, MetricSnapshot snapshot, MetricId id,
        double value, string name, string unit, string semanticType)
    {
        Assert.AreEqual(id, snapshot.MetricId);
        Assert.IsTrue(registry.TryGet(id, out var descriptor));
        Assert.AreEqual(name, descriptor!.DisplayName);
        Assert.AreEqual(semanticType, descriptor.SemanticType);
        Assert.AreEqual(MetricValueKind.Double, descriptor.ValueKind);
        Assert.AreEqual(unit, descriptor.Unit);
        Assert.AreEqual(MetricState.Fresh, snapshot.State);
        Assert.AreEqual(MetricConfidence.Measured, snapshot.Confidence);
        Assert.AreEqual(value, snapshot.Value!.Value.AsDouble(), 0.000001);
        Assert.IsNull(snapshot.ErrorCode);
    }

    private static void AssertValueless(MetricSnapshot snapshot, MetricId id, MetricState state)
    {
        Assert.AreEqual(id, snapshot.MetricId);
        Assert.AreEqual(state, snapshot.State);
        Assert.IsNull(snapshot.Value);
        Assert.AreEqual(MetricConfidence.Unknown, snapshot.Confidence);
    }

    private static void AssertApplication(MetricSnapshot snapshot, MetricState state, string? name = null)
    {
        Assert.AreEqual(ApplicationId, snapshot.MetricId);
        Assert.AreEqual(state, snapshot.State);
        Assert.AreEqual("Windows", snapshot.Source);
        if (state == MetricState.Fresh)
        {
            Assert.AreEqual(MetricConfidence.Measured, snapshot.Confidence);
            Assert.AreEqual(name, snapshot.Value!.Value.AsString());
            Assert.IsNull(snapshot.ErrorCode);
        }
        else
        {
            Assert.IsNull(snapshot.Value);
            Assert.AreEqual(MetricConfidence.Unknown, snapshot.Confidence);
        }
    }

    private sealed class FakeCapture(params PresentMonReading[] readings) : IPresentMonCapture
    {
        private readonly Queue<PresentMonReading> _readings = new(readings);

        public Exception? PollError { get; init; }
        public int PollCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public PresentMonReading Poll()
        {
            PollCount++;
            if (PollError is not null) throw PollError;
            return _readings.Dequeue();
        }

        public void Dispose() => IsDisposed = true;
    }

    internal static async Task PublishSyntheticReadingAsync(MetricRegistry registry, MetricBus bus)
    {
        var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6));
        using var provider = new PresentMonTelemetryProvider(registry, bus, Environment.ProcessId,
            _ => capture, static _ => "TelemetryTest");
        await provider.PublishNextAsync();
    }
}
