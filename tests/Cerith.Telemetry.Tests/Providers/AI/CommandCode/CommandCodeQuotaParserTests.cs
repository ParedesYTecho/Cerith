#nullable enable

using System;
using System.Text.Json;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.CommandCode;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.CommandCode;

[TestClass]
public sealed class CommandCodeQuotaParserTests
{
    [TestMethod]
    public void ParsesUsedPercentResetAndSanitizedSubscriptionFields()
    {
        using var credits = JsonDocument.Parse("""
            {"windowLimits":{
              "fiveHour":{"used":25,"cap":100,"resetAt":1912345678901},
              "weekly":{"used":"4","cap":"20","resetAt":1912950478901}
            },"credits":{"balance":{"amount":42,"unit":"unknown"}}}
            """);
        using var subscription = JsonDocument.Parse("""
            {"data":{"planId":"individual-pro-v1","status":"active","accountEmail":"private@example.invalid"}}
            """);

        var parsed = CommandCodeQuotaParser.Parse(credits.RootElement, subscription.RootElement);

        Assert.AreEqual(MetricState.Fresh, parsed.FiveHour.UsedPercent.State);
        Assert.AreEqual(25d, parsed.FiveHour.UsedPercent.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1912345678901), parsed.FiveHour.ResetAt);
        Assert.AreEqual(20d, parsed.Weekly.UsedPercent.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual("active", parsed.SubscriptionStatus.Value!.Value.AsString());
        Assert.AreEqual("Pro", parsed.SubscriptionPlan.Value!.Value.AsString());
    }

    [TestMethod]
    public void MissingQuotaIsUnavailableAndZeroRemainsARealUsedValue()
    {
        using var credits = JsonDocument.Parse("""
            {"windowLimits":{
              "fiveHour":{"cap":100},
              "weekly":{"used":0,"cap":100}
            }}
            """);
        using var subscription = JsonDocument.Parse("{}");

        var parsed = CommandCodeQuotaParser.Parse(credits.RootElement, subscription.RootElement);

        Assert.AreEqual(MetricState.Unavailable, parsed.FiveHour.UsedPercent.State);
        Assert.IsNull(parsed.FiveHour.UsedPercent.Value);
        Assert.AreEqual("quota_missing", parsed.FiveHour.UsedPercent.ErrorCode);
        Assert.AreEqual(MetricState.Fresh, parsed.Weekly.UsedPercent.State);
        Assert.AreEqual(0d, parsed.Weekly.UsedPercent.Value!.Value.AsDouble(), 0d);
        Assert.AreEqual(MetricState.Unavailable, parsed.Weekly.Reset.State);
    }

    [TestMethod]
    public void InvalidUsageAndNonIntegerResetAreValuelessErrors()
    {
        using var credits = JsonDocument.Parse("""
            {"windowLimits":{
              "fiveHour":{"used":"NaN","cap":100,"resetAt":1912345678901},
              "weekly":{"used":1,"cap":0,"resetAt":1912345678901.5}
            }}
            """);
        using var subscription = JsonDocument.Parse("{}");

        var parsed = CommandCodeQuotaParser.Parse(credits.RootElement, subscription.RootElement);

        Assert.AreEqual(MetricState.Error, parsed.FiveHour.UsedPercent.State);
        Assert.IsNull(parsed.FiveHour.UsedPercent.Value);
        Assert.AreEqual("invalid_quota", parsed.FiveHour.UsedPercent.ErrorCode);
        Assert.AreEqual(MetricState.Error, parsed.Weekly.UsedPercent.State);
        Assert.IsNull(parsed.Weekly.UsedPercent.Value);
        Assert.AreEqual(MetricState.Error, parsed.Weekly.Reset.State);
        Assert.IsNull(parsed.Weekly.Reset.Value);
    }

    [TestMethod]
    public void ResetEpochIsAlwaysMillisecondsAndUnknownPlanIdIsNotPublished()
    {
        using var credits = JsonDocument.Parse("""
            {"windowLimits":{"fiveHour":{"used":1,"cap":2,"resetAt":1900000000},"weekly":null}}
            """);
        using var subscription = JsonDocument.Parse("""
            {"data":{"planId":"private-custom-tier-v7","status":"active"}}
            """);

        var parsed = CommandCodeQuotaParser.Parse(credits.RootElement, subscription.RootElement);

        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1900000000), parsed.FiveHour.ResetAt);
        Assert.AreEqual(MetricState.Unavailable, parsed.Weekly.UsedPercent.State);
        Assert.AreEqual(MetricState.Unavailable, parsed.SubscriptionPlan.State);
        Assert.IsNull(parsed.SubscriptionPlan.Value);
    }

    [TestMethod]
    public void OrgScopeUsesOnlyObservedOrgIdAndRejectsConflictingScopes()
    {
        using var supplied = JsonDocument.Parse("""
            {"data":{"org":{"id":"org / one"}}}
            """);
        using var conflict = JsonDocument.Parse("""
            {"org":{"id":"org-a"},"data":{"org":{"id":"org-b"}}}
            """);
        using var absent = JsonDocument.Parse("""{"user":{"id":"private-user"}}""");

        var observed = CommandCodeQuotaParser.ParseOrgScope(supplied.RootElement);
        var conflicting = CommandCodeQuotaParser.ParseOrgScope(conflict.RootElement);
        var missing = CommandCodeQuotaParser.ParseOrgScope(absent.RootElement);

        Assert.IsTrue(observed.IsValid);
        Assert.AreEqual("org / one", observed.OrgId);
        Assert.IsFalse(conflicting.IsValid);
        Assert.IsTrue(missing.IsValid);
        Assert.IsNull(missing.OrgId);
    }
}
