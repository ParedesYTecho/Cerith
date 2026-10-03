#nullable enable

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal readonly record struct ArkCodingPlanQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static ArkCodingPlanQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static ArkCodingPlanQuotaPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static ArkCodingPlanQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record ArkCodingPlanQuotaReading(ArkCodingPlanQuotaPoint[] Points, string OwnerFingerprint)
{
    public static ArkCodingPlanQuotaReading Failure(string code, string ownerFingerprint) =>
        new(EnumerablePoints(code), ownerFingerprint);

    private static ArkCodingPlanQuotaPoint[] EnumerablePoints(string code) =>
    [
        ArkCodingPlanQuotaPoint.Error(code), ArkCodingPlanQuotaPoint.Error(code),
        ArkCodingPlanQuotaPoint.Error(code), ArkCodingPlanQuotaPoint.Error(code),
        ArkCodingPlanQuotaPoint.Error(code), ArkCodingPlanQuotaPoint.Error(code)
    ];
}

internal readonly record struct ArkCodingPlanIdentity(
    string Profile,
    string AuthMethod,
    string AccountId,
    string? UserId,
    string Trn,
    bool? IsRoot,
    string Fingerprint)
{
    public bool SamePrincipal(ArkCodingPlanIdentity other) =>
        StringComparer.Ordinal.Equals(Profile, other.Profile) &&
        StringComparer.Ordinal.Equals(AuthMethod, other.AuthMethod) &&
        StringComparer.Ordinal.Equals(AccountId, other.AccountId) &&
        StringComparer.Ordinal.Equals(UserId, other.UserId) &&
        StringComparer.Ordinal.Equals(Trn, other.Trn) &&
        IsRoot == other.IsRoot;
}

internal static class ArkCodingPlanQuotaParser
{
    internal const int SessionUsed = 0;
    internal const int WeeklyUsed = 1;
    internal const int MonthlyUsed = 2;
    internal const int SessionReset = 3;
    internal const int WeeklyReset = 4;
    internal const int MonthlyReset = 5;
    internal const int PointCount = 6;
    internal const int MaximumPayloadBytes = 128 * 1024;

    private static readonly string[] PeriodLabels = ["session", "weekly", "monthly"];

    public static ArkCodingPlanIdentity ParseIdentity(ReadOnlyMemory<byte> payload, string profile,
        ReadOnlySpan<byte> fingerprintKey)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes)
            throw new ArkCodingPlanAcquisitionException("identity_invalid");

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetBoolean(document.RootElement, "logged_in", out var loggedIn) || !loggedIn)
                throw new ArkCodingPlanAcquisitionException("auth_required");

            var authMethod = ReadString(document.RootElement, "auth_method", required: true);
            if (!StringComparer.Ordinal.Equals(authMethod, "sso"))
                throw new ArkCodingPlanAcquisitionException("refresh_owner_unsupported");

            var accountId = ReadString(document.RootElement, "account_id", required: true);
            var userId = ReadString(document.RootElement, "user_id", required: false);
            var trn = ReadString(document.RootElement, "trn", required: true);
            bool? isRoot = TryGetBoolean(document.RootElement, "is_root", out var root) ? root : null;
            return CreateIdentity(profile, authMethod, accountId, userId, trn, isRoot, fingerprintKey);
        }
        catch (JsonException)
        {
            throw new ArkCodingPlanAcquisitionException("identity_invalid");
        }
    }

    public static ArkCodingPlanQuotaReading Parse(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt,
        string profile, ArkCodingPlanIdentity expectedIdentity)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes)
            return ArkCodingPlanQuotaReading.Failure("schema_invalid", expectedIdentity.Fingerprint);

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("viewer", out var viewer) || viewer.ValueKind != JsonValueKind.Object)
                return ArkCodingPlanQuotaReading.Failure("schema_invalid", expectedIdentity.Fingerprint);

            if (!ViewerMatchesIdentity(viewer, profile, expectedIdentity))
                throw new ArkCodingPlanAcquisitionException("identity_changed");

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return ArkCodingPlanQuotaReading.Failure("schema_invalid", expectedIdentity.Fingerprint);

            JsonElement codingPlan = default;
            var matchCount = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String)
                    continue;
                if (!StringComparer.Ordinal.Equals(product.GetString(), "coding-plan")) continue;
                codingPlan = item;
                matchCount++;
            }

            if (matchCount != 1)
                return ArkCodingPlanQuotaReading.Failure("product_missing", expectedIdentity.Fingerprint);

            if (codingPlan.TryGetProperty("edition", out var edition) &&
                (edition.ValueKind != JsonValueKind.String ||
                 !StringComparer.Ordinal.Equals(edition.GetString(), "personal")))
                return ArkCodingPlanQuotaReading.Failure("scope_unsupported", expectedIdentity.Fingerprint);

            if (!TryGetBoolean(codingPlan, "subscribed", out var subscribed))
                return ArkCodingPlanQuotaReading.Failure("schema_invalid", expectedIdentity.Fingerprint);
            if (!subscribed)
                return Missing("not_subscribed", expectedIdentity.Fingerprint);

            if (codingPlan.TryGetProperty("error", out var error) &&
                error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return Failure("source_error", expectedIdentity.Fingerprint);

            if (!codingPlan.TryGetProperty("periods", out var periods) || periods.ValueKind != JsonValueKind.Array)
                return Missing("periods_missing", expectedIdentity.Fingerprint);

            return ParsePeriods(periods, observedAt, expectedIdentity.Fingerprint);
        }
        catch (JsonException)
        {
            return ArkCodingPlanQuotaReading.Failure("schema_invalid", expectedIdentity.Fingerprint);
        }
    }

    private static bool ViewerMatchesIdentity(JsonElement viewer, string profile,
        ArkCodingPlanIdentity expectedIdentity)
    {
        var viewerProfile = ReadString(viewer, "profile", required: true);
        if (!StringComparer.Ordinal.Equals(viewerProfile, profile))
            return false;

        var authMethod = ReadString(viewer, "auth_method", required: true);
        if (!StringComparer.Ordinal.Equals(authMethod, "sso"))
            throw new ArkCodingPlanAcquisitionException("refresh_owner_unsupported");

        var accountId = ReadString(viewer, "account_id", required: true);
        var userId = ReadString(viewer, "user_id", required: false);
        var trn = ReadString(viewer, "trn", required: false);
        bool? isRoot = TryGetBoolean(viewer, "is_root", out var root) ? root : null;
        return StringComparer.Ordinal.Equals(authMethod, expectedIdentity.AuthMethod) &&
               StringComparer.Ordinal.Equals(accountId, expectedIdentity.AccountId) &&
               StringComparer.Ordinal.Equals(userId, expectedIdentity.UserId) &&
               (trn.Length == 0 || StringComparer.Ordinal.Equals(trn, expectedIdentity.Trn)) &&
               (!isRoot.HasValue || isRoot == expectedIdentity.IsRoot);
    }

    private static ArkCodingPlanIdentity CreateIdentity(string profile, string authMethod, string accountId,
        string? userId, string trn, bool? isRoot, ReadOnlySpan<byte> fingerprintKey)
    {
        var canonical = string.Join('\n', profile, authMethod, accountId, userId ?? string.Empty, trn,
            isRoot?.ToString() ?? string.Empty);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            var fingerprint = Convert.ToHexString(HMACSHA256.HashData(fingerprintKey, bytes));
            return new ArkCodingPlanIdentity(profile, authMethod, accountId, userId, trn, isRoot, fingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static ArkCodingPlanQuotaReading ParsePeriods(JsonElement periods, DateTimeOffset observedAt,
        string ownerFingerprint)
    {
        var points = new ArkCodingPlanQuotaPoint[PointCount];
        Array.Fill(points, ArkCodingPlanQuotaPoint.Unavailable());
        var seen = new bool[PeriodLabels.Length];

        foreach (var period in periods.EnumerateArray())
        {
            if (period.ValueKind != JsonValueKind.Object ||
                !period.TryGetProperty("label", out var labelElement) || labelElement.ValueKind != JsonValueKind.String)
                continue;

            var label = labelElement.GetString();
            var index = Array.IndexOf(PeriodLabels, label);
            if (index < 0) continue;
            if (seen[index])
            {
                points[index] = ArkCodingPlanQuotaPoint.Error("schema_invalid");
                points[index + PeriodLabels.Length] = ArkCodingPlanQuotaPoint.Error("schema_invalid");
                continue;
            }
            seen[index] = true;

            var used = ParsePercent(period);
            var reset = ParseReset(period);
            if (reset.Value is MetricValue resetValue && resetValue.AsDateTimeOffset() <= observedAt)
            {
                points[index] = ArkCodingPlanQuotaPoint.Unavailable("reset_elapsed");
                points[index + PeriodLabels.Length] = ArkCodingPlanQuotaPoint.Unavailable("reset_elapsed");
            }
            else
            {
                points[index] = used;
                points[index + PeriodLabels.Length] = reset;
            }
        }

        return new ArkCodingPlanQuotaReading(points, ownerFingerprint);
    }

    private static ArkCodingPlanQuotaPoint ParsePercent(JsonElement period)
    {
        if (!period.TryGetProperty("percent", out var percent) ||
            percent.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ArkCodingPlanQuotaPoint.Unavailable();
        if (percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value is < 0 or > 100)
            return ArkCodingPlanQuotaPoint.Error("invalid_quota");
        return ArkCodingPlanQuotaPoint.Fresh(MetricValue.From(value));
    }

    private static ArkCodingPlanQuotaPoint ParseReset(JsonElement period)
    {
        if (!period.TryGetProperty("reset_at", out var reset) ||
            reset.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ArkCodingPlanQuotaPoint.Unavailable();
        if (reset.ValueKind != JsonValueKind.String)
            return ArkCodingPlanQuotaPoint.Error("invalid_reset");

        var text = reset.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 || !HasExplicitOffset(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return ArkCodingPlanQuotaPoint.Error("invalid_reset");
        return ArkCodingPlanQuotaPoint.Fresh(MetricValue.From(value));
    }

    private static bool HasExplicitOffset(string text)
    {
        if (text.Length < 20 || text.IndexOf('T') < 0) return false;
        if (text.EndsWith('Z') || text.EndsWith('z')) return true;
        var timeSeparator = text.IndexOf('T');
        var plus = text.LastIndexOf('+');
        var minus = text.LastIndexOf('-');
        var offsetStart = Math.Max(plus, minus);
        return offsetStart > timeSeparator && text.Length - offsetStart == 6 &&
               char.IsAsciiDigit(text[offsetStart + 1]) && char.IsAsciiDigit(text[offsetStart + 2]) &&
               text[offsetStart + 3] == ':' && char.IsAsciiDigit(text[offsetStart + 4]) &&
               char.IsAsciiDigit(text[offsetStart + 5]);
    }

    private static string ReadString(JsonElement element, string propertyName, bool required)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!required) return string.Empty;
            throw new ArkCodingPlanAcquisitionException("identity_invalid");
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new ArkCodingPlanAcquisitionException("identity_invalid");

        var text = value.GetString();
        if (text is null || text.Length > 512 ||
            (required && string.IsNullOrWhiteSpace(text)) || ContainsControlCharacter(text))
            throw new ArkCodingPlanAcquisitionException("identity_invalid");
        return text;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character)) return true;
        return false;
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName, out bool value)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    private static ArkCodingPlanQuotaReading Missing(string code, string ownerFingerprint) =>
        new(
        [
            ArkCodingPlanQuotaPoint.Unavailable(code), ArkCodingPlanQuotaPoint.Unavailable(code),
            ArkCodingPlanQuotaPoint.Unavailable(code), ArkCodingPlanQuotaPoint.Unavailable(code),
            ArkCodingPlanQuotaPoint.Unavailable(code), ArkCodingPlanQuotaPoint.Unavailable(code)
        ], ownerFingerprint);

    private static ArkCodingPlanQuotaReading Failure(string code, string ownerFingerprint) =>
        ArkCodingPlanQuotaReading.Failure(code, ownerFingerprint);
}

internal sealed class ArkCodingPlanAcquisitionException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
