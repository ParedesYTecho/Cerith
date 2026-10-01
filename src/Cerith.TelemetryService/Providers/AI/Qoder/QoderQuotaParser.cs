#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Qoder;

internal readonly record struct QoderQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static QoderQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static QoderQuotaPoint Unavailable(string? errorCode = null) => new(MetricState.Unavailable, null, errorCode);
    public static QoderQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record QoderSessionIdentity(string AccountId, string? OrganizationId);

internal sealed record QoderQuotaReading(QoderQuotaPoint[] Points, DateTimeOffset ObservedAt,
    string AccountId, string? OrganizationId)
{
    public static QoderQuotaReading Failure(string code, MetricState state, DateTimeOffset observedAt) =>
        new(Enumerable.Repeat(state == MetricState.Error ? QoderQuotaPoint.Error(code) :
            QoderQuotaPoint.Unavailable(code), QoderQuotaParser.MetricCount).ToArray(), observedAt, string.Empty, null);
}

internal static class QoderQuotaParser
{
    internal const int MetricCount = 16;
    internal const int MaxJsonDepth = 20;
    // TokenTracker's pinned Qoder test identifies this exact epoch value as no expiry.
    internal const long NoExpiryEpochMilliseconds = 253402214400000;

    internal static readonly string[] MetricIds =
    [
        "ai.qoder.quota.personal.used_percent",
        "ai.qoder.credits.personal.used",
        "ai.qoder.credits.personal.remaining",
        "ai.qoder.credits.personal.limit",
        "ai.qoder.quota.addon.used_percent",
        "ai.qoder.credits.addon.used",
        "ai.qoder.credits.addon.remaining",
        "ai.qoder.credits.addon.limit",
        "ai.qoder.quota.organization.used_percent",
        "ai.qoder.credits.organization.used",
        "ai.qoder.credits.organization.remaining",
        "ai.qoder.credits.organization.limit",
        "ai.qoder.quota.organization.available",
        "ai.qoder.quota.overall.used_percent",
        "ai.qoder.quota.exceeded",
        "ai.qoder.plan.expires_at"
    ];

    internal static bool TryParseAuthStatus(ReadOnlyMemory<byte> payload, out QoderSessionIdentity? identity)
    {
        identity = null;
        try
        {
            var reader = new Utf8JsonReader(payload.Span, new JsonReaderOptions { MaxDepth = MaxJsonDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            string? id = null;
            string? accountId = null;
            string? organizationId = null;
            var sawId = false;
            var sawAccountId = false;
            var sawOrganizationId = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return false;
                var isId = reader.ValueTextEquals("id");
                var isAccountId = reader.ValueTextEquals("accountId");
                var isOrganizationId = reader.ValueTextEquals("orgId");
                if (!reader.Read())
                    return false;

                if (isId || isAccountId || isOrganizationId)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        if (reader.TokenType != JsonTokenType.Null || isId || isAccountId)
                            return false;
                    }
                    var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    if (isId)
                    {
                        if (sawId)
                            return false;
                        id = value;
                        sawId = true;
                    }
                    else if (isAccountId)
                    {
                        if (sawAccountId)
                            return false;
                        accountId = value;
                        sawAccountId = true;
                    }
                    else
                    {
                        if (sawOrganizationId)
                            return false;
                        organizationId = value;
                        sawOrganizationId = true;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
                return false;
            if (id is not null && accountId is not null && !StringComparer.Ordinal.Equals(id, accountId))
                return false;

            var selectedId = id ?? accountId;
            if (string.IsNullOrWhiteSpace(selectedId))
                return false;

            if (string.IsNullOrWhiteSpace(organizationId))
                organizationId = null;
            identity = new QoderSessionIdentity(selectedId, organizationId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static QoderQuotaReading Parse(ReadOnlyMemory<byte> payload, QoderSessionIdentity identity,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException)
        {
            return QoderQuotaReading.Failure("schema_invalid", MetricState.Error, observedAt);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return QoderQuotaReading.Failure("schema_invalid", MetricState.Error, observedAt);

            if (root.TryGetProperty("userId", out var userId))
            {
                if (userId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(userId.GetString()) ||
                    !StringComparer.Ordinal.Equals(userId.GetString(), identity.AccountId))
                    return QoderQuotaReading.Failure("identity_unavailable", MetricState.Unavailable, observedAt);
            }

            if (identity.AccountId.Length == 0)
                return QoderQuotaReading.Failure("identity_unavailable", MetricState.Unavailable, observedAt);

            var points = Enumerable.Repeat(QoderQuotaPoint.Unavailable(), MetricCount).ToArray();
            if (TryGetObject(root, "userQuota", out var personal))
            {
                points[0] = ReadPercent(personal, "percentage");
                ReadCreditBucket(personal, "total", 1, 2, 3, points);
            }

            if (TryGetObject(root, "addOnQuota", out var addOn))
            {
                points[4] = ReadPercent(addOn, "percentage");
                ReadCreditBucket(addOn, "total", 5, 6, 7, points);
            }

            if (identity.OrganizationId is not null && TryGetObject(root, "orgResourcePackage", out var organization))
            {
                points[8] = ReadPercent(organization, "percentage");
                ReadCreditBucket(organization, "cap", 9, 10, 11, points);
                points[12] = ReadBoolean(organization, "available");
            }
            else if (TryGetObject(root, "orgResourcePackage", out _))
            {
                for (var index = 8; index <= 12; index++)
                    points[index] = QoderQuotaPoint.Unavailable("organization_identity_unavailable");
            }

            points[13] = ReadPercent(root, "totalUsagePercentage");
            points[14] = ReadBoolean(root, "isQuotaExceeded");
            points[15] = ReadPlanExpiry(root, "expiresAt");
            return new QoderQuotaReading(points, observedAt, identity.AccountId, identity.OrganizationId);
        }
    }

    private static void ReadCreditBucket(JsonElement bucket, string limitProperty, int usedIndex,
        int remainingIndex, int limitIndex, QoderQuotaPoint[] points)
    {
        var unit = ReadOptionalString(bucket, "unit");
        if (!StringComparer.OrdinalIgnoreCase.Equals(unit?.Trim(), "credits"))
        {
            var hasAnyValue = HasNonNullProperty(bucket, "used") || HasNonNullProperty(bucket, "remaining") ||
                HasNonNullProperty(bucket, limitProperty);
            var point = QoderQuotaPoint.Unavailable(hasAnyValue ? "unit_unverified" : null);
            points[usedIndex] = point;
            points[remainingIndex] = point;
            points[limitIndex] = point;
            return;
        }

        points[usedIndex] = ReadNonNegativeNumber(bucket, "used");
        points[remainingIndex] = ReadNonNegativeNumber(bucket, "remaining");
        points[limitIndex] = ReadNonNegativeNumber(bucket, limitProperty);
    }

    private static QoderQuotaPoint ReadNonNegativeNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return QoderQuotaPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < 0)
            return QoderQuotaPoint.Error("invalid_quota");
        return QoderQuotaPoint.Fresh(MetricValue.From(number));
    }

    private static QoderQuotaPoint ReadPercent(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return QoderQuotaPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number is < 0 or > 100)
            return QoderQuotaPoint.Error("invalid_percentage");
        return QoderQuotaPoint.Fresh(MetricValue.From(number));
    }

    private static QoderQuotaPoint ReadBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return QoderQuotaPoint.Unavailable();
        return value.ValueKind switch
        {
            JsonValueKind.True => QoderQuotaPoint.Fresh(MetricValue.From(true)),
            JsonValueKind.False => QoderQuotaPoint.Fresh(MetricValue.From(false)),
            _ => QoderQuotaPoint.Error("invalid_status")
        };
    }

    private static QoderQuotaPoint ReadPlanExpiry(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return QoderQuotaPoint.Unavailable();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var milliseconds))
            return QoderQuotaPoint.Error("invalid_timestamp");
        if (milliseconds == NoExpiryEpochMilliseconds)
            return QoderQuotaPoint.Unavailable();

        try
        {
            return QoderQuotaPoint.Fresh(MetricValue.From(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return QoderQuotaPoint.Error("invalid_timestamp");
        }
    }

    private static bool TryGetObject(JsonElement parent, string property, out JsonElement value)
    {
        if (parent.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static string? ReadOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasNonNullProperty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null;

}
