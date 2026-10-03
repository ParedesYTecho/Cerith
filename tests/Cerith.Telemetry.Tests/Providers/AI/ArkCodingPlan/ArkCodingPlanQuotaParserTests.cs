#nullable enable

using System;
using System.Text;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class ArkCodingPlanQuotaParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly byte[] FingerprintKey = new byte[32];
    private const string IdentityJson = """
        {"logged_in":true,"auth_method":"sso","account_id":"210000","user_id":"12345","trn":"trn:iam::210000:user/alice","is_root":false}
        """;

    [TestMethod]
    public void ParsesOnlyPersonalCodingPlanUsedPercentAndPreservesResetOffset()
    {
        var identity = ParseIdentity(IdentityJson, "personal-profile");
        var reading = Parse(identity, """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"12345","user_name":"alice","account_id":"210000","profile":"personal-profile","tenant":"volc","region":"cn-beijing","project_name":"default"},
              "items":[
                {"product":"coding-plan-team","edition":"team","subscribed":true,"periods":[{"label":"session","percent":1}]},
                {"product":"coding-plan","edition":"personal","subscribed":true,"periods":[
                  {"label":"session","used":9,"total":20,"percent":45.25,"reset_at":"2026-10-01T17:00:00+08:00"},
                  {"label":"weekly","percent":0,"reset_at":"2026-10-08T08:00:00+08:00"},
                  {"label":"monthly","percent":100,"reset_at":"2026-11-01T00:00:00+08:00"}
                ]}
              ]
            }
            """, identity);

        Assert.AreEqual(45.25d, reading.Points[ArkCodingPlanQuotaParser.SessionUsed].Value!.Value.AsDouble());
        Assert.AreEqual(0d, reading.Points[ArkCodingPlanQuotaParser.WeeklyUsed].Value!.Value.AsDouble());
        Assert.AreEqual(100d, reading.Points[ArkCodingPlanQuotaParser.MonthlyUsed].Value!.Value.AsDouble());
        var sessionReset = reading.Points[ArkCodingPlanQuotaParser.SessionReset].Value!.Value.AsDateTimeOffset();
        Assert.AreEqual(TimeSpan.FromHours(8), sessionReset.Offset);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.FromHours(8)), sessionReset);
        Assert.AreEqual(MetricState.Fresh, reading.Points[ArkCodingPlanQuotaParser.MonthlyUsed].State);
        Assert.AreEqual(identity.Fingerprint, reading.OwnerFingerprint);
    }

    [TestMethod]
    public void MissingPercentIsUnavailableAndNumericResetHasNoGuessedUnit()
    {
        var identity = ParseIdentity(IdentityJson, "personal-profile");
        var reading = Parse(identity, """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"12345","account_id":"210000","profile":"personal-profile"},
              "items":[{"product":"coding-plan","edition":"personal","subscribed":true,"periods":[
                {"label":"session","used":0,"total":100,"reset_at":1790864400},
                {"label":"weekly","percent":24.5},
                {"label":"monthly","percent":101,"reset_at":"2026-11-01T00:00:00+08:00"}
              ]}]
            }
            """, identity);

        Assert.AreEqual(MetricState.Unavailable, reading.Points[ArkCodingPlanQuotaParser.SessionUsed].State);
        Assert.IsNull(reading.Points[ArkCodingPlanQuotaParser.SessionUsed].Value);
        Assert.AreEqual("invalid_reset", reading.Points[ArkCodingPlanQuotaParser.SessionReset].ErrorCode);
        Assert.AreEqual(24.5d, reading.Points[ArkCodingPlanQuotaParser.WeeklyUsed].Value!.Value.AsDouble());
        Assert.AreEqual(MetricState.Unavailable, reading.Points[ArkCodingPlanQuotaParser.WeeklyReset].State);
        Assert.AreEqual("invalid_quota", reading.Points[ArkCodingPlanQuotaParser.MonthlyUsed].ErrorCode);
    }

    [TestMethod]
    public void UnsubscribedProductDoesNotInventQuotaValues()
    {
        var identity = ParseIdentity(IdentityJson, "personal-profile");
        var reading = Parse(identity, """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"12345","account_id":"210000","profile":"personal-profile"},
              "items":[{"product":"coding-plan","edition":"personal","subscribed":false,"periods":[]}]
            }
            """, identity);

        Assert.IsTrue(Array.TrueForAll(reading.Points, point =>
            point.State == MetricState.Unavailable && point.ErrorCode == "not_subscribed" && point.Value is null));
    }

    [TestMethod]
    public void MalformedUsageJsonReturnsUnavailableSchemaErrorsWithoutValues()
    {
        var identity = ParseIdentity(IdentityJson, "personal-profile");
        var reading = Parse(identity, "{not-json", identity);

        Assert.IsTrue(Array.TrueForAll(reading.Points, point =>
            point.State == MetricState.Error && point.ErrorCode == "schema_invalid" && point.Value is null));
    }

    [TestMethod]
    public void RejectsProfileAndOwnerMismatchAndUnsupportedRefreshOwner()
    {
        var identity = ParseIdentity(IdentityJson, "personal-profile");
        var mismatchedProfile = """
            {"viewer":{"auth_method":"sso","account_id":"210000","user_id":"12345","profile":"other-profile"},"items":[]}
            """;
        Assert.ThrowsExactly<ArkCodingPlanAcquisitionException>(() =>
            Parse(identity, mismatchedProfile, identity));

        var mismatchedAccount = """
            {"viewer":{"auth_method":"sso","account_id":"999999","user_id":"12345","profile":"personal-profile"},"items":[]}
            """;
        Assert.ThrowsExactly<ArkCodingPlanAcquisitionException>(() =>
            Parse(identity, mismatchedAccount, identity));

        var unsupportedAuth = Encoding.UTF8.GetBytes(
            "{\"logged_in\":true,\"auth_method\":\"sts\",\"account_id\":\"210000\",\"trn\":\"trn:iam::210000:root\"}");
        var exception = Assert.ThrowsExactly<ArkCodingPlanAcquisitionException>(() =>
            ArkCodingPlanQuotaParser.ParseIdentity(unsupportedAuth, "personal-profile", FingerprintKey));
        Assert.AreEqual("refresh_owner_unsupported", exception.ErrorCode);
    }

    private static ArkCodingPlanIdentity ParseIdentity(string json, string profile) =>
        ArkCodingPlanQuotaParser.ParseIdentity(Encoding.UTF8.GetBytes(json), profile, FingerprintKey);

    private static ArkCodingPlanQuotaReading Parse(ArkCodingPlanIdentity identity, string json) =>
        Parse(identity, json, identity);

    private static ArkCodingPlanQuotaReading Parse(ArkCodingPlanIdentity identity, string json,
        ArkCodingPlanIdentity expectedIdentity) =>
        ArkCodingPlanQuotaParser.Parse(Encoding.UTF8.GetBytes(json), ObservedAt, identity.Profile, expectedIdentity);
}
