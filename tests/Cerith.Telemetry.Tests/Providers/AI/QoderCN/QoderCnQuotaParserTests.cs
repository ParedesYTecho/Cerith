#nullable enable

using System;
using System.Text;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class QoderCnQuotaParserTests
{
    [TestMethod]
    public void ParsesOnlyAccountIncludedAndAddOnBucketsAndPlanExpiry()
    {
        var reading = Parse("""
            {"usage":{
              "userId":"cn-user-17",
              "userType":"pro",
              "totalUsagePercentage":21.5,
              "isQuotaExceeded":false,
              "expiresAt":1792099200000,
              "userQuota":{"total":500,"used":125,"remaining":375,"percentage":25,"unit":"Credits"},
              "addOnQuota":{"total":100,"used":10,"remaining":90,"percentage":10,"unit":"Credits"},
              "orgResourcePackage":{"cap":1000,"used":40,"remaining":960,"percentage":4,"available":true,"unit":"Credits"}
            },"session":{"total_credits":999,"model_usage":{"model-a":{"credits":999}}}}
            """);

        AssertFresh(reading, QoderCnQuotaParser.IncludedTotal, 500);
        AssertFresh(reading, QoderCnQuotaParser.IncludedUsed, 125);
        AssertFresh(reading, QoderCnQuotaParser.IncludedRemaining, 375);
        AssertFresh(reading, QoderCnQuotaParser.AddOnTotal, 100);
        AssertFresh(reading, QoderCnQuotaParser.AddOnUsed, 10);
        AssertFresh(reading, QoderCnQuotaParser.AddOnRemaining, 90);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1792099200000),
            reading.Points[QoderCnQuotaParser.PlanExpiresAt].Value!.Value.AsDateTimeOffset());
        Assert.IsTrue(reading.Points.AsSpan(QoderCnQuotaParser.OrganizationCap, 3).ToArray().AllUnavailableWithCode("scope_unverified"));
    }

    [TestMethod]
    public void MissingFieldsRemainUnavailableAndExplicitZeroRemainsMeasured()
    {
        var reading = Parse("""
            {"usage":{"userQuota":{"used":0},"addOnQuota":null},"session":{"total_credits":42}}
            """);

        Assert.AreEqual(MetricState.Unavailable, reading.Points[QoderCnQuotaParser.IncludedTotal].State);
        Assert.IsNull(reading.Points[QoderCnQuotaParser.IncludedTotal].Value);
        Assert.AreEqual(MetricState.Fresh, reading.Points[QoderCnQuotaParser.IncludedUsed].State);
        Assert.AreEqual(0d, reading.Points[QoderCnQuotaParser.IncludedUsed].Value!.Value.AsDouble());
        Assert.AreEqual(MetricState.Unavailable, reading.Points[QoderCnQuotaParser.IncludedRemaining].State);
        Assert.IsTrue(reading.Points.AsSpan(QoderCnQuotaParser.AddOnTotal, 3).ToArray().AllUnavailable());
        Assert.AreEqual(MetricState.Unavailable, reading.Points[QoderCnQuotaParser.PlanExpiresAt].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[QoderCnQuotaParser.OrganizationCap].State);
    }

    [TestMethod]
    public void MalformedValuesFailIndependentlyWithoutClampingOrSentinelReset()
    {
        var reading = Parse("""
            {"usage":{
              "userQuota":{"total":-1,"used":"125","remaining":0.5},
              "addOnQuota":[],
              "expiresAt":"2026-10-01T00:00:00Z",
              "orgResourcePackage":{}
            }}
            """);

        AssertError(reading, QoderCnQuotaParser.IncludedTotal, "invalid_quota");
        AssertError(reading, QoderCnQuotaParser.IncludedUsed, "invalid_quota");
        AssertFresh(reading, QoderCnQuotaParser.IncludedRemaining, 0.5);
        AssertError(reading, QoderCnQuotaParser.AddOnTotal, "schema_invalid");
        AssertError(reading, QoderCnQuotaParser.AddOnUsed, "schema_invalid");
        AssertError(reading, QoderCnQuotaParser.AddOnRemaining, "schema_invalid");
        AssertError(reading, QoderCnQuotaParser.PlanExpiresAt, "invalid_timestamp");
        Assert.IsTrue(reading.Points.AsSpan(QoderCnQuotaParser.OrganizationCap, 3).ToArray().AllUnavailableWithCode("scope_unverified"));
    }

    [TestMethod]
    public void InvalidDocumentFailsClosedWhileNullUsageIsMissing()
    {
        var malformed = QoderCnQuotaParser.Parse(Encoding.UTF8.GetBytes("{"));
        Assert.IsTrue(malformed.Points.AllErrorsWithCode("schema_invalid"));

        var missing = Parse("""
            {"usage":null,"session":{"total_credits":9}}
            """);
        Assert.IsTrue(missing.Points.AllUnavailable());
    }

    [TestMethod]
    public void DoesNotLabelAnExplicitNonCreditUnitAsCredits()
    {
        var reading = Parse("""
            {"usage":{"userQuota":{"total":10,"used":2,"remaining":8,"unit":"Tokens"}}}
            """);

        AssertError(reading, QoderCnQuotaParser.IncludedTotal, "unit_unsupported");
        AssertError(reading, QoderCnQuotaParser.IncludedUsed, "unit_unsupported");
        AssertError(reading, QoderCnQuotaParser.IncludedRemaining, "unit_unsupported");
    }

    private static QoderCnQuotaReading Parse(string json) => QoderCnQuotaParser.Parse(Encoding.UTF8.GetBytes(json));

    private static void AssertFresh(QoderCnQuotaReading reading, int index, double value)
    {
        Assert.AreEqual(MetricState.Fresh, reading.Points[index].State);
        Assert.AreEqual(value, reading.Points[index].Value!.Value.AsDouble());
        Assert.IsNull(reading.Points[index].ErrorCode);
    }

    private static void AssertError(QoderCnQuotaReading reading, int index, string code)
    {
        Assert.AreEqual(MetricState.Error, reading.Points[index].State);
        Assert.IsNull(reading.Points[index].Value);
        Assert.AreEqual(code, reading.Points[index].ErrorCode);
    }
}

internal static class QoderCnQuotaPointAssertions
{
    public static bool AllUnavailable(this QoderCnQuotaPoint[] points)
    {
        foreach (var point in points)
            if (point.State != MetricState.Unavailable || point.Value is not null)
                return false;
        return true;
    }

    public static bool AllUnavailableWithCode(this QoderCnQuotaPoint[] points, string code)
    {
        foreach (var point in points)
            if (point.State != MetricState.Unavailable || point.Value is not null || point.ErrorCode != code)
                return false;
        return true;
    }

    public static bool AllErrorsWithCode(this QoderCnQuotaPoint[] points, string code)
    {
        foreach (var point in points)
            if (point.State != MetricState.Error || point.Value is not null || point.ErrorCode != code)
                return false;
        return true;
    }
}
