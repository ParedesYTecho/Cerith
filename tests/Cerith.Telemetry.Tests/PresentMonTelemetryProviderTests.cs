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

    [TestMethod]
    public async Task RegistersAndPublishesTheThreeMeasuredDisplayedMetrics()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 144.5, 6.92,
            P1State: MetricState.Fresh, P1Fps: 35.25));
        using var provider = new PresentMonTelemetryProvider(registry, bus, 123,
            pid => { Assert.AreEqual(123, pid); return capture; });

        var (fps, frameTime, p1) = await PublishAndReadAsync(provider, bus);

        AssertSnapshot(registry, fps, FpsId, 144.5, "Displayed FPS", "FPS", "rate");
        AssertSnapshot(registry, frameTime, FrameTimeId, 6.92, "Displayed Frame Time", "ms", "duration");
        AssertSnapshot(registry, p1, P1Id, 35.25, "Displayed FPS P1 (10 s)", "FPS", "rate");
        Assert.IsTrue(registry.TryGet(P1Id, out var descriptor));
        StringAssert.Contains(descriptor!.Description!, "linearly interpolated 99th percentile");
        StringAssert.Contains(descriptor.Description!, "4096 frame samples");
        Assert.AreEqual("PresentMon", fps.Source);
        Assert.AreEqual(1, capture.PollCount);

        provider.Dispose();
        Assert.IsTrue(capture.IsDisposed);
    }

    [TestMethod]
    public async Task P1WarmupDoesNotInvalidateFreshAverageMetrics()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 144, 6.94));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 123, _ => capture);

        var (fps, frameTime, p1) = await PublishAndReadAsync(provider, bus);

        Assert.AreEqual(MetricState.Fresh, fps.State);
        Assert.AreEqual(MetricState.Fresh, frameTime.State);
        AssertValueless(p1, P1Id, MetricState.Unavailable);
    }

    [TestMethod]
    public async Task P1InvalidValuesDoNotInvalidateTheAverageMetrics()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, 144, 6.94, P1State: MetricState.Fresh, P1Fps: double.NaN),
            new PresentMonReading(MetricState.Fresh, 144, 6.94, P1State: MetricState.Fresh, P1Fps: double.PositiveInfinity),
            new PresentMonReading(MetricState.Fresh, 144, 6.94, P1State: MetricState.Fresh, P1Fps: 0));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 123, _ => capture);

        for (var i = 0; i < 3; i++)
        {
            var (fps, frameTime, p1) = await PublishAndReadAsync(provider, bus);
            Assert.AreEqual(MetricState.Fresh, fps.State);
            Assert.AreEqual(MetricState.Fresh, frameTime.State);
            AssertValueless(p1, P1Id, MetricState.Error);
            Assert.AreEqual("invalid_reading", p1.ErrorCode);
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
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 123, _ => capture);

        foreach (var errorCode in new[] { "presentmon_api_failure", "multiple_swap_chains" })
        {
            var (fps, frameTime, p1) = await PublishAndReadAsync(provider, bus);
            Assert.AreEqual(MetricState.Fresh, fps.State);
            Assert.AreEqual(MetricState.Fresh, frameTime.State);
            AssertValueless(p1, P1Id, MetricState.Error);
            Assert.AreEqual(errorCode, p1.ErrorCode);
        }
    }

    [TestMethod]
    public async Task MissingTargetPublishesStoppedWithoutOpeningCapture()
    {
        using var bus = new MetricBus(1);
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, null,
            _ => throw new AssertFailedException("A missing target must not open PresentMon."));

        var (fps, frameTime, p1) = await PublishAndReadAsync(provider, bus);

        AssertValueless(fps, FpsId, MetricState.Stopped);
        AssertValueless(frameTime, FrameTimeId, MetricState.Stopped);
        AssertValueless(p1, P1Id, MetricState.Stopped);
    }

    [TestMethod]
    public async Task UnavailableAndApiFailureNeverPublishValues()
    {
        using var unavailableBus = new MetricBus(1);
        using var unavailableCapture = new FakeCapture(new PresentMonReading(MetricState.Unavailable));
        using var unavailableProvider = new PresentMonTelemetryProvider(new MetricRegistry(), unavailableBus, 321,
            _ => unavailableCapture);
        var unavailable = await PublishAndReadAsync(unavailableProvider, unavailableBus);
        AssertValueless(unavailable.Fps, FpsId, MetricState.Unavailable);
        AssertValueless(unavailable.FrameTime, FrameTimeId, MetricState.Unavailable);
        AssertValueless(unavailable.P1, P1Id, MetricState.Unavailable);

        using var errorBus = new MetricBus(1);
        using var errorCapture = new FakeCapture(new PresentMonReading(MetricState.Error,
            ErrorCode: "presentmon_api_failure", P1State: MetricState.Error,
            P1ErrorCode: "presentmon_api_failure"));
        using var errorProvider = new PresentMonTelemetryProvider(new MetricRegistry(), errorBus, 654,
            _ => errorCapture);
        var error = await PublishAndReadAsync(errorProvider, errorBus);
        AssertValueless(error.Fps, FpsId, MetricState.Error);
        AssertValueless(error.FrameTime, FrameTimeId, MetricState.Error);
        AssertValueless(error.P1, P1Id, MetricState.Error);
        Assert.AreEqual("presentmon_api_failure", error.Fps.ErrorCode);
        Assert.AreEqual("presentmon_api_failure", error.P1.ErrorCode);
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
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 456, _ => capture);

        var (fps, frameTime, p1) = await PublishAndReadAsync(provider, bus);

        AssertValueless(fps, FpsId, MetricState.Unavailable);
        AssertValueless(frameTime, FrameTimeId, MetricState.Unavailable);
        AssertValueless(p1, P1Id, MetricState.Unavailable);
        Assert.IsTrue(capture.IsDisposed);
    }

    [TestMethod]
    public async Task NonFiniteOrZeroFreshAverageValuesBecomeErrorWithoutPublishingThem()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, double.NaN, 10),
            new PresentMonReading(MetricState.Fresh, 60, double.PositiveInfinity),
            new PresentMonReading(MetricState.Fresh, 0, 16.6));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 987, _ => capture);

        for (var i = 0; i < 3; i++)
        {
            var (fps, frameTime, p1) = await PublishAndReadAsync(provider, bus);
            AssertValueless(fps, FpsId, MetricState.Error);
            AssertValueless(frameTime, FrameTimeId, MetricState.Error);
            Assert.AreEqual("invalid_reading", fps.ErrorCode);
            AssertValueless(p1, P1Id, MetricState.Unavailable);
        }
    }

    [TestMethod]
    public async Task CancellationPreventsCaptureAndBusWork()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 111, _ => capture);
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
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 222, _ =>
        {
            openCount++;
            return capture;
        });

        var fresh = await PublishAndReadAsync(provider, bus);
        Assert.AreEqual(MetricState.Fresh, fresh.Fps.State);
        Assert.AreEqual(MetricState.Fresh, fresh.FrameTime.State);
        Assert.AreEqual(MetricState.Fresh, fresh.P1.State);

        var stopped = await PublishAndReadAsync(provider, bus);
        AssertValueless(stopped.Fps, FpsId, MetricState.Stopped);
        AssertValueless(stopped.FrameTime, FrameTimeId, MetricState.Stopped);
        AssertValueless(stopped.P1, P1Id, MetricState.Stopped);
        Assert.IsTrue(capture.IsDisposed);

        var stoppedAgain = await PublishAndReadAsync(provider, bus);
        AssertValueless(stoppedAgain.Fps, FpsId, MetricState.Stopped);
        AssertValueless(stoppedAgain.FrameTime, FrameTimeId, MetricState.Stopped);
        AssertValueless(stoppedAgain.P1, P1Id, MetricState.Stopped);
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

    private static async Task<(MetricSnapshot Fps, MetricSnapshot FrameTime, MetricSnapshot P1)> PublishAndReadAsync(
        PresentMonTelemetryProvider provider, MetricBus bus)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fps = ReadNextAsync(bus, FpsId, cancellation.Token);
        var frameTime = ReadNextAsync(bus, FrameTimeId, cancellation.Token);
        var p1 = ReadNextAsync(bus, P1Id, cancellation.Token);
        await provider.PublishNextAsync(cancellation.Token);
        return (await fps, await frameTime, await p1);
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
}
