#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerith.TelemetryService.Providers.AI.GitHubCopilot;

internal static class CopilotQuotaParser
{
    internal const int MaxResponseBytes = 1024 * 1024;
    internal const int MaxBuckets = 3;

    private static readonly HashSet<string> SupportedBuckets = new(StringComparer.Ordinal)
    {
        "chat",
        "completions",
        "premium_interactions"
    };

    internal static CopilotQuotaReading Parse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxResponseBytes)
            throw new CopilotQuotaParseException("response_too_large");

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            EnsureUniqueProperties(root);

            var accountId = ReadPositiveAccountId(root);
            var tokenBasedBilling = ReadRequiredBoolean(root, "token_based_billing");
            var accountKey = HashAccountId(accountId);
            var buckets = new Dictionary<string, CopilotQuotaBucket>(StringComparer.Ordinal);

            if (!root.TryGetProperty("quota_snapshots", out var rawBuckets) || rawBuckets.ValueKind == JsonValueKind.Null)
                return new CopilotQuotaReading(accountKey, tokenBasedBilling, buckets);

            if (rawBuckets.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            EnsureUniqueProperties(rawBuckets);

            foreach (var property in rawBuckets.EnumerateObject())
            {
                if (buckets.Count == MaxBuckets || !SupportedBuckets.Contains(property.Name) ||
                    buckets.ContainsKey(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                    throw new CopilotQuotaParseException("unsupported_bucket");
                EnsureUniqueProperties(property.Value);

                buckets.Add(property.Name, ParseBucket(property.Value, tokenBasedBilling));
            }

            return new CopilotQuotaReading(accountKey, tokenBasedBilling, buckets);
        }
        catch (CopilotQuotaParseException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidResponse();
        }
        catch (OverflowException)
        {
            throw InvalidResponse();
        }
    }

    private static CopilotQuotaBucket ParseBucket(JsonElement raw, bool tokenBasedBilling)
    {
        var unlimited = ReadRequiredBoolean(raw, "unlimited");
        var hasQuota = ReadOptionalBoolean(raw, "has_quota");
        if (raw.TryGetProperty("token_based_billing", out var bucketBilling) &&
            (bucketBilling.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
             bucketBilling.GetBoolean() != tokenBasedBilling))
            throw InvalidResponse();

        var entitlement = ReadOptionalFiniteNumber(raw, "entitlement");
        var creditsUsed = ReadOptionalNonNegativeNumber(raw, "credits_used");
        var quotaRemaining = ReadOptionalNonNegativeNumber(raw, "quota_remaining");
        var percentRemaining = ReadOptionalPercent(raw, "percent_remaining");
        var resetAt = ReadOptionalUnixSeconds(raw, "quota_reset_at");

        if (entitlement is < 0 && (!unlimited || entitlement != -1))
            throw InvalidResponse();

        if (entitlement is < 0) entitlement = null;

        return new CopilotQuotaBucket(entitlement, creditsUsed, quotaRemaining, percentRemaining,
            resetAt, unlimited, hasQuota);
    }

    private static long ReadPositiveAccountId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out var value) || value <= 0)
            throw new CopilotQuotaParseException("identity_unavailable");

        return value;
    }

    private static string HashAccountId(long accountId)
    {
        var material = Encoding.UTF8.GetBytes("github.com:copilot-user:" + accountId.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static bool ReadRequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw InvalidResponse();

        return value.GetBoolean();
    }

    private static bool? ReadOptionalBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw InvalidResponse();

        return value.GetBoolean();
    }

    private static double? ReadOptionalNonNegativeNumber(JsonElement parent, string name)
    {
        var number = ReadOptionalFiniteNumber(parent, name);
        if (number is < 0) throw InvalidResponse();
        return number;
    }

    private static double? ReadOptionalFiniteNumber(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        double number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out number)) throw InvalidResponse();
        }
        else if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
        }
        else
        {
            throw InvalidResponse();
        }

        if (!double.IsFinite(number)) throw InvalidResponse();
        return number;
    }

    private static double? ReadOptionalPercent(JsonElement parent, string name)
    {
        var value = ReadOptionalNonNegativeNumber(parent, name);
        if (value is double percent && percent > 100)
            throw InvalidResponse();
        return value;
    }

    private static DateTimeOffset? ReadOptionalUnixSeconds(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() / 1000d)
            throw InvalidResponse();

        return DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round(seconds * 1000d)));
    }

    private static void EnsureUniqueProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw InvalidResponse();
        }
    }

    private static CopilotQuotaParseException InvalidResponse() => new("response_invalid");
}

internal sealed record CopilotQuotaReading(
    string StableAccountKey,
    bool TokenBasedBilling,
    IReadOnlyDictionary<string, CopilotQuotaBucket> Buckets);

internal sealed record CopilotQuotaBucket(
    double? Entitlement,
    double? CreditsUsed,
    double? QuotaRemaining,
    double? PercentRemaining,
    DateTimeOffset? ResetAt,
    bool Unlimited,
    bool? HasQuota);

internal sealed class CopilotQuotaParseException(string errorCode) : Exception
{
    internal string ErrorCode { get; } = errorCode;
}
