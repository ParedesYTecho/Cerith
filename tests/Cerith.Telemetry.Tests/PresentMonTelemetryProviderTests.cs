#nullable enable

using System;
using System.Collections.Generic;
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

    [TestMethod]
    public async Task RegistersAndPublishesTheTwoMeasuredDisplayedMetrics()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 144.5, 6.92));
        using var provider = new PresentMonTelemetryProvider(registry, bus, 123,
            pid => { Assert.AreEqual(123, pid); return capture; });

        var (fps, frameTime) = await PublishAndReadAsync(provider, bus);

        AssertSnapshot(registry, fps, FpsId, 144.5, "Displayed FPS", "FPS", "rate");
        AssertSnapshot(registry, frameTime, FrameTimeId, 6.92, "Displayed Frame Time", "ms", "duration");
        Assert.AreEqual("PresentMon", fps.Source);
        Assert.AreEqual(1, capture.PollCount);

        provider.Dispose();
        Assert.IsTrue(capture.IsDisposed);
    }

    [TestMethod]
    public async Task MissingTargetPublishesStoppedWithoutOpeningCapture()
    {
        using var bus = new MetricBus(1);
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, null,
            _ => throw new AssertFailedException("A missing target must not open PresentMon."));

        var (fps, frameTime) = await PublishAndReadAsync(provider, bus);

        AssertValueless(fps, FpsId, MetricState.Stopped);
        AssertValueless(frameTime, FrameTimeId, MetricState.Stopped);
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

        using var errorBus = new MetricBus(1);
        using var errorCapture = new FakeCapture(new PresentMonReading(MetricState.Error,
            ErrorCode: "presentmon_api_failure"));
        using var errorProvider = new PresentMonTelemetryProvider(new MetricRegistry(), errorBus, 654,
            _ => errorCapture);
        var error = await PublishAndReadAsync(errorProvider, errorBus);
        AssertValueless(error.Fps, FpsId, MetricState.Error);
        AssertValueless(error.FrameTime, FrameTimeId, MetricState.Error);
        Assert.AreEqual("presentmon_api_failure", error.Fps.ErrorCode);
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

        var (fps, frameTime) = await PublishAndReadAsync(provider, bus);

        AssertValueless(fps, FpsId, MetricState.Unavailable);
        AssertValueless(frameTime, FrameTimeId, MetricState.Unavailable);
        Assert.IsTrue(capture.IsDisposed);
    }

    [TestMethod]
    public async Task NonFiniteOrZeroFreshValuesBecomeErrorWithoutPublishingThem()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, double.NaN, 10),
            new PresentMonReading(MetricState.Fresh, 60, double.PositiveInfinity),
            new PresentMonReading(MetricState.Fresh, 0, 16.6));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 987,
            _ => capture);

        foreach (var expected in new[] { "invalid_reading", "invalid_reading", "invalid_reading" })
        {
            var pair = await PublishAndReadAsync(provider, bus);
            AssertValueless(pair.Fps, FpsId, MetricState.Error);
            AssertValueless(pair.FrameTime, FrameTimeId, MetricState.Error);
            Assert.AreEqual(expected, pair.Fps.ErrorCode);
        }
    }

    [TestMethod]
    public async Task CancellationPreventsCaptureAndBusWork()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(new PresentMonReading(MetricState.Fresh, 60, 16.6));
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 111,
            _ => capture);
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
    public async Task StoppedCaptureClearsFreshValuesAndIsNotReopened()
    {
        using var bus = new MetricBus(1);
        using var capture = new FakeCapture(
            new PresentMonReading(MetricState.Fresh, 60, 16.6),
            new PresentMonReading(MetricState.Stopped));
        var openCount = 0;
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, 222, _ =>
        {
            openCount++;
            return capture;
        });

        var fresh = await PublishAndReadAsync(provider, bus);
        Assert.AreEqual(MetricState.Fresh, fresh.Fps.State);
        Assert.AreEqual(MetricState.Fresh, fresh.FrameTime.State);

        var stopped = await PublishAndReadAsync(provider, bus);
        AssertValueless(stopped.Fps, FpsId, MetricState.Stopped);
        AssertValueless(stopped.FrameTime, FrameTimeId, MetricState.Stopped);
        Assert.IsTrue(capture.IsDisposed);

        var stoppedAgain = await PublishAndReadAsync(provider, bus);
        AssertValueless(stoppedAgain.Fps, FpsId, MetricState.Stopped);
        AssertValueless(stoppedAgain.FrameTime, FrameTimeId, MetricState.Stopped);
        Assert.AreEqual(1, openCount);
    }

    private static async Task<(MetricSnapshot Fps, MetricSnapshot FrameTime)> PublishAndReadAsync(
        PresentMonTelemetryProvider provider, MetricBus bus)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fps = ReadNextAsync(bus, FpsId, cancellation.Token);
        var frameTime = ReadNextAsync(bus, FrameTimeId, cancellation.Token);
        await provider.PublishNextAsync(cancellation.Token);
        return (await fps, await frameTime);
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
