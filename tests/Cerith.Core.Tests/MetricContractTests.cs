#nullable enable

using System;
using Cerith.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Core.Tests;

[TestClass]
public sealed class MetricIdTests
{
    [TestMethod]
    public void RejectsMissingWhitespaceOnlyAndPaddedIdentifiers()
    {
        Assert.Throws<ArgumentNullException>(() => new MetricId(null!));
        Assert.Throws<ArgumentException>(() => new MetricId(string.Empty));
        Assert.Throws<ArgumentException>(() => new MetricId(" \t"));
        Assert.Throws<ArgumentException>(() => new MetricId(" cpu.total.usage"));
        Assert.Throws<ArgumentException>(() => new MetricId("cpu.total.usage\u00a0"));
    }

    [TestMethod]
    public void PreservesExactTextAndUsesCaseSensitiveValueEquality()
    {
        var id = new MetricId("AI.Custom metric.v1");

        Assert.AreEqual("AI.Custom metric.v1", id.Value);
        Assert.AreEqual("AI.Custom metric.v1", id.ToString());
        Assert.IsTrue(id == new MetricId("AI.Custom metric.v1"));
        Assert.IsFalse(id == new MetricId("ai.Custom metric.v1"));
    }
}

[TestClass]
public sealed class MetricValueTests
{
    [TestMethod]
    public void KeepsEverySupportedValueKindAndItsTypedValue()
    {
        var timestamp = new DateTimeOffset(2026, 9, 23, 12, 34, 56, TimeSpan.FromHours(5));
        var duration = TimeSpan.FromMilliseconds(125);

        var doubleValue = MetricValue.From(12.5d);
        Assert.AreEqual(MetricValueKind.Double, doubleValue.Kind);
        Assert.AreEqual(12.5d, doubleValue.AsDouble());

        var int64Value = MetricValue.From(42L);
        Assert.AreEqual(MetricValueKind.Int64, int64Value.Kind);
        Assert.AreEqual(42L, int64Value.AsInt64());

        var decimalValue = MetricValue.From(19.95m);
        Assert.AreEqual(MetricValueKind.Decimal, decimalValue.Kind);
        Assert.AreEqual(19.95m, decimalValue.AsDecimal());

        var booleanValue = MetricValue.From(true);
        Assert.AreEqual(MetricValueKind.Boolean, booleanValue.Kind);
        Assert.IsTrue(booleanValue.AsBoolean());

        var stringValue = MetricValue.From("active");
        Assert.AreEqual(MetricValueKind.String, stringValue.Kind);
        Assert.AreEqual("active", stringValue.AsString());

        var timestampValue = MetricValue.From(timestamp);
        Assert.AreEqual(MetricValueKind.DateTimeOffset, timestampValue.Kind);
        Assert.AreEqual(timestamp, timestampValue.AsDateTimeOffset());
        Assert.AreEqual(timestamp.Offset, timestampValue.AsDateTimeOffset().Offset);

        var durationValue = MetricValue.From(duration);
        Assert.AreEqual(MetricValueKind.TimeSpan, durationValue.Kind);
        Assert.AreEqual(duration, durationValue.AsTimeSpan());
    }

    [TestMethod]
    public void TypedReadRejectsADifferentKind()
    {
        Assert.Throws<InvalidOperationException>(() => MetricValue.From(1L).AsDouble());
    }
}

[TestClass]
public sealed class MetricDescriptorTests
{
    [TestMethod]
    public void DescribesAnOpenMetricIdWithoutProviderIdentity()
    {
        var descriptor = new MetricDescriptor(
            new MetricId("plugin.sample.metric"),
            "Sample metric",
            "custom.semantic.type",
            MetricValueKind.TimeSpan,
            unit: "ms");

        Assert.AreEqual("plugin.sample.metric", descriptor.Id.Value);
        Assert.AreEqual("custom.semantic.type", descriptor.SemanticType);
        Assert.AreEqual(MetricValueKind.TimeSpan, descriptor.ValueKind);
        Assert.AreEqual("ms", descriptor.Unit);
    }

    [TestMethod]
    public void RequiresANameSemanticTypeAndConcreteValueKind()
    {
        var id = new MetricId("plugin.sensor.temperature");

        Assert.Throws<ArgumentException>(() =>
            new MetricDescriptor(id, " ", "temperature", MetricValueKind.Double));
        Assert.Throws<ArgumentException>(() =>
            new MetricDescriptor(id, "Temperature", "\t", MetricValueKind.Double));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MetricDescriptor(id, "Temperature", "temperature", MetricValueKind.None));
    }
}

[TestClass]
public sealed class MetricSnapshotTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(2));

    [TestMethod]
    public void FreshAndStaleRequireARealValue()
    {
        Assert.Throws<ArgumentException>(() => Create(MetricState.Fresh, null));
        Assert.Throws<ArgumentException>(() => Create(MetricState.Stale, null));
        Assert.Throws<ArgumentException>(() => Create(MetricState.Fresh, default(MetricValue)));
    }

    [TestMethod]
    public void ZeroIsPresentRatherThanMissing()
    {
        var snapshot = Create(MetricState.Fresh, MetricValue.From(0d), MetricConfidence.Measured);

        Assert.IsTrue(snapshot.Value.HasValue);
        Assert.AreEqual(0d, snapshot.Value.Value.AsDouble());
    }

    [TestMethod]
    public void StatesWithoutMeasurementsRejectFabricatedValues()
    {
        foreach (var state in new[] { MetricState.Unavailable, MetricState.Stopped, MetricState.Error })
        {
            Assert.Throws<ArgumentException>(() =>
                Create(state, MetricValue.From(0L), MetricConfidence.Unknown));
        }
    }

    [TestMethod]
    public void UnlimitedRequiresNoValueAndUnknownConfidence()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(MetricState.Unlimited, MetricValue.From(0d)));
        Assert.Throws<ArgumentException>(() =>
            Create(MetricState.Unlimited, MetricValue.From("no finite limit")));

        var snapshot = Create(MetricState.Unlimited, null);

        Assert.AreEqual(MetricState.Unlimited, snapshot.State);
        Assert.IsFalse(snapshot.Value.HasValue);
        Assert.AreEqual(MetricConfidence.Unknown, snapshot.Confidence);
    }

    [TestMethod]
    public void SnapshotsWithoutValuesCannotClaimMeasuredOrEstimatedConfidence()
    {
        foreach (var state in new[]
        {
            MetricState.Unavailable,
            MetricState.Stopped,
            MetricState.Error,
            MetricState.Unlimited
        })
        {
            Assert.Throws<ArgumentException>(() =>
                Create(state, null, MetricConfidence.Measured));
            Assert.Throws<ArgumentException>(() =>
                Create(state, null, MetricConfidence.Estimated));
        }
    }

    [TestMethod]
    [DataRow(MetricState.Fresh, MetricConfidence.Measured)]
    [DataRow(MetricState.Fresh, MetricConfidence.Estimated)]
    [DataRow(MetricState.Stale, MetricConfidence.Measured)]
    [DataRow(MetricState.Stale, MetricConfidence.Estimated)]
    public void FreshnessAndConfidenceRemainIndependent(
        MetricState state,
        MetricConfidence confidence)
    {
        var snapshot = Create(
            state,
            MetricValue.From(23.5d),
            confidence,
            errorCode: state == MetricState.Stale ? "refresh_failed" : null);

        Assert.AreEqual(state, snapshot.State);
        Assert.AreEqual(confidence, snapshot.Confidence);
        Assert.AreEqual(23.5d, snapshot.Value!.Value.AsDouble());
        Assert.AreEqual(
            state == MetricState.Stale ? "refresh_failed" : null,
            snapshot.ErrorCode);
    }

    [TestMethod]
    public void ObservedTimestampRetainsItsOffset()
    {
        var snapshot = Create(MetricState.Fresh, MetricValue.From(1d));

        Assert.AreEqual(ObservedAt, snapshot.ObservedAt);
        Assert.AreEqual(TimeSpan.FromHours(2), snapshot.ObservedAt.Offset);
    }

    [TestMethod]
    public void ErrorCodeAcceptsOnlyShortSanitizedIdentifiers()
    {
        var snapshot = Create(
            MetricState.Error,
            null,
            errorCode: "provider_timeout");

        Assert.AreEqual("provider_timeout", snapshot.ErrorCode);
        Assert.Throws<ArgumentException>(() =>
            Create(MetricState.Error, null, errorCode: "provider failed with details"));
        Assert.Throws<ArgumentException>(() =>
            Create(MetricState.Error, null, errorCode: new string('x', 65)));
    }

    private static MetricSnapshot Create(
        MetricState state,
        MetricValue? value,
        MetricConfidence confidence = MetricConfidence.Unknown,
        string? errorCode = null) => new(
            new MetricId("cpu.total.usage"),
            value,
            ObservedAt,
            state,
            confidence,
            source: "test-provider",
            errorCode: errorCode);
}
