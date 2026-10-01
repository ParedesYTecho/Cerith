#nullable enable

using System;
using System.Text;
using Cerith.TelemetryService.Providers.AI.GitHubCopilot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.GitHubCopilot;

[TestClass]
public sealed class CopilotQuotaParserTests
{
    [TestMethod]
    public void TokenBasedSnapshotPreservesQuotaFieldsIndependentlyAndHashesAccountIdentity()
    {
        var reading = Parse("""
            {
              "id": 8642,
              "login": "synthetic-copilot-account",
              "token_based_billing": true,
              "quota_snapshots": {
                "chat": {
                  "token_based_billing": true,
                  "entitlement": 1200,
                  "credits_used": 123.75,
                  "quota_remaining": 1025,
                  "percent_remaining": 85.4,
                  "quota_reset_at": 1790812800,
                  "unlimited": false,
                  "has_quota": false
                }
              }
            }
            """);

        var chat = reading.Buckets["chat"];
        Assert.IsTrue(reading.TokenBasedBilling);
        Assert.AreEqual(1200d, chat.Entitlement);
        Assert.AreEqual(123.75d, chat.CreditsUsed);
        Assert.AreEqual(1025d, chat.QuotaRemaining);
        Assert.AreEqual(85.4d, chat.PercentRemaining);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1790812800), chat.ResetAt);
        Assert.IsFalse(chat.Unlimited);
        Assert.AreEqual(false, chat.HasQuota);
        Assert.AreNotEqual("8642", reading.StableAccountKey);
        Assert.IsFalse(reading.StableAccountKey.Contains("synthetic-copilot-account", StringComparison.Ordinal));
        Assert.AreEqual(reading.StableAccountKey, Parse(WithAccountId("8642")).StableAccountKey);
        Assert.AreNotEqual(reading.StableAccountKey, Parse(WithAccountId("8643")).StableAccountKey);
    }

    [TestMethod]
    public void LegacySnapshotDoesNotBecomeTokenBasedCredits()
    {
        var reading = Parse("""
            {
              "id": 8642,
              "token_based_billing": false,
              "quota_snapshots": {
                "premium_interactions": {
                  "token_based_billing": false,
                  "entitlement": 300,
                  "quota_remaining": 210,
                  "percent_remaining": 70,
                  "quota_reset_at": 1790812800,
                  "unlimited": false
                }
              }
            }
            """);

        var premium = reading.Buckets["premium_interactions"];
        Assert.IsFalse(reading.TokenBasedBilling);
        Assert.AreEqual(300d, premium.Entitlement);
        Assert.AreEqual(210d, premium.QuotaRemaining);
        Assert.IsNull(premium.CreditsUsed);
    }

    [TestMethod]
    public void MissingSnapshotsAndMissingFieldsRemainAbsent()
    {
        var noSnapshots = Parse("""{"id":8642,"token_based_billing":true}""");
        Assert.AreEqual(0, noSnapshots.Buckets.Count);

        var emptyBucket = Parse("""
            {"id":8642,"token_based_billing":true,"quota_snapshots":{"chat":{"unlimited":false}}}
            """);
        var chat = emptyBucket.Buckets["chat"];
        Assert.IsNull(chat.Entitlement);
        Assert.IsNull(chat.CreditsUsed);
        Assert.IsNull(chat.QuotaRemaining);
        Assert.IsNull(chat.PercentRemaining);
        Assert.IsNull(chat.ResetAt);
        Assert.IsNull(chat.HasQuota);

        var explicitZero = Parse("""
            {"id":8642,"token_based_billing":true,"quota_snapshots":{"chat":{"entitlement":0,"credits_used":0,"quota_remaining":0,"percent_remaining":0,"unlimited":false}}}
            """);
        Assert.AreEqual(0d, explicitZero.Buckets["chat"].Entitlement);
        Assert.AreEqual(0d, explicitZero.Buckets["chat"].CreditsUsed);
        Assert.AreEqual(0d, explicitZero.Buckets["chat"].QuotaRemaining);
    }

    [TestMethod]
    public void ExplicitUnlimitedIsNotConvertedToAnUnlimitedNumber()
    {
        var reading = Parse("""
            {"id":8642,"token_based_billing":true,"quota_snapshots":{"chat":{"entitlement":-1,"unlimited":true}}}
            """);

        Assert.IsTrue(reading.Buckets["chat"].Unlimited);
        Assert.IsNull(reading.Buckets["chat"].Entitlement);
    }

    [TestMethod]
    public void InvalidNumbersBillingMismatchUnknownBucketAndDuplicateFieldsFailClosed()
    {
        AssertParseError("response_invalid", """{"id":8642,"token_based_billing":true,"quota_snapshots":{"chat":{"unlimited":false,"credits_used":-1}}}""");
        AssertParseError("response_invalid", """{"id":8642,"token_based_billing":true,"quota_snapshots":{"chat":{"unlimited":false,"entitlement":-1}}}""");
        AssertParseError("response_invalid", """{"id":8642,"token_based_billing":true,"quota_snapshots":{"chat":{"unlimited":false,"percent_remaining":100.1}}}""");
        AssertParseError("response_invalid", """{"id":8642,"token_based_billing":true,"quota_snapshots":{"chat":{"token_based_billing":false,"unlimited":false}}}""");
        AssertParseError("unsupported_bucket", """{"id":8642,"token_based_billing":true,"quota_snapshots":{"future_bucket":{"unlimited":false}}}""");
        AssertParseError("response_invalid", """{"id":8642,"id":8643,"token_based_billing":true}""");
        AssertParseError("identity_unavailable", """{"token_based_billing":true}""");
        AssertParseError("response_invalid", """{"id":8642,"token_based_billing":"true"}""");
    }

    [TestMethod]
    public void OversizedAndMalformedJsonAreRejectedWithSanitizedCodes()
    {
        var tooLarge = new string(' ', CopilotQuotaParser.MaxResponseBytes + 1);
        AssertParseError("response_too_large", tooLarge);
        AssertParseError("response_invalid", "{\"id\":");
    }

    private static CopilotQuotaReading Parse(string json) =>
        CopilotQuotaParser.Parse(Encoding.UTF8.GetBytes(json));

    private static string WithAccountId(string id) =>
        "{\"id\":" + id + ",\"token_based_billing\":true}";

    private static void AssertParseError(string expectedCode, string json)
    {
        var exception = Assert.ThrowsExactly<CopilotQuotaParseException>(() => Parse(json));
        Assert.AreEqual(expectedCode, exception.ErrorCode);
    }
}
