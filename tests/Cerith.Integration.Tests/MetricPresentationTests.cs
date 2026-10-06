#nullable enable

using System;
using System.IO;
using Cerith.Core;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class MetricPresentationTests
{
    private static readonly MetricPresentationSpec Percent =
        new(new MetricId("example.percent"), "Example", "0.0", "%");

    [TestMethod]
    public void FreshNonZeroPresentsValueAndMeasuredConfidence()
    {
        var result = Present(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(42.25));
        Assert.AreEqual("42.3 %", result.ValueText);
        Assert.AreEqual("Fresh", result.StateText);
        Assert.AreEqual("Measured", result.ConfidenceText);
    }

    [TestMethod]
    public void FreshRealZeroRemainsZero()
    {
        var result = Present(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(0d));
        Assert.AreEqual("0.0 %", result.ValueText);
    }

    [TestMethod]
    public void StaleRetainsLastValueAndEstimatedConfidence()
    {
        var result = Present(MetricState.Stale, MetricConfidence.Estimated, MetricValue.From(8d));
        Assert.AreEqual("8.0 %", result.ValueText);
        Assert.AreEqual("Stale", result.StateText);
        Assert.AreEqual("Estimated", result.ConfidenceText);
    }

    [TestMethod]
    [DataRow(MetricState.Unavailable, "Unavailable")]
    [DataRow(MetricState.Stopped, "Stopped")]
    public void ValuelessStatesNeverBecomeZero(MetricState state, string expected)
    {
        var result = Present(state, MetricConfidence.Unknown, null);
        Assert.AreEqual("—", result.ValueText);
        Assert.AreEqual(expected, result.StateText);
        Assert.AreEqual("Unknown", result.ConfidenceText);
    }

    [TestMethod]
    public void ErrorIsValuelessAndKeepsSanitizedCode()
    {
        var result = MetricPresentation.Create(Percent,
            Snapshot(MetricState.Error, MetricConfidence.Unknown, null, "source", "provider_failure"));
        Assert.AreEqual("—", result.ValueText);
        Assert.AreEqual("Error · provider_failure", result.StateText);
    }

    [TestMethod]
    public void UnlimitedDoesNotBecomePercentage()
    {
        var result = Present(MetricState.Unlimited, MetricConfidence.Unknown, null);
        Assert.AreEqual("Unlimited", result.ValueText);
        Assert.AreEqual("Unlimited", result.StateText);
    }

    [TestMethod]
    [DataRow(MetricConfidence.Measured, "Measured")]
    [DataRow(MetricConfidence.Estimated, "Estimated")]
    [DataRow(MetricConfidence.Unknown, "Unknown")]
    public void ValueConfidenceRemainsVisible(MetricConfidence confidence, string expected)
    {
        Assert.AreEqual(expected, Present(MetricState.Fresh, confidence, MetricValue.From(1d)).ConfidenceText);
    }

    [TestMethod]
    public void SourceDoesNotAffectPresentation()
    {
        var first = MetricPresentation.Create(Percent,
            Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(72d), "ProviderA"));
        var second = MetricPresentation.Create(Percent,
            Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(72d), "ProviderB"));
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void MismatchedMetricIdIsRejected()
    {
        var snapshot = new MetricSnapshot(new MetricId("other.metric"), MetricValue.From(1d), DateTimeOffset.UtcNow,
            MetricState.Fresh, MetricConfidence.Measured);
        Assert.Throws<InvalidDataException>(() => MetricPresentation.Create(Percent, snapshot));
    }

    private static MetricPresentationResult Present(MetricState state, MetricConfidence confidence, MetricValue? value) =>
        MetricPresentation.Create(Percent, Snapshot(state, confidence, value, "source"));

    private static MetricSnapshot Snapshot(MetricState state, MetricConfidence confidence, MetricValue? value,
        string? source, string? errorCode = null) =>
        new(Percent.MetricId, value, DateTimeOffset.UtcNow, state, confidence, source, errorCode);
}
