#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.Core.History;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class HistoryRecorderTests
{
    private static readonly MetricId Cpu = new("cpu.0.load.total");
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("cerith-recorder-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { Assert.Inconclusive("Temp history files still locked."); }
    }

    private static MetricSnapshot Sample(double value, DateTimeOffset at) =>
        new(Cpu, MetricValue.From(value), at, MetricState.Fresh, MetricConfidence.Measured);

    [TestMethod]
    public async Task RecordsPublishedSnapshotsEvenWithoutSubscribers()
    {
        var path = Path.Combine(_dir, "history.db");
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var bus = new MetricBus(1);
        using (var recorder = new HistoryRecorder(bus, HistoryStore.OpenWriter(path), HistoryRetention.Default, TimeSpan.FromSeconds(30), 16))
        {
            await bus.PublishAsync(Sample(1, at));
            await bus.PublishAsync(Sample(2, at.AddSeconds(1)));
            recorder.Flush();
        }

        using var reader = HistoryStore.OpenReader(path);
        var points = reader.ReadRaw(Cpu, at.AddSeconds(-1), at.AddMinutes(1));
        CollectionAssert.AreEqual(new[] { 1.0, 2.0 }, points.Select(p => p.Value!.Value.AsDouble()).ToArray());
    }

    [TestMethod]
    public async Task DropsAndCountsOnOverflowWithoutBlockingThePublisher()
    {
        using var bus = new MetricBus(1);
        using var recorder = new HistoryRecorder(bus, HistoryStore.OpenWriter(Path.Combine(_dir, "history.db")),
            HistoryRetention.Default, TimeSpan.FromSeconds(30), capacity: 2);
        var at = DateTimeOffset.UtcNow;

        var publishing = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++) await bus.PublishAsync(Sample(i, at.AddSeconds(i)));
        });

        Assert.AreSame(publishing, await Task.WhenAny(publishing, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.AreEqual(3, recorder.Dropped);
    }

    [TestMethod]
    public async Task RunFlushesPendingSnapshotsWhenStopped()
    {
        var path = Path.Combine(_dir, "history.db");
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var bus = new MetricBus(1);
        using var recorder = new HistoryRecorder(bus, HistoryStore.OpenWriter(path), HistoryRetention.Default, TimeSpan.FromMinutes(10), 16);
        using var stop = new CancellationTokenSource();
        var run = recorder.RunAsync(stop.Token);

        await bus.PublishAsync(Sample(9, at));
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);

        using var reader = HistoryStore.OpenReader(path);
        Assert.AreEqual(1, reader.ReadRaw(Cpu, at.AddSeconds(-1), at.AddMinutes(1)).Count);
    }

    [TestMethod]
    public async Task DisposeFlushesSnapshotsPublishedAfterTheLoopStopped()
    {
        var path = Path.Combine(_dir, "history.db");
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var bus = new MetricBus(1);
        var recorder = new HistoryRecorder(bus, HistoryStore.OpenWriter(path), HistoryRetention.Default, TimeSpan.FromMinutes(10), 16);

        await bus.PublishAsync(Sample(4, at));
        recorder.Dispose();

        using var reader = HistoryStore.OpenReader(path);
        Assert.AreEqual(1, reader.ReadRaw(Cpu, at.AddSeconds(-1), at.AddMinutes(1)).Count);
    }
}
