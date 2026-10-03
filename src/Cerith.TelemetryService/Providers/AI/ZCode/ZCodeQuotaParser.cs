#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal readonly record struct ZCodeQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static ZCodeQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static ZCodeQuotaPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static ZCodeQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record ZCodeQuotaReading(ZCodeQuotaPoint[] Points);

internal static class ZCodeQuotaParser
{
    internal const int PlanId = 0;
    internal const int FiveHourRemaining = 1;
    internal const int FiveHourReset = 2;
    internal const int FiveHourMetadata = 3;
    internal const int WeeklyRemaining = 4;
    internal const int WeeklyReset = 5;
    internal const int WeeklyMetadata = 6;
    internal const int ToolsRemaining = 7;
    internal const int ToolsReset = 8;
    internal const int ToolsMetadata = 9;
    internal const int PointCount = 10;
    internal const int MaxModelDetails = 32;

    public static ZCodeQuotaReading Parse(ReadOnlyMemory<byte> body)
    {
        var points = Enumerable.Range(0, PointCount).Select(_ => ZCodeQuotaPoint.Unavailable()).ToArray();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return AllError("schema_invalid");
            if (root.TryGetProperty("code", out var code) &&
                (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var codeValue) || codeValue is not (0 or 200)))
                return AllError("upstream_rejected");
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return AllError("upstream_rejected");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return AllError("schema_invalid");

            if (data.TryGetProperty("level", out var level))
            {
                if (level.ValueKind == JsonValueKind.String && IsBoundedNonempty(level.GetString(), 128, out var planId))
                    points[PlanId] = ZCodeQuotaPoint.Fresh(MetricValue.From(planId));
                else if (level.ValueKind != JsonValueKind.Null)
                    points[PlanId] = ZCodeQuotaPoint.Error("invalid_plan_id");
            }

            if (!data.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                return ReplaceQuotaPoints(points, "schema_invalid");

            FillWindow(points, limits, "5h", "TOKENS_LIMIT", 3, 5,
                FiveHourRemaining, FiveHourReset, FiveHourMetadata);
            FillWindow(points, limits, "weekly", "TOKENS_LIMIT", 6, null,
                WeeklyRemaining, WeeklyReset, WeeklyMetadata);
            FillWindow(points, limits, "tools", "TIME_LIMIT", 5, 1,
                ToolsRemaining, ToolsReset, ToolsMetadata);
            return new ZCodeQuotaReading(points);
        }
        catch (JsonException) { return AllError("response_invalid"); }
        catch (Exception) { return AllError("response_invalid"); }
    }

    private static void FillWindow(ZCodeQuotaPoint[] points, JsonElement limits, string label,
        string expectedType, int expectedUnit, int? expectedNumber, int remainingIndex, int resetIndex, int metadataIndex)
    {
        JsonElement selected = default;
        var matches = 0;
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object || !TryString(limit, "type", 64, out var type) ||
                !StringComparer.Ordinal.Equals(type, expectedType) || !TryInt(limit, "unit", out var unit) || unit != expectedUnit)
                continue;
            if (expectedNumber.HasValue && (!TryInt(limit, "number", out var number) || number != expectedNumber.Value))
                continue;
            selected = limit;
            matches++;
        }

        if (matches == 0) return;
        if (matches != 1)
        {
            points[remainingIndex] = points[resetIndex] = points[metadataIndex] = ZCodeQuotaPoint.Error("bucket_ambiguous");
            return;
        }

        points[remainingIndex] = ReadRemaining(selected);
        points[resetIndex] = ReadReset(selected);
        points[metadataIndex] = ReadMetadata(selected, label, expectedType, expectedUnit);
    }

    private static ZCodeQuotaPoint ReadRemaining(JsonElement limit)
    {
        if (!limit.TryGetProperty("percentage", out var usedPercent) || usedPercent.ValueKind == JsonValueKind.Null)
            return ZCodeQuotaPoint.Unavailable("percentage_missing");
        if (usedPercent.ValueKind != JsonValueKind.Number || !usedPercent.TryGetDouble(out var used) ||
            !double.IsFinite(used) || used is < 0 or > 100)
            return ZCodeQuotaPoint.Error("invalid_percentage");
        return ZCodeQuotaPoint.Fresh(MetricValue.From(100d - used));
    }

    private static ZCodeQuotaPoint ReadReset(JsonElement limit)
    {
        if (!limit.TryGetProperty("nextResetTime", out var reset) || reset.ValueKind == JsonValueKind.Null)
            return ZCodeQuotaPoint.Unavailable("reset_missing");
        if (reset.ValueKind == JsonValueKind.Number)
            return ZCodeQuotaPoint.Unavailable("reset_unit_unverified");
        if (reset.ValueKind != JsonValueKind.String || !TryParseExplicitOffset(reset.GetString(), out var dateTime))
            return ZCodeQuotaPoint.Error("invalid_reset");
        return ZCodeQuotaPoint.Fresh(MetricValue.From(dateTime));
    }

    private static ZCodeQuotaPoint ReadMetadata(JsonElement limit, string label, string type, int unit)
    {
        var details = new List<ModelDetail>();
        if (limit.TryGetProperty("usageDetails", out var usageDetails) && usageDetails.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in usageDetails.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object) continue;
                var displayName = ReadOptionalString(detail, "displayName", 128);
                var modelCode = ReadOptionalString(detail, "modelCode", 128);
                if (displayName is null && modelCode is null) continue;
                if (details.Count == MaxModelDetails) return ZCodeQuotaPoint.Error("model_list_too_large");
                details.Add(new ModelDetail(displayName, modelCode));
            }
        }
        var distinct = details.Distinct().ToArray();
        long? sourceNumber = null;
        if (limit.TryGetProperty("number", out var sourceNumberElement))
        {
            if (sourceNumberElement.ValueKind != JsonValueKind.Number ||
                !sourceNumberElement.TryGetInt64(out var parsedSourceNumber) || parsedSourceNumber < 0)
                return ZCodeQuotaPoint.Error("invalid_bucket_number");
            sourceNumber = parsedSourceNumber;
        }
        var value = JsonSerializer.Serialize(new BucketMetadata(label, type, unit, sourceNumber, distinct));
        return value.Length <= 8192
            ? ZCodeQuotaPoint.Fresh(MetricValue.From(value))
            : ZCodeQuotaPoint.Error("metadata_too_large");
    }

    private static string? ReadOptionalString(JsonElement parent, string name, int maxLength)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String && IsBoundedNonempty(value.GetString(), maxLength, out var result)
            ? result
            : null;
    }

    private static bool TryParseExplicitOffset(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        var hasZone = trimmed.EndsWith('Z') || trimmed.EndsWith('z') ||
            (trimmed.Length >= 6 && (trimmed[^6] == '+' || trimmed[^6] == '-') && trimmed[^3] == ':');
        return hasZone && DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out result);
    }

    private static bool TryString(JsonElement parent, string name, int maxLength, out string value)
    {
        value = string.Empty;
        return parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            IsBoundedNonempty(property.GetString(), maxLength, out value);
    }

    private static bool TryInt(JsonElement parent, string name, out int value)
    {
        value = default;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number) return false;
        return property.TryGetInt32(out value);
    }

    private static bool IsBoundedNonempty(string? value, int maximumLength, out string result)
    {
        result = value?.Trim() ?? string.Empty;
        return result.Length > 0 && result.Length <= maximumLength;
    }

    private static ZCodeQuotaReading ReplaceQuotaPoints(ZCodeQuotaPoint[] points, string errorCode)
    {
        for (var i = FiveHourRemaining; i < PointCount; i++)
            points[i] = ZCodeQuotaPoint.Error(errorCode);
        return new ZCodeQuotaReading(points);
    }

    private static ZCodeQuotaReading AllError(string errorCode) =>
        new(Enumerable.Range(0, PointCount).Select(_ => ZCodeQuotaPoint.Error(errorCode)).ToArray());

    private sealed record BucketMetadata(string Label, string Type, int Unit, long? SourceNumber, ModelDetail[] Models);
    private sealed record ModelDetail(string? DisplayName, string? ModelCode);
}
