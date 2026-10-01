#nullable enable

using System;
using System.Globalization;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal readonly record struct OpenCodeGoQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static OpenCodeGoQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static OpenCodeGoQuotaPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static OpenCodeGoQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record OpenCodeGoQuotaReading(OpenCodeGoQuotaPoint[] Points)
{
    public static OpenCodeGoQuotaReading Failure(string code) =>
        new([OpenCodeGoQuotaPoint.Error(code), OpenCodeGoQuotaPoint.Error(code),
            OpenCodeGoQuotaPoint.Error(code), OpenCodeGoQuotaPoint.Error(code),
            OpenCodeGoQuotaPoint.Error(code), OpenCodeGoQuotaPoint.Error(code)]);

    public static OpenCodeGoQuotaReading Missing() =>
        new([OpenCodeGoQuotaPoint.Unavailable(), OpenCodeGoQuotaPoint.Unavailable(),
            OpenCodeGoQuotaPoint.Unavailable(), OpenCodeGoQuotaPoint.Unavailable(),
            OpenCodeGoQuotaPoint.Unavailable(), OpenCodeGoQuotaPoint.Unavailable()]);
}

internal static class OpenCodeGoQuotaParser
{
    internal const int RollingUsed = 0;
    internal const int WeeklyUsed = 1;
    internal const int MonthlyUsed = 2;
    internal const int RollingReset = 3;
    internal const int WeeklyReset = 4;
    internal const int MonthlyReset = 5;

    private static readonly string[] WindowNames = ["rolling", "weekly", "monthly"];

    public static OpenCodeGoQuotaReading Parse(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { return OpenCodeGoQuotaReading.Failure("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return OpenCodeGoQuotaReading.Failure("schema_invalid");
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return OpenCodeGoQuotaReading.Missing();
            if (usage.ValueKind != JsonValueKind.Object)
                return OpenCodeGoQuotaReading.Failure("schema_invalid");

            var points = new OpenCodeGoQuotaPoint[6];
            for (var i = 0; i < WindowNames.Length; i++)
            {
                var window = ParseWindow(usage, WindowNames[i], observedAt);
                points[i] = window.Used;
                points[i + WindowNames.Length] = window.Reset;
            }
            return new OpenCodeGoQuotaReading(points);
        }
    }

    private static Window ParseWindow(JsonElement usage, string name, DateTimeOffset observedAt)
    {
        if (!usage.TryGetProperty(name, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Window.Missing;
        if (element.ValueKind != JsonValueKind.Object)
            return Window.InvalidSchema;

        var used = ReadUsed(element);
        var reset = ReadReset(element);
        if (reset.Value is MetricValue value && value.AsDateTimeOffset() <= observedAt)
            return Window.Elapsed;
        return new Window(used, reset);
    }

    private static OpenCodeGoQuotaPoint ReadUsed(JsonElement window)
    {
        if (!window.TryGetProperty("percent", out var percentage) || percentage.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return OpenCodeGoQuotaPoint.Unavailable();
        if (percentage.ValueKind != JsonValueKind.Number || !percentage.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value is < 0 or > 100)
            return OpenCodeGoQuotaPoint.Error("invalid_quota");
        return OpenCodeGoQuotaPoint.Fresh(MetricValue.From(value));
    }

    private static OpenCodeGoQuotaPoint ReadReset(JsonElement window)
    {
        if (!window.TryGetProperty("resetsAt", out var reset) || reset.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return OpenCodeGoQuotaPoint.Unavailable();
        if (reset.ValueKind != JsonValueKind.String)
            return OpenCodeGoQuotaPoint.Error("invalid_reset");

        var text = reset.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 || !HasExplicitOffset(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return OpenCodeGoQuotaPoint.Error("invalid_reset");
        return OpenCodeGoQuotaPoint.Fresh(MetricValue.From(value.ToUniversalTime()));
    }

    private static bool HasExplicitOffset(string text)
    {
        if (text.Length < 20 || text.IndexOf('T') < 0) return false;
        if (text.EndsWith('Z') || text.EndsWith('z')) return true;
        var timeSeparator = text.IndexOf('T');
        var plus = text.LastIndexOf('+');
        var minus = text.LastIndexOf('-');
        return (plus > timeSeparator || minus > timeSeparator) && text.Length - Math.Max(plus, minus) == 6;
    }

    private readonly record struct Window(OpenCodeGoQuotaPoint Used, OpenCodeGoQuotaPoint Reset)
    {
        public static Window Missing => new(OpenCodeGoQuotaPoint.Unavailable(), OpenCodeGoQuotaPoint.Unavailable());
        public static Window InvalidSchema => new(OpenCodeGoQuotaPoint.Error("schema_invalid"),
            OpenCodeGoQuotaPoint.Error("schema_invalid"));
        public static Window Elapsed => new(OpenCodeGoQuotaPoint.Unavailable("reset_elapsed"),
            OpenCodeGoQuotaPoint.Unavailable("reset_elapsed"));
    }
}
