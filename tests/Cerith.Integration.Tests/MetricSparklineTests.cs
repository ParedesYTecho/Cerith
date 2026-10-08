#nullable enable

using System;
using Cerith.Core;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class MetricSparklineTests
{
    private static readonly MetricId Id = new("cpu.total.usage");
    private static readonly MetricRangeSpec Spec = new(new(Id, "0.0"), 0, 100);
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    private static MetricSnapshot Sample(int seconds, double value, MetricState state = MetricState.Fresh,
        MetricConfidence confidence = MetricConfidence.Measured, string? source = null) =>
        new(Id, MetricValue.From(value), Start.AddSeconds(seconds), state, confidence, source);

    [TestMethod]
    public void ActualInstantsReplaceEqualOffsetsAndResetBackwardTime()
    {
        var sequence = new MetricSparklineSequence(Spec);
        sequence.Apply(null, Sample(0, 10));
        sequence.Apply(null, Sample(9, 30));
        sequence.Apply(null, Sample(10, 40));
        Assert.AreEqual(3, sequence.Samples.Count);
        Assert.AreEqual(9d, (sequence.Samples[1].Time - sequence.Samples[0].Time).TotalSeconds);
        sequence.Apply(null, new(Id, MetricValue.From(60d), Start.AddSeconds(10).ToOffset(TimeSpan.FromHours(1)),
            MetricState.Fresh, MetricConfidence.Measured));
        Assert.AreEqual(3, sequence.Samples.Count);
        Assert.AreEqual(.6, sequence.Samples[^1].Fraction, .000001);
        sequence.Apply(null, Sample(3, 20));
        Assert.AreEqual(1, sequence.Samples.Count);
    }

    [TestMethod]
    public void CapAndZeroEndpoints()
    {
        var sequence = new MetricSparklineSequence(Spec);
        for (var i = 0; i < 65; i++) sequence.Apply(null, Sample(i * i, 0));
        Assert.AreEqual(60, sequence.Samples.Count);
        Assert.AreEqual(25d, (sequence.Samples[0].Time - Start).TotalSeconds);
        Assert.AreEqual(0d, sequence.Samples[^1].Fraction);
        Assert.AreEqual(4096 - 25, sequence.Span.TotalSeconds);
    }

    [TestMethod]
    public void StaleFreezesThenFreshResetsAndValuelessClears()
    {
        var sequence = new MetricSparklineSequence(Spec);
        sequence.Apply(null, Sample(0, 0));
        sequence.Apply(null, Sample(1, 100));
        sequence.Apply(null, Sample(2, 100, MetricState.Stale));
        Assert.IsTrue(sequence.IsStale);
        Assert.AreEqual(2, sequence.Samples.Count);
        sequence.Apply(null, Sample(3, 25));
        Assert.AreEqual(1, sequence.Samples.Count);
        sequence.Apply(null, new(Id, null, Start, MetricState.Unavailable, MetricConfidence.Unknown));
        Assert.AreEqual(0, sequence.Samples.Count);
    }

    [TestMethod]
    public void ChangedSourceConfidenceAndMetadataReset()
    {
        var sequence = new MetricSparklineSequence(Spec);
        var descriptor = new MetricDescriptor(Id, "CPU", "usage", MetricValueKind.Double, unit: "%");
        sequence.Apply(descriptor, Sample(0, 1, source: "a"));
        sequence.Apply(descriptor, Sample(1, 2, source: "b"));
        Assert.AreEqual(1, sequence.Samples.Count);
        sequence.Apply(descriptor, Sample(2, 3, MetricState.Fresh, MetricConfidence.Estimated, "b"));
        Assert.AreEqual(1, sequence.Samples.Count);
        sequence.Apply(new(Id, "CPU", "usage", MetricValueKind.Double, unit: "points"),
            Sample(3, 4, MetricState.Fresh, MetricConfidence.Estimated, "b"));
        Assert.AreEqual(1, sequence.Samples.Count);
    }

    [TestMethod]
    public void InvalidValuesAndRangeClear()
    {
        var sequence = new MetricSparklineSequence(Spec);
        foreach (var invalid in new[] { -1d, 101d, double.NaN, double.PositiveInfinity })
        {
            sequence.Apply(null, Sample(0, 20));
            sequence.Apply(null, Sample(1, invalid));
            Assert.AreEqual(0, sequence.Samples.Count);
        }
        sequence.Apply(null, Sample(0, 20));
        sequence.Apply(null, new(Id, MetricValue.From("text"), Start, MetricState.Fresh, MetricConfidence.Measured));
        Assert.AreEqual(0, sequence.Samples.Count);
    }

    [TestMethod]
    public void StaleRequiresValidUnchangedSemanticsAndNeverStartsHistory()
    {
        var sequence = new MetricSparklineSequence(Spec);
        var descriptor = new MetricDescriptor(Id, "CPU", "usage", MetricValueKind.Double, unit: "%");
        sequence.Apply(descriptor, Sample(0, 1, MetricState.Stale));
        Assert.AreEqual(0, sequence.Samples.Count);
        sequence.Apply(descriptor, Sample(0, 10));
        sequence.Apply(descriptor, Sample(1, 20));
        sequence.Apply(descriptor, Sample(2, 20, MetricState.Stale));
        Assert.IsTrue(sequence.IsStale);
        Assert.AreEqual(2, sequence.Samples.Count);
        sequence.Apply(descriptor, Sample(3, 101, MetricState.Stale));
        Assert.AreEqual(0, sequence.Samples.Count);
        sequence.Apply(descriptor, Sample(4, 20));
        sequence.Apply(descriptor, Sample(5, 30));
        sequence.Apply(descriptor, Sample(6, 30, MetricState.Stale, source: "other"));
        Assert.AreEqual(0, sequence.Samples.Count);
        sequence.Apply(descriptor, Sample(7, 20));
        sequence.Apply(new(Id, "CPU", "different", MetricValueKind.Double, unit: "%"),
            Sample(8, 20, MetricState.Stale));
        Assert.AreEqual(0, sequence.Samples.Count);
        sequence.Apply(descriptor, Sample(9, 20));
        sequence.Apply(descriptor, Sample(10, double.NaN, MetricState.Stale));
        Assert.AreEqual(0, sequence.Samples.Count);
    }

    [TestMethod]
    public void DuplicateDoesNotRequestGeometryUpdateButReplacementDoes()
    {
        var sequence = new MetricSparklineSequence(Spec);
        Assert.IsTrue(sequence.Apply(null, Sample(0, 0)));
        Assert.IsTrue(sequence.Apply(null, Sample(1, 20)));
        Assert.IsFalse(sequence.Apply(null, Sample(1, 20)));
        Assert.IsFalse(sequence.Apply(null, Sample(2, 20, MetricState.Stale)));
        Assert.IsTrue(sequence.IsStale);
        Assert.IsTrue(sequence.Apply(null, Sample(3, 20)));
        Assert.AreEqual(1, sequence.Samples.Count);
        Assert.IsFalse(sequence.Apply(null, Sample(3, 20)));
        Assert.IsTrue(sequence.Apply(null, Sample(3, 40)));
        Assert.AreEqual(.4, sequence.Samples[^1].Fraction, .000001);
        Assert.IsTrue(sequence.Apply(null, null));
        Assert.IsFalse(sequence.Apply(null, null));
    }
}
