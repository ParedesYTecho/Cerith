#nullable enable

using System;
using System.IO;
using System.Linq;
using Cerith.Core;
using Cerith.Core.History;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Core.Tests;

[TestClass]
public sealed class HistoryStoreTests
{
    private static readonly MetricId Cpu = new("cpu.0.load.total");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("cerith-history-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { Assert.Inconclusive("Temp history files still locked."); }
    }

    private string DbPath => Path.Combine(_dir, "history.db");

    private static MetricSnapshot Fresh(MetricId id, MetricValue value, DateTimeOffset at, MetricConfidence confidence = MetricConfidence.Measured) =>
        new(id, value, at, MetricState.Fresh, confidence);

    private static MetricSnapshot Gap(MetricId id, DateTimeOffset at, MetricState state) =>
        new(id, null, at, state, MetricConfidence.Unknown);

    [TestMethod]
    public void RoundTripsEveryValueKindWithStateAndConfidence()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        var ids = new[] { "d", "i", "m", "b", "s", "t", "o" }.Select(n => new MetricId("test." + n)).ToArray();
        var values = new[]
        {
            MetricValue.From(12.5), MetricValue.From(9_007_199_254_740_993L), MetricValue.From(1234.5678901234567890123m),
            MetricValue.From(true), MetricValue.From("ok"), MetricValue.From(TimeSpan.FromSeconds(90)),
            MetricValue.From(T0.AddMinutes(3))
        };
        var result = store.Append(ids.Select((id, i) =>
            Fresh(id, values[i], T0.AddSeconds(i), i == 0 ? MetricConfidence.Estimated : MetricConfidence.Measured)).ToArray());

        Assert.AreEqual(new HistoryAppendResult(7, 0), result);
        for (var i = 0; i < ids.Length; i++)
        {
            var point = store.ReadRaw(ids[i], T0, T0.AddHours(1)).Single();
            Assert.AreEqual(values[i], point.Value, ids[i].Value);
            Assert.AreEqual(MetricState.Fresh, point.State);
            Assert.AreEqual(i == 0 ? MetricConfidence.Estimated : MetricConfidence.Measured, point.Confidence);
        }
    }

    [TestMethod]
    public void StoresMissingValuesAsStateNeverAsZeroAndOnlyOnChange()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        store.Append([
            Fresh(Cpu, MetricValue.From(1.0), T0),
            Gap(Cpu, T0.AddSeconds(1), MetricState.Unavailable),
            Gap(Cpu, T0.AddSeconds(2), MetricState.Unavailable),
            Gap(Cpu, T0.AddSeconds(3), MetricState.Unavailable),
            Fresh(Cpu, MetricValue.From(2.0), T0.AddSeconds(4)),
            Gap(Cpu, T0.AddSeconds(5), MetricState.Error)
        ]);

        var points = store.ReadRaw(Cpu, T0, T0.AddHours(1));

        CollectionAssert.AreEqual(
            new[] { MetricState.Fresh, MetricState.Unavailable, MetricState.Fresh, MetricState.Error },
            points.Select(p => p.State).ToArray());
        Assert.IsNull(points[1].Value);
        Assert.IsNull(points[3].Value);
    }

    [TestMethod]
    public void RejectsNonFiniteValuesAndKindChangesAndCountsThem()
    {
        using var store = HistoryStore.OpenWriter(DbPath);

        var result = store.Append([
            Fresh(Cpu, MetricValue.From(1.0), T0),
            Fresh(Cpu, MetricValue.From(double.NaN), T0.AddSeconds(1)),
            Fresh(Cpu, MetricValue.From(5L), T0.AddSeconds(2))
        ]);

        Assert.AreEqual(new HistoryAppendResult(1, 2), result);
        Assert.AreEqual(1, store.ReadRaw(Cpu, T0, T0.AddHours(1)).Count);
    }

    [TestMethod]
    public void ReaderSeesCommittedDataWhileWriterIsOpenAndCannotWrite()
    {
        using var writer = HistoryStore.OpenWriter(DbPath);
        writer.Append([Fresh(Cpu, MetricValue.From(7.0), T0)]);
        using var reader = HistoryStore.OpenReader(DbPath);

        Assert.AreEqual(MetricValue.From(7.0), reader.ReadRaw(Cpu, T0, T0.AddHours(1)).Single().Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.Append([Fresh(Cpu, MetricValue.From(8.0), T0.AddSeconds(1))]));
    }

    [TestMethod]
    public void DataSurvivesReopen()
    {
        using (var store = HistoryStore.OpenWriter(DbPath))
            store.Append([Fresh(Cpu, MetricValue.From(3.0), T0)]);

        using var reopened = HistoryStore.OpenWriter(DbPath);
        reopened.Append([Fresh(Cpu, MetricValue.From(4.0), T0.AddSeconds(1))]);

        Assert.AreEqual(2, reopened.ReadRaw(Cpu, T0, T0.AddHours(1)).Count);
    }

    [TestMethod]
    public void MaintainRollsUpFreshNumericOnlyThenPrunesRawAndKeepsRollups()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        var staleOnly = new MetricId("test.stale");
        store.Append([
            Fresh(Cpu, MetricValue.From(10.0), T0.AddSeconds(5)),
            Fresh(Cpu, MetricValue.From(30.0), T0.AddSeconds(35)),
            new MetricSnapshot(Cpu, MetricValue.From(999.0), T0.AddSeconds(40), MetricState.Stale, MetricConfidence.Measured),
            Fresh(Cpu, MetricValue.From(50.0), T0.AddMinutes(1).AddSeconds(5)),
            new MetricSnapshot(staleOnly, MetricValue.From(1.0), T0, MetricState.Stale, MetricConfidence.Measured)
        ]);

        store.Maintain(T0.AddHours(3), new HistoryRetention(TimeSpan.FromHours(1), TimeSpan.FromDays(1), TimeSpan.FromDays(10)));

        var minutes = store.ReadRollup(Cpu, TimeSpan.FromMinutes(1), T0, T0.AddHours(1));
        Assert.AreEqual(2, minutes.Count);
        Assert.AreEqual(2, minutes[0].Count);
        Assert.AreEqual(10.0, minutes[0].Min);
        Assert.AreEqual(30.0, minutes[0].Max);
        Assert.AreEqual(20.0, minutes[0].Average);
        var hours = store.ReadRollup(Cpu, TimeSpan.FromHours(1), T0, T0.AddHours(2));
        Assert.AreEqual(3, hours.Single().Count);
        Assert.AreEqual(0, store.ReadRollup(staleOnly, TimeSpan.FromMinutes(1), T0, T0.AddHours(1)).Count);
        Assert.AreEqual(0, store.ReadRaw(Cpu, T0, T0.AddHours(1)).Count, "raw older than retention is gone once rolled up");
    }

    [TestMethod]
    public void MaintainNeverDeletesRawDataThatIsNotRolledUpYet()
    {
        using var store = HistoryStore.OpenWriter(DbPath);
        var now = T0.AddHours(1);
        store.Append([Fresh(Cpu, MetricValue.From(1.0), now.AddMinutes(-2))]);

        store.Maintain(now, new HistoryRetention(TimeSpan.Zero, TimeSpan.FromDays(1), TimeSpan.FromDays(10)));

        Assert.AreEqual(1, store.ReadRaw(Cpu, T0, T0.AddHours(2)).Count, "within the rollup delay the raw row must be kept");
    }

    [TestMethod]
    public void RefusesAndPreservesAForeignFile()
    {
        File.WriteAllText(DbPath, "not a database");

        Assert.ThrowsExactly<IOException>(() => HistoryStore.OpenWriter(DbPath));
        Assert.AreEqual("not a database", File.ReadAllText(DbPath));
    }
}
