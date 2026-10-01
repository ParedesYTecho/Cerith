#nullable enable

using System;
using System.Text;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Qoder;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Qoder;

[TestClass]
public sealed class QoderQuotaParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PotentiallyAvailableOrganizationPoolWithholdsAggregateButKeepsPersonalAndAddon()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "userType": "synthetic-team-plan",
              "totalUsagePercentage": 31.25,
              "isQuotaExceeded": false,
              "expiresAt": 1798761600000,
              "userQuota": { "used": 12.5, "total": 100, "remaining": 87.5, "percentage": 12.5, "unit": "credits" },
              "addOnQuota": { "used": 3, "total": 20, "remaining": 17, "percentage": 15, "unit": "credits" },
              "orgResourcePackage": { "used": 8, "cap": 40, "remaining": 32, "percentage": 20, "available": true, "unit": "credits" },
              "session": { "total_credits": 999 }
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", "synthetic-org-1"));

        Assert.AreEqual(16, reading.Points.Length);
        AssertMeasured(reading.Points[0], 12.5d);
        AssertMeasured(reading.Points[1], 12.5d);
        AssertMeasured(reading.Points[2], 87.5d);
        AssertMeasured(reading.Points[3], 100d);
        AssertMeasured(reading.Points[4], 15d);
        AssertMeasured(reading.Points[5], 3d);
        AssertMeasured(reading.Points[6], 17d);
        AssertMeasured(reading.Points[7], 20d);
        for (var index = 8; index <= 12; index++)
        {
            Assert.AreEqual(MetricState.Unavailable, reading.Points[index].State);
            Assert.AreEqual("scope_unverified", reading.Points[index].ErrorCode);
            Assert.IsNull(reading.Points[index].Value);
        }
        AssertAggregateScopeUnverified(reading);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1798761600000),
            reading.Points[15].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual("synthetic-user-1", reading.AccountId);
        Assert.AreEqual("synthetic-org-1", reading.OrganizationId);
    }

    [TestMethod]
    public void MissingOrganizationAvailabilityWithholdsAggregateButKeepsPersonalAndAddon()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "totalUsagePercentage": 31.25,
              "isQuotaExceeded": false,
              "userQuota": { "used": 12.5, "total": 100, "remaining": 87.5, "percentage": 12.5, "unit": "credits" },
              "addOnQuota": { "used": 3, "total": 20, "remaining": 17, "percentage": 15, "unit": "credits" },
              "orgResourcePackage": { "used": 8, "cap": 40, "remaining": 32, "percentage": 20, "unit": "credits" }
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", "synthetic-org-1"));

        AssertMeasured(reading.Points[0], 12.5d);
        AssertMeasured(reading.Points[5], 3d);
        AssertAggregateScopeUnverified(reading);
    }

    [TestMethod]
    public void ExplicitlyUnavailableOrganizationPoolPreservesAggregateFields()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "totalUsagePercentage": 31.25,
              "isQuotaExceeded": true,
              "userQuota": { "used": 12.5, "total": 100, "remaining": 87.5, "percentage": 12.5, "unit": "credits" },
              "addOnQuota": { "used": 3, "total": 20, "remaining": 17, "percentage": 15, "unit": "credits" },
              "orgResourcePackage": { "used": 8, "cap": 40, "remaining": 32, "percentage": 20, "available": false, "unit": "credits" }
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", "synthetic-org-1"));

        AssertMeasured(reading.Points[0], 12.5d);
        AssertMeasured(reading.Points[5], 3d);
        AssertMeasured(reading.Points[13], 31.25d);
        Assert.AreEqual(MetricState.Fresh, reading.Points[14].State);
        Assert.IsTrue(reading.Points[14].Value!.Value.AsBoolean());
    }

    [TestMethod]
    public void AbsentOrganizationPackagePreservesAggregateFields()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "totalUsagePercentage": 31.25,
              "isQuotaExceeded": false
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", null));

        AssertMeasured(reading.Points[13], 31.25d);
        Assert.AreEqual(MetricState.Fresh, reading.Points[14].State);
        Assert.IsFalse(reading.Points[14].Value!.Value.AsBoolean());
    }

    [TestMethod]
    public void InvalidOrganizationAvailabilityWithholdsAggregate()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "totalUsagePercentage": 31.25,
              "isQuotaExceeded": false,
              "orgResourcePackage": { "available": "true" }
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", "synthetic-org-1"));

        AssertAggregateScopeUnverified(reading);
    }

    [TestMethod]
    public void MissingValuesStayUnavailableAndRealZeroStaysMeasured()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "userQuota": { "used": 0, "unit": "credits" }
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", "synthetic-org-1"));

        AssertMeasured(reading.Points[1], 0d);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[2].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[3].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[4].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[8].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[13].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[14].State);
    }

    [TestMethod]
    public void OrganizationValuesStayUnverifiedEvenWithSelectedOrganizationIdentity()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "orgResourcePackage": { "used": 4, "cap": 10, "remaining": 6, "percentage": 40, "available": true, "unit": "credits" }
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", "synthetic-org-1"));

        for (var index = 8; index <= 12; index++)
        {
            Assert.AreEqual(MetricState.Unavailable, reading.Points[index].State);
            Assert.AreEqual("scope_unverified", reading.Points[index].ErrorCode);
            Assert.IsNull(reading.Points[index].Value);
        }
        Assert.AreEqual(MetricState.Unavailable, reading.Points[1].State);
    }

    [TestMethod]
    public void UnknownCreditUnitDoesNotBecomeCreditsAndPercentagesAreNotClamped()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "totalUsagePercentage": 100.01,
              "userQuota": { "used": 5, "total": 10, "remaining": 5, "percentage": -1, "unit": "requests" }
            }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-1", null));

        Assert.AreEqual(MetricState.Unavailable, reading.Points[1].State);
        Assert.AreEqual("unit_unverified", reading.Points[1].ErrorCode);
        Assert.AreEqual(MetricState.Error, reading.Points[0].State);
        Assert.AreEqual("invalid_percentage", reading.Points[0].ErrorCode);
        Assert.AreEqual(MetricState.Error, reading.Points[13].State);
        Assert.AreEqual("invalid_percentage", reading.Points[13].ErrorCode);
    }

    [TestMethod]
    public void AccountMismatchDiscardsTheWholeSnapshot()
    {
        const string json = """
            { "userId": "synthetic-user-B", "userQuota": { "used": 1, "total": 2, "remaining": 1, "unit": "credits" } }
            """;

        var reading = Parse(json, new QoderSessionIdentity("synthetic-user-A", null));

        Assert.AreEqual(MetricState.Unavailable, reading.Points[1].State);
        Assert.AreEqual("identity_unavailable", reading.Points[1].ErrorCode);
        Assert.IsNull(reading.Points[1].Value);
    }

    [TestMethod]
    public void ExactKnownNoExpiryValueIsAbsentAndOtherEpochMillisecondsRemainTimestamps()
    {
        var sentinelJson = $$"""
            { "userId": "synthetic-user-1", "expiresAt": {{QoderQuotaParser.NoExpiryEpochMilliseconds}} }
            """;
        var datedJson = """
            { "userId": "synthetic-user-1", "expiresAt": 1798761600000 }
            """;

        var sentinel = Parse(sentinelJson, new QoderSessionIdentity("synthetic-user-1", null));
        var dated = Parse(datedJson, new QoderSessionIdentity("synthetic-user-1", null));

        Assert.AreEqual(MetricState.Unavailable, sentinel.Points[15].State);
        Assert.IsNull(sentinel.Points[15].Value);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1798761600000),
            dated.Points[15].Value!.Value.AsDateTimeOffset());
    }

    [TestMethod]
    public void AuthStatusSelectsOnlyAccountAndOrganizationIds()
    {
        const string json = """
            {
              "id": "synthetic-user-1",
              "accountId": "synthetic-user-1",
              "orgId": "synthetic-org-1",
              "name": "Synthetic Name",
              "email": "not-for-telemetry@example.invalid",
              "token": "synthetic-secret"
            }
            """;

        var parsed = QoderQuotaParser.TryParseAuthStatus(Encoding.UTF8.GetBytes(json), out var identity);

        Assert.IsTrue(parsed);
        Assert.AreEqual(new QoderSessionIdentity("synthetic-user-1", "synthetic-org-1"), identity);
    }

    [TestMethod]
    public void AuthStatusRejectsConflictingAccountIdentifiers()
    {
        const string json = """
            { "id": "synthetic-user-A", "accountId": "synthetic-user-B", "orgId": "synthetic-org-1" }
            """;

        Assert.IsFalse(QoderQuotaParser.TryParseAuthStatus(Encoding.UTF8.GetBytes(json), out _));
    }

    [TestMethod]
    public void BlankOrganizationIdDoesNotSelectAnOrganizationPool()
    {
        const string json = """{ "id": "synthetic-user-1", "orgId": " " }""";

        Assert.IsTrue(QoderQuotaParser.TryParseAuthStatus(Encoding.UTF8.GetBytes(json), out var identity));
        Assert.AreEqual(new QoderSessionIdentity("synthetic-user-1", null), identity);
    }

    [TestMethod]
    public void MalformedJsonIsValuelessError()
    {
        var reading = QoderQuotaParser.Parse(Encoding.UTF8.GetBytes("{broken"),
            new QoderSessionIdentity("synthetic-user-1", null), ObservedAt);

        Assert.AreEqual(MetricState.Error, reading.Points[0].State);
        Assert.AreEqual("schema_invalid", reading.Points[0].ErrorCode);
        Assert.IsNull(reading.Points[0].Value);
    }

    private static QoderQuotaReading Parse(string json, QoderSessionIdentity identity) =>
        QoderQuotaParser.Parse(Encoding.UTF8.GetBytes(json), identity, ObservedAt);

    private static void AssertAggregateScopeUnverified(QoderQuotaReading reading)
    {
        for (var index = 13; index <= 14; index++)
        {
            Assert.AreEqual(MetricState.Unavailable, reading.Points[index].State);
            Assert.AreEqual("scope_unverified", reading.Points[index].ErrorCode);
            Assert.IsNull(reading.Points[index].Value);
        }
    }

    private static void AssertMeasured(QoderQuotaPoint point, double expected)
    {
        Assert.AreEqual(MetricState.Fresh, point.State);
        Assert.AreEqual(expected, point.Value!.Value.AsDouble(), 0.000001);
        Assert.IsNull(point.ErrorCode);
    }
}
