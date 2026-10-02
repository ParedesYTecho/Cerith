#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.ArkAgentPlan;

internal readonly record struct ArkAgentPlanPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static ArkAgentPlanPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static ArkAgentPlanPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static ArkAgentPlanPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record ArkAgentPlanIdentity(
    string AuthMethod,
    bool IsRoot,
    string? UserId,
    string? AccountId,
    string? Region,
    string? ProjectName,
    string? Profile = null)
{
    public bool CanCompareTo(ArkAgentPlanIdentity current) =>
        AccountId is not null && current.AccountId is not null &&
        (UserId is null) == (current.UserId is null) &&
        (Region is null) == (current.Region is null) &&
        (ProjectName is null) == (current.ProjectName is null) &&
        (Profile is null) == (current.Profile is null);

    public bool MatchesCurrent(ArkAgentPlanIdentity current) =>
        StringComparer.Ordinal.Equals(AuthMethod, current.AuthMethod) &&
        IsRoot == current.IsRoot &&
        StringComparer.Ordinal.Equals(AccountId, current.AccountId) &&
        StringComparer.Ordinal.Equals(UserId, current.UserId) &&
        StringComparer.Ordinal.Equals(Region, current.Region) &&
        StringComparer.Ordinal.Equals(ProjectName, current.ProjectName) &&
        StringComparer.Ordinal.Equals(Profile, current.Profile);
}

internal sealed record ArkAgentPlanReading(
    ArkAgentPlanIdentity? Identity,
    ArkAgentPlanPoint[] Points,
    bool IsSubscribed = false,
    bool HasDailyPeriod = false,
    string? Tier = null)
{
    internal const int MetricsPerPeriod = 4;
    internal static readonly string[] Periods = ["5h", "daily", "weekly", "monthly"];

    public static ArkAgentPlanReading Failure(string code) =>
        new(null, Enumerable.Repeat(ArkAgentPlanPoint.Error(code), Periods.Length * MetricsPerPeriod).ToArray());

    public static ArkAgentPlanReading Missing(ArkAgentPlanIdentity? identity = null) =>
        new(identity, Enumerable.Repeat(ArkAgentPlanPoint.Unavailable(), Periods.Length * MetricsPerPeriod).ToArray());
}

internal static class ArkAgentPlanParser
{
    internal const int MaxPayloadBytes = 1024 * 1024;
    private static readonly Regex OffsetTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ArkAgentPlanReading Parse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes) return ArkAgentPlanReading.Failure("response_too_large");

        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return ArkAgentPlanReading.Failure("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("viewer", out var viewer) ||
                !TryReadIdentity(viewer, out var identity))
                return ArkAgentPlanReading.Failure("identity_unverified");

            if (!root.TryGetProperty("items", out var items) || items.ValueKind == JsonValueKind.Null)
                return ArkAgentPlanReading.Missing(identity);
            if (items.ValueKind != JsonValueKind.Array)
                return ArkAgentPlanReading.Failure("schema_invalid");

            JsonElement selected = default;
            var found = false;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("product", out var product) ||
                    product.ValueKind != JsonValueKind.String)
                    return ArkAgentPlanReading.Failure("schema_invalid");
                if (!StringComparer.Ordinal.Equals(product.GetString(), "agent-plan")) continue;
                if (found) return ArkAgentPlanReading.Failure("schema_invalid");
                selected = item;
                found = true;
            }

            if (!found) return ArkAgentPlanReading.Missing(identity);
            if (!selected.TryGetProperty("subscribed", out var subscribed) ||
                subscribed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return ArkAgentPlanReading.Failure("schema_invalid");
            if (!subscribed.GetBoolean()) return ArkAgentPlanReading.Missing(identity);
            if (!TryReadOptionalString(selected, "tier", out var tier))
                return ArkAgentPlanReading.Failure("schema_invalid");

            var points = Enumerable.Repeat(ArkAgentPlanPoint.Unavailable(),
                ArkAgentPlanReading.Periods.Length * ArkAgentPlanReading.MetricsPerPeriod).ToArray();
            if (!selected.TryGetProperty("periods", out var periods) || periods.ValueKind == JsonValueKind.Null)
                return new ArkAgentPlanReading(identity, points, true, false, tier);
            if (periods.ValueKind != JsonValueKind.Array)
                return ArkAgentPlanReading.Failure("schema_invalid");

            var seen = new bool[ArkAgentPlanReading.Periods.Length];
            foreach (var period in periods.EnumerateArray())
            {
                if (period.ValueKind != JsonValueKind.Object ||
                    !period.TryGetProperty("label", out var label) ||
                    label.ValueKind != JsonValueKind.String)
                    return ArkAgentPlanReading.Failure("schema_invalid");
                var index = Array.IndexOf(ArkAgentPlanReading.Periods, label.GetString());
                if (index < 0) continue;
                if (seen[index]) return ArkAgentPlanReading.Failure("schema_invalid");
                seen[index] = true;
                ParsePeriod(period, points, index * ArkAgentPlanReading.MetricsPerPeriod);
            }

            return new ArkAgentPlanReading(identity, points, true, seen[1], tier);
        }
    }

    public static ArkAgentPlanPoint[] ParseDaily(ReadOnlyMemory<byte> payload,
        ArkAgentPlanIdentity expectedIdentity, string? expectedTier)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (payload.Length > MaxPayloadBytes) return DailyFailure("response_too_large");

        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return DailyFailure("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ResponseMetadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
                !TryReadRequiredString(metadata, "Action", out var action) ||
                !TryReadRequiredString(metadata, "Service", out var service) ||
                !TryReadRequiredString(metadata, "Version", out var version) ||
                !TryReadRequiredString(metadata, "Region", out var region))
                return DailyFailure("schema_invalid");
            if (metadata.TryGetProperty("Error", out var apiError) && apiError.ValueKind != JsonValueKind.Null)
                return DailyFailure("api_failed");

            if (!StringComparer.Ordinal.Equals(action, "GetAFPUsage") ||
                !StringComparer.Ordinal.Equals(service, "ark") ||
                !StringComparer.Ordinal.Equals(version, "2024-01-01"))
                return DailyFailure("source_unverified");
            if (string.IsNullOrWhiteSpace(expectedIdentity.Region) ||
                !StringComparer.Ordinal.Equals(region, expectedIdentity.Region))
                return DailyFailure("identity_unverified");
            if (!root.TryGetProperty("Result", out var result) || result.ValueKind != JsonValueKind.Object ||
                !TryReadRequiredString(result, "PlanType", out var planType))
                return DailyFailure("schema_invalid");
            if (expectedTier is not null &&
                !StringComparer.OrdinalIgnoreCase.Equals(planType, expectedTier))
                return DailyFailure("scope_mismatch");
            if (!result.TryGetProperty("AFPDaily", out var daily) || daily.ValueKind == JsonValueKind.Null)
                return Enumerable.Repeat(ArkAgentPlanPoint.Unavailable(), 4).ToArray();
            if (daily.ValueKind != JsonValueKind.Object)
                return DailyFailure("schema_invalid");

            var used = ReadNonNegativeDecimalString(daily, "Used");
            var total = ReadNonNegativeDecimalString(daily, "Quota");
            return
            [
                used,
                total,
                DerivePercent(used, total),
                ReadUnixMilliseconds(daily, "ResetTime")
            ];
        }
    }

    private static ArkAgentPlanPoint[] DailyFailure(string code) =>
        Enumerable.Repeat(ArkAgentPlanPoint.Error(code), 4).ToArray();

    private static bool TryReadIdentity(JsonElement viewer, out ArkAgentPlanIdentity? identity)
    {
        identity = null;
        if (viewer.ValueKind != JsonValueKind.Object ||
            !TryReadRequiredString(viewer, "auth_method", out var authMethod) ||
            !TryReadRequiredBoolean(viewer, "is_root", out var isRoot) ||
            !TryReadOptionalString(viewer, "user_id", out var userId) ||
            !TryReadRequiredString(viewer, "account_id", out var accountId) ||
            !TryReadOptionalString(viewer, "region", out var region) ||
            !TryReadOptionalString(viewer, "project_name", out var projectName) ||
            !TryReadOptionalString(viewer, "profile", out var profile))
            return false;

        var hasUser = !string.IsNullOrWhiteSpace(userId);
        if (StringComparer.OrdinalIgnoreCase.Equals(authMethod, "sso") &&
            !isRoot && !hasUser)
            return false;

        identity = new ArkAgentPlanIdentity(authMethod, isRoot, userId, accountId, region, projectName, profile);
        return true;
    }

    private static bool TryReadRequiredString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        value = text;
        return true;
    }

    private static bool TryReadRequiredBoolean(JsonElement owner, string name, out bool value)
    {
        value = false;
        if (!owner.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadOptionalString(JsonElement owner, string name, out string? value)
    {
        value = null;
        if (!owner.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return value is null || !string.IsNullOrWhiteSpace(value);
    }

    private static void ParsePeriod(JsonElement period, ArkAgentPlanPoint[] points, int offset)
    {
        points[offset] = ReadNonNegativeDecimal(period, "used");
        points[offset + 1] = ReadNonNegativeDecimal(period, "total");
        points[offset + 2] = ReadPercent(period);
        points[offset + 3] = ReadReset(period);
    }

    private static ArkAgentPlanPoint ReadNonNegativeDecimal(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return ArkAgentPlanPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0)
            return ArkAgentPlanPoint.Error("invalid_quota");
        return ArkAgentPlanPoint.Fresh(MetricValue.From(number));
    }

    private static ArkAgentPlanPoint ReadPercent(JsonElement owner)
    {
        if (!owner.TryGetProperty("percent", out var value) || value.ValueKind == JsonValueKind.Null)
            return ArkAgentPlanPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) ||
            number is < 0 or > 100)
            return ArkAgentPlanPoint.Error("invalid_quota");
        return ArkAgentPlanPoint.Fresh(MetricValue.From(number));
    }

    private static ArkAgentPlanPoint ReadNonNegativeDecimalString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return ArkAgentPlanPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 64 } text ||
            !HasAtMost28SignificantDigits(text) ||
            !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
                NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number) || number < 0 ||
            (number == 0 && HasNonZeroSignificand(text)))
            return ArkAgentPlanPoint.Error("invalid_quota");
        return ArkAgentPlanPoint.Fresh(MetricValue.From(number));
    }

    private static bool HasAtMost28SignificantDigits(string text)
    {
        var exponent = text.IndexOfAny(['e', 'E']);
        var mantissaLength = exponent < 0 ? text.Length : exponent;
        var firstNonZero = -1;
        var lastNonZero = -1;
        for (var index = 0; index < mantissaLength; index++)
        {
            if (text[index] is < '1' or > '9') continue;
            if (firstNonZero < 0) firstNonZero = index;
            lastNonZero = index;
        }
        if (firstNonZero < 0) return true;

        var significantDigits = 0;
        for (var index = firstNonZero; index <= lastNonZero; index++)
            if (char.IsAsciiDigit(text[index])) significantDigits++;
        return significantDigits <= 28;
    }

    private static bool HasNonZeroSignificand(string text)
    {
        var exponent = text.IndexOfAny(['e', 'E']);
        var mantissaLength = exponent < 0 ? text.Length : exponent;
        for (var index = 0; index < mantissaLength; index++)
            if (text[index] is >= '1' and <= '9') return true;
        return false;
    }

    private static ArkAgentPlanPoint DerivePercent(ArkAgentPlanPoint used, ArkAgentPlanPoint total)
    {
        if (used.State == MetricState.Error || total.State == MetricState.Error)
            return ArkAgentPlanPoint.Error("invalid_quota");
        if (!used.Value.HasValue || !total.Value.HasValue || total.Value.Value.AsDecimal() == 0)
            return ArkAgentPlanPoint.Unavailable();

        try
        {
            var percent = used.Value.Value.AsDecimal() / total.Value.Value.AsDecimal() * 100m;
            return ArkAgentPlanPoint.Fresh(MetricValue.From(percent));
        }
        catch (OverflowException)
        {
            return ArkAgentPlanPoint.Error("invalid_quota");
        }
    }

    private static ArkAgentPlanPoint ReadUnixMilliseconds(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return ArkAgentPlanPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var milliseconds))
            return ArkAgentPlanPoint.Error("invalid_reset");
        try
        {
            return ArkAgentPlanPoint.Fresh(MetricValue.From(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ArkAgentPlanPoint.Error("invalid_reset");
        }
    }

    private static ArkAgentPlanPoint ReadReset(JsonElement owner)
    {
        if (!owner.TryGetProperty("reset_at", out var value) || value.ValueKind == JsonValueKind.Null)
            return ArkAgentPlanPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.String) return ArkAgentPlanPoint.Error("invalid_reset");
        var text = value.GetString();
        if (text is null || !OffsetTimestamp.IsMatch(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var resetAt))
            return ArkAgentPlanPoint.Error("invalid_reset");
        return ArkAgentPlanPoint.Fresh(MetricValue.From(resetAt));
    }
}
