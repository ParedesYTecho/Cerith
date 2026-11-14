#nullable enable

using System;
using System.IO;
using System.Linq;
using Cerith.Core;
using Cerith.Core.History;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Core.Tests;

[TestClass]
public sealed class HistorySeriesTests
{
    private static readonly MetricId Cpu = new("cpu.total.usage");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("cerith-series-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { Assert.Inconclusive("Temp history files still locked."); }
    }

    private string DbPath => Path.Combine(_dir, "history.db");

    private static MetricSnapshot Fresh(double value, DateTimeOffset at) =>
        new(Cpu, MetricValue.From(value), at, MetricState.Fresh, MetricConfidence.Measured);

    [TestMethod]
    public void ShortRangesUseRawFreshPointsAndSkipGaps()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        store.Append([
            Fresh(10, T0),
            new MetricSnapshot(Cpu, null, T0.AddSeconds(1), MetricState.Unavailable, MetricConfidence.Unknown),
            Fresh(30, T0.AddSeconds(2))
        ]);

        var series = store.ReadSeries(Cpu, T0, T0.AddHours(1));

        Assert.AreEqual(HistoryResolution.Raw, series.Resolution);
        CollectionAssert.AreEqual(new[] { 10.0, 30.0 }, series.Points.Select(p => p.Average).ToArray());
    }

    [TestMethod]
    public void MinuteSeriesCombinesRollupsWithTheNotYetRolledTail()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        var now = T0.AddHours(10);
        store.Append([Fresh(10, T0.AddHours(5)), Fresh(30, T0.AddHours(5).AddSeconds(10)), Fresh(50, now.AddMinutes(-1))]);
        store.Maintain(now, HistoryRetention.Default);

        var series = store.ReadSeries(Cpu, T0, now);

        Assert.AreEqual(HistoryResolution.Minute, series.Resolution);
        Assert.AreEqual(2, series.Points.Count);
        Assert.AreEqual((10.0, 30.0, 20.0), (series.Points[0].Min, series.Points[0].Max, series.Points[0].Average));
        Assert.AreEqual(50.0, series.Points[1].Average, "tail inside the rollup delay comes from raw samples");
    }

    [TestMethod]
    public void HourSeriesAggregatesAcrossTiersAndNonNumericMetricsAreEmpty()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        var now = T0.AddDays(10);
        store.Append([
            Fresh(10, T0.AddDays(1)), Fresh(20, T0.AddDays(1).AddMinutes(10)),
            new MetricSnapshot(new MetricId("game.session.application"), MetricValue.From("g"), now, MetricState.Fresh, MetricConfidence.Measured)
        ]);
        store.Maintain(now, new HistoryRetention(TimeSpan.FromHours(1), TimeSpan.FromDays(30), TimeSpan.FromDays(100)));

        var series = store.ReadSeries(Cpu, T0, now);

        Assert.AreEqual(HistoryResolution.Hour, series.Resolution);
        Assert.AreEqual(15.0, series.Points.Single().Average);
        Assert.AreEqual(0, store.ReadSeries(HistoryStore.SessionMetric, T0, now).Points.Count);
        CollectionAssert.AreEqual(new[] { "cpu.total.usage", "game.session.application" }, store.ReadMetrics().Select(m => m.Value).ToArray());
    }
}
