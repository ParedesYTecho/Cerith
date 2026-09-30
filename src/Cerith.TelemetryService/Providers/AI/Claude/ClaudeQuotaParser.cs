#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Claude;

internal sealed record ClaudeQuotaPoint(string Id, string DisplayName, MetricValueKind ValueKind,
    MetricValue? Value, MetricState State, string? ErrorCode, DateTimeOffset? ResetAt);

internal sealed record ClaudeQuotaReading(IReadOnlyList<ClaudeQuotaPoint> Points)
{
    internal static ClaudeQuotaReading Failure(string code) => new(
    [
        Error("ai.claude.session.remaining", "Claude Session Remaining", MetricValueKind.Double, code),
        Error("ai.claude.session.reset", "Claude Session Reset At", MetricValueKind.DateTimeOffset, code),
        Error("ai.claude.weekly.remaining", "Claude Weekly Remaining", MetricValueKind.Double, code),
        Error("ai.claude.weekly.reset", "Claude Weekly Reset At", MetricValueKind.DateTimeOffset, code)
    ]);

    internal static ClaudeQuotaPoint Unavailable(string id, string name, MetricValueKind kind,
        string? code = null, DateTimeOffset? resetAt = null) =>
        new(id, name, kind, null, MetricState.Unavailable, code, resetAt);

    internal static ClaudeQuotaPoint Error(string id, string name, MetricValueKind kind, string code) =>
        new(id, name, kind, null, MetricState.Error, code, null);
}

internal static class ClaudeQuotaParser
{
    public static ClaudeQuotaReading Parse(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return ClaudeQuotaReading.Failure("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ClaudeQuotaReading.Failure("schema_invalid");

            var points = new List<ClaudeQuotaPoint>(8);
            AddWindow(root, "five_hour", "ai.claude.session", "Claude Session", "utilization", observedAt, points);
            AddWindow(root, "seven_day", "ai.claude.weekly", "Claude Weekly", "utilization", observedAt, points);

            AddLegacyModel(root, "seven_day_opus", "opus", "Opus", observedAt, points);
            AddLegacyModel(root, "seven_day_sonnet", "sonnet", "Sonnet", observedAt, points);

            return new ClaudeQuotaReading(points);
        }
    }

    private static void AddLegacyModel(JsonElement root, string sourceKey, string slug, string label,
        DateTimeOffset observedAt, List<ClaudeQuotaPoint> points)
    {
        if (!root.TryGetProperty(sourceKey, out var window) || window.ValueKind == JsonValueKind.Null) return;
        AddWindow(root, sourceKey, "ai.claude.weekly.model." + slug, "Claude Weekly " + label,
            "utilization", observedAt, points);
    }

    private static void AddWindow(JsonElement parent, string sourceKey, string metricBase, string displayName,
        string utilizationProperty, DateTimeOffset observedAt, List<ClaudeQuotaPoint> points)
    {
        if (!parent.TryGetProperty(sourceKey, out var window) || window.ValueKind == JsonValueKind.Null)
        {
            points.Add(ClaudeQuotaReading.Unavailable(metricBase + ".remaining", displayName + " Remaining", MetricValueKind.Double));
            points.Add(ClaudeQuotaReading.Unavailable(metricBase + ".reset", displayName + " Reset At", MetricValueKind.DateTimeOffset));
            return;
        }

        if (window.ValueKind != JsonValueKind.Object)
        {
            points.Add(ClaudeQuotaReading.Error(metricBase + ".remaining", displayName + " Remaining", MetricValueKind.Double, "schema_invalid"));
            points.Add(ClaudeQuotaReading.Error(metricBase + ".reset", displayName + " Reset At", MetricValueKind.DateTimeOffset, "schema_invalid"));
            return;
        }

        var reset = ReadReset(window, "resets_at");
        if (reset.State == MetricState.Fresh && reset.Value is DateTimeOffset resetAt && resetAt <= observedAt)
        {
            points.Add(ClaudeQuotaReading.Unavailable(metricBase + ".remaining", displayName + " Remaining", MetricValueKind.Double,
                "reset_elapsed", resetAt));
            points.Add(ClaudeQuotaReading.Unavailable(metricBase + ".reset", displayName + " Reset At", MetricValueKind.DateTimeOffset,
                "reset_elapsed", resetAt));
            return;
        }

        if (!window.TryGetProperty(utilizationProperty, out var utilization) || utilization.ValueKind == JsonValueKind.Null)
            points.Add(ClaudeQuotaReading.Unavailable(metricBase + ".remaining", displayName + " Remaining", MetricValueKind.Double));
        else if (utilization.ValueKind != JsonValueKind.Number || !utilization.TryGetDouble(out var used) ||
            !double.IsFinite(used) || used is < 0 or > 100)
            points.Add(ClaudeQuotaReading.Error(metricBase + ".remaining", displayName + " Remaining", MetricValueKind.Double, "invalid_quota"));
        else
            points.Add(new ClaudeQuotaPoint(metricBase + ".remaining", displayName + " Remaining", MetricValueKind.Double,
                MetricValue.From(100d - used), MetricState.Fresh, null, reset.Value));

        if (reset.State == MetricState.Fresh && reset.Value is DateTimeOffset validReset)
            points.Add(new ClaudeQuotaPoint(metricBase + ".reset", displayName + " Reset At", MetricValueKind.DateTimeOffset,
                MetricValue.From(validReset), MetricState.Fresh, null, validReset));
        else if (reset.State == MetricState.Error)
            points.Add(ClaudeQuotaReading.Error(metricBase + ".reset", displayName + " Reset At", MetricValueKind.DateTimeOffset,
                reset.ErrorCode ?? "invalid_reset"));
        else
            points.Add(ClaudeQuotaReading.Unavailable(metricBase + ".reset", displayName + " Reset At", MetricValueKind.DateTimeOffset));
    }

    private static ParsedReset ReadReset(JsonElement window, string propertyName)
    {
        if (!window.TryGetProperty(propertyName, out var reset) || reset.ValueKind == JsonValueKind.Null)
            return ParsedReset.Missing;
        if (reset.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
            return ParsedReset.Invalid;
        return new ParsedReset(MetricState.Fresh, parsed, null);
    }


    private readonly record struct ParsedReset(MetricState State, DateTimeOffset? Value, string? ErrorCode)
    {
        public static ParsedReset Missing => new(MetricState.Unavailable, null, null);
        public static ParsedReset Invalid => new(MetricState.Error, null, "invalid_reset");
    }
}
