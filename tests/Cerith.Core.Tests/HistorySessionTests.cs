#nullable enable

using System;
using System.IO;
using System.Linq;
using Cerith.Core;
using Cerith.Core.History;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Core.Tests;

[TestClass]
public sealed class HistorySessionTests
{
    private static readonly MetricId Fps = new("game.fps.display");
    private static readonly MetricId Temp = new("gpu.0.temperature.core");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("cerith-sessions-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { Assert.Inconclusive("Temp history files still locked."); }
    }

    private string DbPath => Path.Combine(_dir, "history.db");

    private static MetricSnapshot Fresh(MetricId id, MetricValue value, DateTimeOffset at) =>
        new(id, value, at, MetricState.Fresh, MetricConfidence.Measured);

    private static MetricSnapshot App(string name, DateTimeOffset at) => Fresh(HistoryStore.SessionMetric, MetricValue.From(name), at);

    private static MetricSnapshot AppStopped(DateTimeOffset at) =>
        new(HistoryStore.SessionMetric, null, at, MetricState.Stopped, MetricConfidence.Unknown);

    [TestMethod]
    public void RecordsSessionBoundsAndStatisticsOnlyForValuesDuringTheSession()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        store.Append([
            Fresh(Fps, MetricValue.From(500.0), T0),
            App("game", T0.AddSeconds(10)),
            Fresh(Fps, MetricValue.From(60.0), T0.AddSeconds(11)),
            Fresh(Fps, MetricValue.From(100.0), T0.AddSeconds(12)),
            Fresh(Temp, MetricValue.From(70L), T0.AddSeconds(12)),
            App("game", T0.AddSeconds(13)),
            AppStopped(T0.AddSeconds(20)),
            Fresh(Fps, MetricValue.From(999.0), T0.AddSeconds(21))
        ]);

        var session = store.ReadSessions(T0, T0.AddHours(1)).Single();

        Assert.AreEqual("game", session.Label);
        Assert.AreEqual(T0.AddSeconds(10), session.Start);
        Assert.AreEqual(T0.AddSeconds(20), session.End);
        var fps = session.Stats.Single(s => s.MetricId == Fps);
        Assert.AreEqual((2L, 60.0, 100.0, 80.0), (fps.Count, fps.Min, fps.Max, fps.Average));
        Assert.AreEqual(70.0, session.Stats.Single(s => s.MetricId == Temp).Average);
    }

    [TestMethod]
    public void StatisticsAccumulateAcrossBatchesAndALabelChangeStartsANewSession()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        store.Append([App("one", T0), Fresh(Fps, MetricValue.From(30.0), T0.AddSeconds(1))]);
        store.Append([Fresh(Fps, MetricValue.From(50.0), T0.AddSeconds(2))]);
        store.Append([App("two", T0.AddSeconds(5)), Fresh(Fps, MetricValue.From(144.0), T0.AddSeconds(6))]);

        var sessions = store.ReadSessions(T0, T0.AddHours(1));

        Assert.AreEqual(2, sessions.Count);
        var two = sessions[0];
        var one = sessions[1];
        Assert.AreEqual(T0.AddSeconds(5), one.End);
        Assert.AreEqual(40.0, one.Stats.Single(s => s.MetricId == Fps).Average);
        Assert.IsNull(two.End);
        Assert.AreEqual(144.0, two.Stats.Single(s => s.MetricId == Fps).Max);
    }

    [TestMethod]
    public void ASessionLeftOpenByADeadWriterIsEndedAtTheLastInstantSeen()
    {
        var store = HistoryStore.OpenWriter(DbPath);
        store.Append([App("game", T0), Fresh(Fps, MetricValue.From(60.0), T0.AddSeconds(30))]);
        Assert.IsNull(store.ReadSessions(T0, T0.AddHours(1)).Single().End);
        store.Dispose();

        using var reopened = HistoryStore.OpenWriter(DbPath);

        Assert.AreEqual(T0.AddSeconds(30), reopened.ReadSessions(T0, T0.AddHours(1)).Single().End);
    }

    [TestMethod]
    public void ANonStringOrNonFreshApplicationValueNeverOpensASession()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        store.Append([
            new MetricSnapshot(HistoryStore.SessionMetric, null, T0, MetricState.Unavailable, MetricConfidence.Unknown),
            new MetricSnapshot(HistoryStore.SessionMetric, null, T0.AddSeconds(1), MetricState.Error, MetricConfidence.Unknown, errorCode: "invalid_application_name")
        ]);

        Assert.AreEqual(0, store.ReadSessions(T0, T0.AddHours(1)).Count);
    }
}
