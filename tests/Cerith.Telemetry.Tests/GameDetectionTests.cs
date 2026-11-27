#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class GameDetectionTests
{
    private static readonly ForegroundSample Game = new(100, "SomeGame", true);
    private static readonly ForegroundSample OtherGame = new(200, "OtherGame", true);
    private static readonly ForegroundSample Desktop = new(300, "SomeEditor", false);

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    [TestMethod]
    public void BrowsersShellAndWindowedAppsNeverBecomeTargets()
    {
        var selector = new GameTargetSelector();
        foreach (var sample in new[] { new ForegroundSample(1, "MSEdge", true), new ForegroundSample(2, "explorer", true), Desktop })
            for (var second = 0; second < 10; second++)
                Assert.IsNull(selector.Next(sample, true, true, Seconds(second)).ProcessId);
    }

    [TestMethod]
    public void FullscreenProcessIsProbedOnlyAfterTheDwell()
    {
        var selector = new GameTargetSelector();
        Assert.IsNull(selector.Next(Game, true, false, Seconds(0)).ProcessId);
        Assert.IsNull(selector.Next(Desktop, true, false, Seconds(1.5)).ProcessId);
        Assert.IsNull(selector.Next(Game, true, false, Seconds(2)).ProcessId);
        Assert.IsNull(selector.Next(Game, true, false, Seconds(3.5)).ProcessId);
        Assert.AreEqual(new GameTarget(100, true), selector.Next(Game, true, false, Seconds(4)));
    }

    [TestMethod]
    public void ThreeSecondsOfFramesProveTheGameAndAltTabKeepsIt()
    {
        var selector = Probing();
        Assert.IsTrue(selector.Next(Game, true, true, Seconds(3)).Probation);
        Assert.IsTrue(selector.Next(Game, true, false, Seconds(4)).Probation);
        Assert.IsTrue(selector.Next(Game, true, true, Seconds(5)).Probation);
        Assert.IsTrue(selector.Next(Game, true, true, Seconds(7)).Probation);
        Assert.AreEqual(new GameTarget(100, false), selector.Next(Game, true, true, Seconds(8)));
        Assert.AreEqual(new GameTarget(100, false), selector.Next(Desktop, true, false, Seconds(600)));
        Assert.AreEqual(new GameTarget(null, false), selector.Next(Desktop, false, false, Seconds(601)));
    }

    [TestMethod]
    public void AnotherFullscreenGameReplacesTheTargetAfterTheDwell()
    {
        var selector = Probing();
        selector.Next(Game, true, true, Seconds(3));
        selector.Next(Game, true, true, Seconds(6));
        Assert.AreEqual(100, selector.Next(OtherGame, true, true, Seconds(7)).ProcessId);
        Assert.AreEqual(new GameTarget(200, true), selector.Next(OtherGame, true, true, Seconds(9)));
    }

    [TestMethod]
    public void AProcessThatShowsNoFramesIsDroppedAndLeftAloneForAMinute()
    {
        var selector = Probing();
        Assert.AreEqual(100, selector.Next(Game, true, false, Seconds(16)).ProcessId);
        Assert.IsNull(selector.Next(Game, true, false, Seconds(17)).ProcessId);
        Assert.IsNull(selector.Next(Game, true, false, Seconds(70)).ProcessId);
        Assert.IsNull(selector.Next(Game, true, false, Seconds(77)).ProcessId);
        Assert.AreEqual(100, selector.Next(Game, true, false, Seconds(79)).ProcessId);
    }

    [TestMethod]
    public async Task LabelAndValuesAreWithheldUntilTheDetectedGameIsProven()
    {
        using var bus = new MetricBus(1);
        var clock = TimeSpan.Zero;
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, null,
            _ => new SteadyCapture(), _ => "SomeGame",
            readForeground: () => new ForegroundSample(Environment.ProcessId, "SomeGame", true),
            clock: () => clock);

        var idle = await PublishAsync(provider, bus);
        Assert.AreEqual(MetricState.Stopped, idle.Fps.State);
        Assert.AreEqual(MetricState.Stopped, idle.Application.State);

        clock = Seconds(2);
        var probing = await PublishAsync(provider, bus);
        Assert.AreEqual(MetricState.Unavailable, probing.Fps.State);
        Assert.IsNull(probing.Fps.Value);
        Assert.AreEqual(MetricState.Unavailable, probing.Application.State);
        Assert.IsNull(probing.Application.Value);

        clock = Seconds(3);
        await PublishAsync(provider, bus);
        clock = Seconds(6);
        var proven = await PublishAsync(provider, bus);
        Assert.AreEqual(MetricState.Fresh, proven.Fps.State);
        Assert.AreEqual("SomeGame", proven.Application.Value!.Value.AsString());
    }

    [TestMethod]
    public async Task AnExplicitPidIgnoresTheForeground()
    {
        using var bus = new MetricBus(1);
        using var provider = new PresentMonTelemetryProvider(new MetricRegistry(), bus, Environment.ProcessId,
            _ => new SteadyCapture(), _ => "Chosen",
            readForeground: () => throw new AssertFailedException("--pid must not probe the foreground."));

        var result = await PublishAsync(provider, bus);

        Assert.AreEqual("Chosen", result.Application.Value!.Value.AsString());
    }

    private static GameTargetSelector Probing()
    {
        var selector = new GameTargetSelector();
        selector.Next(Game, true, false, Seconds(0));
        Assert.AreEqual(new GameTarget(100, true), selector.Next(Game, true, false, Seconds(2)));
        return selector;
    }

    private static async Task<(MetricSnapshot Fps, MetricSnapshot Application)> PublishAsync(
        PresentMonTelemetryProvider provider, MetricBus bus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fps = ReadAsync(bus, new MetricId("game.fps.display"), timeout.Token);
        var application = ReadAsync(bus, new MetricId("game.session.application"), timeout.Token);
        await provider.PublishNextAsync(timeout.Token);
        return (await fps, await application);
    }

    private static async Task<MetricSnapshot> ReadAsync(MetricBus bus, MetricId id, CancellationToken cancellationToken)
    {
        // The bus replays the latest value on subscribe, so wait for one published after this point.
        var after = DateTimeOffset.UtcNow;
        await foreach (var snapshot in bus.Subscribe(id, cancellationToken))
            if (snapshot.ObservedAt >= after) return snapshot;
        throw new AssertFailedException("Metric subscription ended before a snapshot arrived.");
    }

    private sealed class SteadyCapture : IPresentMonCapture
    {
        public PresentMonReading Poll() => new(MetricState.Fresh, 60, 16.6);
        public void Dispose() { }
    }
}
