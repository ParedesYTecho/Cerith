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
    private static readonly MetricDescriptor PercentDescriptor =
        new(new MetricId("example.percent"), "Example percentage", "percentage", MetricValueKind.Double, unit: "%");
    private static readonly MetricPresentationSpec Percent =
        new(PercentDescriptor.Id, "0.0");

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
        var result = MetricPresentation.Create(Percent, PercentDescriptor,
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
        var first = MetricPresentation.Create(Percent, PercentDescriptor,
            Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(72d), "ProviderA"));
        var second = MetricPresentation.Create(Percent, PercentDescriptor,
            Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(72d), "ProviderB"));
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void MismatchedMetricIdIsRejected()
    {
        var snapshot = new MetricSnapshot(new MetricId("other.metric"), MetricValue.From(1d), DateTimeOffset.UtcNow,
            MetricState.Fresh, MetricConfidence.Measured);
        Assert.Throws<InvalidDataException>(() => MetricPresentation.Create(Percent, PercentDescriptor, snapshot));
    }

    [TestMethod]
    public void DescriptorOwnsUnitAndValueKindWhileFormatStaysInPresentation()
    {
        var result = Present(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(42.25));
        Assert.AreEqual("42.3 %", result.ValueText);
        Assert.Throws<InvalidDataException>(() => MetricPresentation.Create(Percent, PercentDescriptor,
            Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(42L), "source")));
    }

    [TestMethod]
    public void MissingDescriptorDoesNotInventSemanticMetadata()
    {
        var result = MetricPresentation.Create(new MetricPresentationSpec(new MetricId("unknown.metric"), "0.0"),
            null, new MetricSnapshot(new MetricId("unknown.metric"), MetricValue.From(2d), DateTimeOffset.UtcNow,
                MetricState.Fresh, MetricConfidence.Measured));
        Assert.AreEqual("2.0", result.ValueText);
    }

    [TestMethod]
    public void ReconnectedDescriptorFreePeerDoesNotReusePreviousUnitOrValueKind()
    {
        var previous = MetricPresentation.Create(Percent, PercentDescriptor,
            Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(2d), "source"));
        var current = MetricPresentation.Create(Percent, null,
            Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(42L), "source"));

        Assert.AreEqual("2.0 %", previous.ValueText);
        Assert.AreEqual("42.0", current.ValueText);
    }

    [TestMethod]
    [DataRow(MetricState.Fresh, 0d, 0d)]
    [DataRow(MetricState.Fresh, 42d, 0.42)]
    [DataRow(MetricState.Fresh, 100d, 1d)]
    [DataRow(MetricState.Stale, 8d, 0.08)]
    public void BarRetainsFiniteValueStateAndConfidence(MetricState state, double value, double fill)
    {
        var snapshot = Snapshot(state, MetricConfidence.Estimated, MetricValue.From(value), "any-provider");
        var result = MetricBarPresentation.Create(new(Percent, 0, 100), PercentDescriptor, snapshot);
        Assert.AreEqual(fill, result.FillFraction);
        Assert.AreEqual("", result.RangeState);
        var text = MetricPresentation.Create(Percent, PercentDescriptor, snapshot);
        Assert.AreEqual(state.ToString(), text.StateText);
        Assert.AreEqual("Estimated", text.ConfidenceText);
        Assert.AreEqual(value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " %", text.ValueText);
    }

    [TestMethod]
    [DataRow(MetricState.Unavailable)]
    [DataRow(MetricState.Stopped)]
    [DataRow(MetricState.Error)]
    [DataRow(MetricState.Unlimited)]
    public void BarNeverShowsNumericFillForValuelessStates(MetricState state)
    {
        var snapshot = Snapshot(state, MetricConfidence.Unknown, null, "source",
            state == MetricState.Error ? "provider_failure" : null);
        var result = MetricBarPresentation.Create(new(Percent, 0, 100), PercentDescriptor, snapshot);
        Assert.IsNull(result.FillFraction);
        Assert.AreEqual("", result.RangeState);
        var text = MetricPresentation.Create(Percent, PercentDescriptor, snapshot);
        Assert.AreEqual(state == MetricState.Unlimited ? "Unlimited" : "—", text.ValueText);
        Assert.AreEqual("Unknown", text.ConfidenceText);
    }

    [TestMethod]
    [DataRow(-1d, "Below range")]
    [DataRow(120d, "Above range")]
    public void BarSuppressesOverflowFillAndPreservesActualValue(double value, string rangeState)
    {
        var snapshot = Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(value), "source");
        var result = MetricBarPresentation.Create(new(Percent, 0, 100), PercentDescriptor, snapshot);
        Assert.IsNull(result.FillFraction);
        Assert.AreEqual(rangeState, result.RangeState);
        Assert.AreEqual(value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " %",
            MetricPresentation.Create(Percent, PercentDescriptor, snapshot).ValueText);
    }

    [TestMethod]
    public void BarRangeIsExplicitAndIndependentOfSourceUnitAndMetricId()
    {
        var spec = new MetricPresentationSpec(new("arbitrary.id"), "0.0");
        var descriptor = new MetricDescriptor(spec.MetricId, "Temperature", "temperature", MetricValueKind.Double, unit: "°C");
        var a = new MetricSnapshot(spec.MetricId, MetricValue.From(10d), DateTimeOffset.UtcNow,
            MetricState.Fresh, MetricConfidence.Measured, "ProviderA");
        var b = new MetricSnapshot(spec.MetricId, MetricValue.From(10d), DateTimeOffset.UtcNow,
            MetricState.Fresh, MetricConfidence.Measured, "ProviderB");
        Assert.AreEqual(0.75, MetricBarPresentation.Create(new(spec, -20, 20), descriptor, a).FillFraction);
        Assert.AreEqual(MetricBarPresentation.Create(new(spec, -20, 20), descriptor, a),
            MetricBarPresentation.Create(new(spec, -20, 20), descriptor, b));
        Assert.AreEqual(0.1, MetricBarPresentation.Create(new(spec, 0, 100), null, a).FillFraction);
        Assert.AreEqual("10.0", MetricPresentation.Create(spec, null, a).ValueText);
    }

    [TestMethod]
    [DataRow(0d, 0d)]
    [DataRow(100d, 0d)]
    [DataRow(double.NaN, 100d)]
    [DataRow(0d, double.PositiveInfinity)]
    [DataRow(-double.MaxValue, double.MaxValue)]
    public void BarRejectsInvalidConfiguredRange(double minimum, double maximum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricBarSpec(Percent, minimum, maximum));

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void BarSuppressesNonFiniteFill(double value)
    {
        var snapshot = Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(value), "source");
        Assert.IsNull(MetricBarPresentation.Create(new(Percent, 0, 100), PercentDescriptor, snapshot).FillFraction);
    }

    [TestMethod]
    public void BarSupportsNumericKindsAndRejectsDescriptorMismatch()
    {
        var integer = Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From(25L), "source");
        Assert.AreEqual(0.25, MetricBarPresentation.Create(new(Percent, 0, 100), null, integer).FillFraction);
        var fraction = Snapshot(MetricState.Stale, MetricConfidence.Estimated, MetricValue.From(25.5m), "source");
        Assert.AreEqual(0.255, MetricBarPresentation.Create(new(Percent, 0, 100), null, fraction).FillFraction);
        Assert.Throws<InvalidDataException>(() => MetricBarPresentation.Create(new(Percent, 0, 100), PercentDescriptor, integer));
        var text = Snapshot(MetricState.Fresh, MetricConfidence.Measured, MetricValue.From("reported text"), "source");
        Assert.IsNull(MetricBarPresentation.Create(new(Percent, 0, 100), null, text).FillFraction);
        Assert.AreEqual("reported text", MetricPresentation.Create(Percent, null, text).ValueText);
    }

    [TestMethod]
    public void BothPresentationsStartWithoutInventedSnapshotOrFill()
    {
        var subscription = new MetricSubscription(Percent.MetricId, new(), "unused");
        Assert.AreEqual("Connecting", MetricPresentation.Create(Percent, subscription).StateText);
        Assert.AreEqual("No snapshot", MetricPresentation.Create(Percent, subscription).ConfidenceText);
        Assert.IsNull(MetricBarPresentation.Create(new(Percent, 0, 100), null, subscription.Snapshot).FillFraction);
    }

    private static MetricPresentationResult Present(MetricState state, MetricConfidence confidence, MetricValue? value) =>
        MetricPresentation.Create(Percent, PercentDescriptor, Snapshot(state, confidence, value, "source"));

    private static MetricSnapshot Snapshot(MetricState state, MetricConfidence confidence, MetricValue? value,
        string? source, string? errorCode = null) =>
        new(Percent.MetricId, value, DateTimeOffset.UtcNow, state, confidence, source, errorCode);
}
