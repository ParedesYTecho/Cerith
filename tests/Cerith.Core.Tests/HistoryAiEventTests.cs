#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cerith.Core;
using Cerith.Core.History;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Core.Tests;

[TestClass]
public sealed class HistoryAiEventTests
{
    private const string Prefix = "ai.opencode.activity.latest.";
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("cerith-ai-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { Assert.Inconclusive("Temp history files still locked."); }
    }

    private string DbPath => Path.Combine(_dir, "history.db");

    private static MetricSnapshot Fresh(string field, MetricValue value, DateTimeOffset observed) =>
        new(new MetricId(Prefix + field), value, observed, MetricState.Fresh, MetricConfidence.Measured);

    // One collector poll: eight snapshots, one ObservedAt, occurred_at last.
    private static List<MetricSnapshot> Poll(DateTimeOffset observed, DateTimeOffset occurred, long input = 10, string model = "m1") =>
    [
        Fresh("tokens.input", MetricValue.From(input), observed),
        Fresh("tokens.output", MetricValue.From(20L), observed),
        Fresh("tokens.reasoning", MetricValue.From(0L), observed),
        Fresh("tokens.cache.read", MetricValue.From(5L), observed),
        Fresh("tokens.cache.write", MetricValue.From(1L), observed),
        Fresh("model.id", MetricValue.From(model), observed),
        Fresh("model.provider", MetricValue.From("prov"), observed),
        Fresh("occurred_at", MetricValue.From(occurred), observed)
    ];

    [TestMethod]
    public void RecordsOneEventPerDistinctActivityNotPerPoll()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        store.Append(Poll(T0.AddSeconds(5), T0));
        store.Append(Poll(T0.AddSeconds(10), T0));
        store.Append(Poll(T0.AddSeconds(15), T0.AddSeconds(12), input: 99, model: "m2"));

        var events = store.ReadAiEvents(T0.AddMinutes(-1), T0.AddHours(1));

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(new HistoryAiEvent(T0, "opencode", "m1", "prov", 10, 20, 0, 5, 1), events[0]);
        Assert.AreEqual((99L, "m2"), (events[1].InputTokens, events[1].Model));
    }

    [TestMethod]
    public void DoesNotDuplicateTheLastActivityAfterARestart()
    {
        using (var store = HistoryStore.OpenWriter(DbPath))
            store.Append(Poll(T0.AddSeconds(5), T0));

        using var reopened = HistoryStore.OpenWriter(DbPath);
        reopened.Append(Poll(T0.AddSeconds(500), T0));

        Assert.AreEqual(1, reopened.ReadAiEvents(T0.AddMinutes(-1), T0.AddHours(1)).Count);
    }

    [TestMethod]
    public void IncompleteOrNonFreshRoundsRecordNothing()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        var partial = Poll(T0.AddSeconds(5), T0);
        partial.RemoveAt(1);
        store.Append(partial);
        var stale = Poll(T0.AddSeconds(10), T0.AddSeconds(1));
        stale[7] = new MetricSnapshot(new MetricId(Prefix + "occurred_at"), null, T0.AddSeconds(10), MetricState.Unavailable, MetricConfidence.Unknown);
        store.Append(stale);

        Assert.AreEqual(0, store.ReadAiEvents(T0.AddMinutes(-1), T0.AddHours(1)).Count);
    }

    [TestMethod]
    public void ValuesFromADifferentRoundAreNeverMixedIntoAnEvent()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        var mixed = Poll(T0.AddSeconds(10), T0.AddSeconds(3));
        mixed[0] = Fresh("tokens.input", MetricValue.From(777L), T0.AddSeconds(5));
        store.Append(mixed);

        Assert.AreEqual(0, store.ReadAiEvents(T0.AddMinutes(-1), T0.AddHours(1)).Count);
    }

    [TestMethod]
    public void MaintainPrunesEventsPastTheLongestRetention()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        store.Append(Poll(T0.AddSeconds(5), T0));

        store.Maintain(T0.AddDays(11), new HistoryRetention(TimeSpan.FromHours(1), TimeSpan.FromDays(2), TimeSpan.FromDays(10)));

        Assert.AreEqual(0, store.ReadAiEvents(T0.AddMinutes(-1), T0.AddDays(1)).Count);
    }
}
