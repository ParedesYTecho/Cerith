#nullable enable

using System;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal readonly record struct QoderCnQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static QoderCnQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static QoderCnQuotaPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static QoderCnQuotaPoint Error(string code) => new(MetricState.Error, null, code);
    public static QoderCnQuotaPoint Stopped() => new(MetricState.Stopped, null, null);
}

internal sealed record QoderCnQuotaReading(QoderCnQuotaPoint[] Points)
{
    public const int MetricCount = 10;

    public static QoderCnQuotaReading Failure(string code) => new(
    [
        QoderCnQuotaPoint.Error(code), QoderCnQuotaPoint.Error(code), QoderCnQuotaPoint.Error(code),
        QoderCnQuotaPoint.Error(code), QoderCnQuotaPoint.Error(code), QoderCnQuotaPoint.Error(code),
        QoderCnQuotaPoint.Error(code), QoderCnQuotaPoint.Error(code), QoderCnQuotaPoint.Error(code),
        QoderCnQuotaPoint.Error(code)
    ]);

    public static QoderCnQuotaReading Missing(string? code = null) => new(
    [
        QoderCnQuotaPoint.Unavailable(code), QoderCnQuotaPoint.Unavailable(code), QoderCnQuotaPoint.Unavailable(code),
        QoderCnQuotaPoint.Unavailable(code), QoderCnQuotaPoint.Unavailable(code), QoderCnQuotaPoint.Unavailable(code),
        QoderCnQuotaPoint.Unavailable(code), QoderCnQuotaPoint.Unavailable(code), QoderCnQuotaPoint.Unavailable(code),
        QoderCnQuotaPoint.Unavailable(code)
    ]);
}

internal static class QoderCnQuotaParser
{
    internal const int IncludedTotal = 0;
    internal const int IncludedUsed = 1;
    internal const int IncludedRemaining = 2;
    internal const int AddOnTotal = 3;
    internal const int AddOnUsed = 4;
    internal const int AddOnRemaining = 5;
    internal const int PlanExpiresAt = 6;
    internal const int OrganizationCap = 7;
    internal const int OrganizationUsed = 8;
    internal const int OrganizationRemaining = 9;

    public static QoderCnQuotaReading Parse(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return QoderCnQuotaReading.Failure("schema_invalid");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return QoderCnQuotaReading.Failure("schema_invalid");
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return QoderCnQuotaReading.Missing();
            if (usage.ValueKind != JsonValueKind.Object)
                return QoderCnQuotaReading.Failure("schema_invalid");

            var points = new QoderCnQuotaPoint[QoderCnQuotaReading.MetricCount];
            Array.Fill(points, QoderCnQuotaPoint.Unavailable());

            ParseQuotaBucket(usage, "userQuota", IncludedTotal, points);
            ParseQuotaBucket(usage, "addOnQuota", AddOnTotal, points);
            points[PlanExpiresAt] = ReadPlanExpiry(usage);
            ParseOrganizationPackage(usage, points);
            return new QoderCnQuotaReading(points);
        }
    }

    private static void ParseQuotaBucket(JsonElement usage, string name, int offset, QoderCnQuotaPoint[] points)
    {
        if (!usage.TryGetProperty(name, out var bucket) || bucket.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;
        if (bucket.ValueKind != JsonValueKind.Object)
        {
            points[offset] = QoderCnQuotaPoint.Error("schema_invalid");
            points[offset + 1] = QoderCnQuotaPoint.Error("schema_invalid");
            points[offset + 2] = QoderCnQuotaPoint.Error("schema_invalid");
            return;
        }

        if (bucket.TryGetProperty("unit", out var unit) && unit.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (unit.ValueKind != JsonValueKind.String ||
                !StringComparer.OrdinalIgnoreCase.Equals(unit.GetString(), "Credits"))
            {
                points[offset] = QoderCnQuotaPoint.Error("unit_unsupported");
                points[offset + 1] = QoderCnQuotaPoint.Error("unit_unsupported");
                points[offset + 2] = QoderCnQuotaPoint.Error("unit_unsupported");
                return;
            }
        }

        points[offset] = ReadAmount(bucket, "total");
        points[offset + 1] = ReadAmount(bucket, "used");
        points[offset + 2] = ReadAmount(bucket, "remaining");
    }

    private static QoderCnQuotaPoint ReadAmount(JsonElement bucket, string name)
    {
        if (!bucket.TryGetProperty(name, out var amount) || amount.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return QoderCnQuotaPoint.Unavailable();
        if (amount.ValueKind != JsonValueKind.Number || !amount.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value < 0)
            return QoderCnQuotaPoint.Error("invalid_quota");
        return QoderCnQuotaPoint.Fresh(MetricValue.From(value));
    }

    private static QoderCnQuotaPoint ReadPlanExpiry(JsonElement usage)
    {
        if (!usage.TryGetProperty("expiresAt", out var expiry) || expiry.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return QoderCnQuotaPoint.Unavailable();
        if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var milliseconds))
            return QoderCnQuotaPoint.Error("invalid_timestamp");

        try
        {
            return QoderCnQuotaPoint.Fresh(MetricValue.From(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return QoderCnQuotaPoint.Error("invalid_timestamp");
        }
    }

    private static void ParseOrganizationPackage(JsonElement usage, QoderCnQuotaPoint[] points)
    {
        if (!usage.TryGetProperty("orgResourcePackage", out var package) ||
            package.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        var state = package.ValueKind == JsonValueKind.Object
            ? QoderCnQuotaPoint.Unavailable("scope_unverified")
            : QoderCnQuotaPoint.Error("schema_invalid");
        points[OrganizationCap] = state;
        points[OrganizationUsed] = state;
        points[OrganizationRemaining] = state;
    }
}
