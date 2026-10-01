#nullable enable

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Kiro;

internal readonly record struct KiroMetricPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static KiroMetricPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static KiroMetricPoint Unavailable(string? errorCode = null) =>
        new(MetricState.Unavailable, null, errorCode);
    public static KiroMetricPoint Error(string errorCode) => new(MetricState.Error, null, errorCode);
}

internal sealed record KiroBonusAllocation(decimal Remaining, DateTimeOffset? ExpiresAt);

internal sealed record KiroUsageReading(
    byte[] IdentityFingerprint,
    DateTimeOffset ObservedAt,
    KiroMetricPoint IncludedRemaining,
    KiroMetricPoint IncludedReset,
    KiroMetricPoint AddOnRemaining,
    KiroMetricPoint BonusRemaining,
    KiroBonusAllocation[]? BonusAllocations);

internal readonly record struct KiroParseResult(KiroUsageReading? Reading, string? ErrorCode)
{
    public static KiroParseResult Ready(KiroUsageReading reading) => new(reading, null);
    public static KiroParseResult Error(string code) => new(null, code);
}

internal static class KiroQuotaParser
{
    internal const int MaximumBonusAllocations = 32;
    private static readonly NumberStyles DecimalStyles = NumberStyles.AllowLeadingSign |
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    public static KiroParseResult Parse(ReadOnlyMemory<byte> payload, ReadOnlySpan<byte> identityKey,
        DateTimeOffset observedAt)
    {
        if (payload.Length is 0 or > KiroAcpUsageClient.MaxResponseBytes || identityKey.Length != 32)
            return KiroParseResult.Error("schema_invalid");

        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return KiroParseResult.Error("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return KiroParseResult.Error("schema_invalid");
            var identity = ReadIdentityFingerprint(root, identityKey);
            if (identity is null) return KiroParseResult.Error("identity_unavailable");

            var reset = ReadReset(root, observedAt);
            var included = ReadIncludedCredits(root, reset, observedAt);
            var addOn = ReadAddOnCredits(root);
            var bonus = ReadBonusCredits(root, observedAt, out var allocations);

            return KiroParseResult.Ready(new KiroUsageReading(identity, observedAt, included, reset,
                addOn, bonus, allocations));
        }
    }

    private static byte[]? ReadIdentityFingerprint(JsonElement root, ReadOnlySpan<byte> identityKey)
    {
        JsonElement identityObject = root;
        if (TryGetAlias(root, "userInfo", "user_info", out var userInfo, out var duplicate))
        {
            if (duplicate || userInfo.ValueKind != JsonValueKind.Object) return null;
            identityObject = userInfo;
        }

        var userId = ReadOptionalIdentifier(identityObject, "userId", "user_id", out var userInvalid);
        var accountId = ReadOptionalIdentifier(identityObject, "accountId", "account_id", out var accountInvalid);
        var profileArn = ReadOptionalIdentifier(identityObject, "profileArn", "profile_arn", out var profileInvalid);
        if (userInvalid || accountInvalid || profileInvalid) return null;

        if (userId is null && profileArn is null) return null;

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, identityKey);
        AppendIdentityPart(hmac, "user", userId);
        AppendIdentityPart(hmac, "account", accountId);
        AppendIdentityPart(hmac, "profile", profileArn);
        return hmac.GetHashAndReset();
    }

    private static void AppendIdentityPart(IncrementalHash hmac, string name, string? value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        hmac.AppendData(nameBytes);
        Span<byte> separator = stackalloc byte[1];
        separator[0] = 0;
        hmac.AppendData(separator);
        if (value is null)
        {
            Span<byte> missing = stackalloc byte[1];
            missing[0] = 0;
            hmac.AppendData(missing);
            return;
        }

        var valueBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, valueBytes.Length);
            hmac.AppendData(length);
            hmac.AppendData(valueBytes);
        }
        finally { CryptographicOperations.ZeroMemory(valueBytes); }
    }

    private static string? ReadOptionalIdentifier(JsonElement parent, string camelName, string snakeName,
        out bool invalid)
    {
        invalid = false;
        if (!TryGetAlias(parent, camelName, snakeName, out var value, out var duplicate)) return null;
        if (duplicate || value.ValueKind != JsonValueKind.String)
        {
            invalid = true;
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048)
        {
            invalid = true;
            return null;
        }

        return text;
    }

    private static KiroMetricPoint ReadReset(JsonElement root, DateTimeOffset observedAt)
    {
        if (!TryGetAlias(root, "subscriptionInfo", "subscription_info", out var subscription, out var duplicate))
            return KiroMetricPoint.Unavailable();
        if (duplicate || subscription.ValueKind != JsonValueKind.Object)
            return KiroMetricPoint.Error("schema_invalid");
        if (!TryGetAlias(subscription, "nextDateReset", "next_date_reset", out var reset, out duplicate))
            return KiroMetricPoint.Unavailable();
        if (duplicate || reset.ValueKind == JsonValueKind.Null)
            return duplicate ? KiroMetricPoint.Error("schema_invalid") : KiroMetricPoint.Unavailable();
        if (reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var unixSeconds))
            return KiroMetricPoint.Error("invalid_reset");

        DateTimeOffset resetAt;
        try { resetAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
        catch (ArgumentOutOfRangeException) { return KiroMetricPoint.Error("invalid_reset"); }
        return resetAt <= observedAt
            ? KiroMetricPoint.Unavailable("reset_elapsed")
            : KiroMetricPoint.Fresh(MetricValue.From(resetAt));
    }

    private static KiroMetricPoint ReadIncludedCredits(JsonElement root, KiroMetricPoint reset,
        DateTimeOffset observedAt)
    {
        if (!TryGetAlias(root, "usageBreakdownList", "usage_breakdown_list", out var breakdown, out var duplicate))
            return KiroMetricPoint.Unavailable();
        if (duplicate || breakdown.ValueKind != JsonValueKind.Array)
            return KiroMetricPoint.Error("schema_invalid");

        JsonElement credit = default;
        var creditCount = 0;
        foreach (var entry in breakdown.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) return KiroMetricPoint.Error("schema_invalid");
            if (!TryGetAlias(entry, "resourceType", "resource_type", out var resourceType, out duplicate) || duplicate ||
                resourceType.ValueKind != JsonValueKind.String)
                return KiroMetricPoint.Error("schema_invalid");
            if (!StringComparer.Ordinal.Equals(resourceType.GetString(), "CREDIT")) continue;
            credit = entry;
            creditCount++;
        }

        if (creditCount == 0) return KiroMetricPoint.Unavailable();
        if (creditCount != 1) return KiroMetricPoint.Error("quota_ambiguous");
        var hasUsed = TryReadPreferredDecimal(credit, "currentUsageWithPrecision", "current_usage_with_precision",
            "currentUsage", "current_usage", out var used, out var usedStatus);
        var hasLimit = TryReadPreferredDecimal(credit, "usageLimitWithPrecision", "usage_limit_with_precision",
            "usageLimit", "usage_limit", out var limit, out var limitStatus);
        if (!hasUsed || !hasLimit)
            return usedStatus == DecimalReadStatus.Invalid || limitStatus == DecimalReadStatus.Invalid
                ? KiroMetricPoint.Error("invalid_quota")
                : KiroMetricPoint.Unavailable();
        if (used < 0 || limit < 0) return KiroMetricPoint.Error("invalid_quota");
        if (reset.State == MetricState.Unavailable && reset.ErrorCode == "reset_elapsed")
            return KiroMetricPoint.Unavailable("reset_elapsed");

        var remaining = Math.Max(0m, limit - used);
        return KiroMetricPoint.Fresh(MetricValue.From(remaining));
    }

    private static KiroMetricPoint ReadAddOnCredits(JsonElement root)
    {
        if (!TryGetAlias(root, "addOnMetadata", "add_on_metadata", out var metadata, out var duplicate))
            return KiroMetricPoint.Unavailable();
        if (duplicate || metadata.ValueKind != JsonValueKind.Object)
            return KiroMetricPoint.Error("schema_invalid");
        var hasLimit = TryReadDecimal(metadata, "addOnTotalLimit", "add_on_total_limit", out var limit,
            out var limitStatus);
        var hasUsed = TryReadDecimal(metadata, "addOnTotalUsage", "add_on_total_usage", out var used,
            out var usedStatus);
        if (!hasLimit || !hasUsed)
            return limitStatus == DecimalReadStatus.Invalid || usedStatus == DecimalReadStatus.Invalid
                ? KiroMetricPoint.Error("invalid_addon")
                : KiroMetricPoint.Unavailable();
        if (limit < 0 || used < 0) return KiroMetricPoint.Error("invalid_addon");
        return KiroMetricPoint.Fresh(MetricValue.From(Math.Max(0m, limit - used)));
    }

    private static KiroMetricPoint ReadBonusCredits(JsonElement root, DateTimeOffset observedAt,
        out KiroBonusAllocation[]? allocations)
    {
        allocations = null;
        if (!TryGetAlias(root, "freeTrialInfo", "free_trial_info", out var trial, out var duplicate))
            return KiroMetricPoint.Unavailable();
        if (duplicate || trial.ValueKind != JsonValueKind.Object)
            return KiroMetricPoint.Error("schema_invalid");
        if (!TryGetAlias(trial, "bonuses", "bonuses", out var bonuses, out duplicate))
            return KiroMetricPoint.Unavailable();
        if (duplicate || bonuses.ValueKind != JsonValueKind.Array)
            return KiroMetricPoint.Error("schema_invalid");
        if (bonuses.GetArrayLength() > MaximumBonusAllocations)
            return KiroMetricPoint.Error("bonus_limit_exceeded");

        var parsed = new KiroBonusAllocation[bonuses.GetArrayLength()];
        var index = 0;
        decimal total = 0m;
        foreach (var bonus in bonuses.EnumerateArray())
        {
            if (bonus.ValueKind != JsonValueKind.Object)
                return KiroMetricPoint.Error("schema_invalid");
            var hasUsed = TryReadDecimal(bonus, "currentUsage", "current_usage", out var used,
                out var usedStatus);
            var hasLimit = TryReadDecimal(bonus, "usageLimit", "usage_limit", out var limit,
                out var limitStatus);
            if (!hasUsed || !hasLimit)
                return usedStatus == DecimalReadStatus.Invalid || limitStatus == DecimalReadStatus.Invalid
                    ? KiroMetricPoint.Error("invalid_bonus")
                    : KiroMetricPoint.Error("schema_invalid");
            if (used < 0 || limit < 0) return KiroMetricPoint.Error("invalid_bonus");

            DateTimeOffset? expiresAt = null;
            if (TryGetAlias(bonus, "expiresAt", "expires_at", out var expiry, out duplicate))
            {
                if (duplicate || !TryReadExpiry(expiry, observedAt, out var parsedExpiry))
                    return KiroMetricPoint.Error("invalid_bonus_expiry");
                expiresAt = parsedExpiry.ToUniversalTime();
            }

            var remaining = Math.Max(0m, limit - used);
            parsed[index++] = new KiroBonusAllocation(remaining, expiresAt);
            if (expiresAt is null || expiresAt > observedAt)
            {
                try { total = checked(total + remaining); }
                catch (OverflowException) { return KiroMetricPoint.Error("invalid_bonus"); }
            }
        }

        allocations = parsed;
        return KiroMetricPoint.Fresh(MetricValue.From(total));
    }

    private static bool TryReadPreferredDecimal(JsonElement parent, string preferredCamel, string preferredSnake,
        string fallbackCamel, string fallbackSnake, out decimal value, out DecimalReadStatus status)
    {
        if (TryGetAlias(parent, preferredCamel, preferredSnake, out var preferred, out var duplicate))
        {
            if (duplicate || !TryDecimalValue(preferred, out value))
            {
                value = 0;
                status = DecimalReadStatus.Invalid;
                return false;
            }
            status = DecimalReadStatus.Present;
            return true;
        }

        return TryReadDecimal(parent, fallbackCamel, fallbackSnake, out value, out status);
    }

    private static bool TryReadDecimal(JsonElement parent, string camelName, string snakeName,
        out decimal value, out DecimalReadStatus status)
    {
        value = 0;
        if (!TryGetAlias(parent, camelName, snakeName, out var element, out var duplicate))
        {
            status = DecimalReadStatus.Missing;
            return false;
        }
        if (duplicate || !TryDecimalValue(element, out value))
        {
            status = DecimalReadStatus.Invalid;
            return false;
        }
        status = DecimalReadStatus.Present;
        return true;
    }

    private static bool TryDecimalValue(JsonElement element, out decimal value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDecimal(out value);
        if (element.ValueKind != JsonValueKind.String) return false;
        var text = element.GetString();
        return text is not null && text.Length > 0 && StringComparer.Ordinal.Equals(text, text.Trim()) &&
            decimal.TryParse(text, DecimalStyles, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadExpiry(JsonElement element, DateTimeOffset observedAt,
        out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (element.ValueKind != JsonValueKind.String) return false;
        var text = element.GetString();
        if (string.IsNullOrEmpty(text)) return false;
        var hasUtcMarker = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var hasNumericOffset = text.Length >= 6 && (text[^6] is '+' or '-') && text[^3] == ':';
        if ((!hasUtcMarker && !hasNumericOffset) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out expiresAt))
            return false;
        try
        {
            _ = expiresAt - observedAt;
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool TryGetAlias(JsonElement parent, string camelName, string snakeName,
        out JsonElement value, out bool duplicate)
    {
        value = default;
        duplicate = false;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        var hasCamel = parent.TryGetProperty(camelName, out var camel);
        JsonElement snake = default;
        var hasSnake = !StringComparer.Ordinal.Equals(camelName, snakeName) &&
            parent.TryGetProperty(snakeName, out snake);
        duplicate = hasCamel && hasSnake;
        if (duplicate) return true;
        if (hasCamel) value = camel;
        else if (hasSnake) value = snake;
        return hasCamel || hasSnake;
    }

    private enum DecimalReadStatus
    {
        Missing,
        Present,
        Invalid
    }
}
