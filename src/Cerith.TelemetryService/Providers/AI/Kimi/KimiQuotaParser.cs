#nullable enable

using System;
using System.Globalization;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Kimi;

internal readonly record struct KimiQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static KimiQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static KimiQuotaPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static KimiQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record KimiQuotaReading(KimiQuotaPoint[] Points)
{
    public static KimiQuotaReading Failure(string code) => new(
        [KimiQuotaPoint.Error(code), KimiQuotaPoint.Error(code), KimiQuotaPoint.Error(code),
         KimiQuotaPoint.Error(code), KimiQuotaPoint.Error(code), KimiQuotaPoint.Error(code)]);
}

internal static class KimiQuotaParser
{
    internal const int FiveHourRemaining = 0;
    internal const int FiveHourReset = 1;
    internal const int SevenDayRemaining = 2;
    internal const int SevenDayReset = 3;
    internal const int MonthlyRemaining = 4;
    internal const int MonthlyReset = 5;

    public static KimiQuotaReading Parse(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 }); }
        catch (JsonException) { return KimiQuotaReading.Failure("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("usages", out var usages) || usages.ValueKind != JsonValueKind.Object)
                return KimiQuotaReading.Failure("schema_invalid");

            var fiveHour = ParseWindow(usages, "limit_5h", observedAt);
            var sevenDay = ParseWindow(usages, "limit_7d", observedAt);
            var monthly = ParseWindow(usages, "limit_month_total", observedAt);
            return new KimiQuotaReading(
            [fiveHour.Remaining, fiveHour.Reset, sevenDay.Remaining, sevenDay.Reset,
             monthly.Remaining, monthly.Reset]);
        }
    }

    private static QuotaWindow ParseWindow(JsonElement usages, string name, DateTimeOffset observedAt)
    {
        if (!usages.TryGetProperty(name, out var window) || window.ValueKind is JsonValueKind.Null)
            return QuotaWindow.Missing;
        if (window.ValueKind != JsonValueKind.Object) return QuotaWindow.Invalid;

        var remaining = ReadRemaining(window);
        var reset = ReadReset(window);
        if (reset.Value is MetricValue resetValue && resetValue.AsDateTimeOffset() <= observedAt)
            return QuotaWindow.Elapsed;
        return new QuotaWindow(remaining, reset);
    }

    private static KimiQuotaPoint ReadRemaining(JsonElement window)
    {
        if (!window.TryGetProperty("used_ratio", out var ratio) || ratio.ValueKind is JsonValueKind.Null)
            return KimiQuotaPoint.Unavailable();

        double used;
        if (ratio.ValueKind == JsonValueKind.Number && ratio.TryGetDouble(out used)) { }
        else if (ratio.ValueKind == JsonValueKind.String &&
                 double.TryParse(ratio.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out used)) { }
        else return KimiQuotaPoint.Error("invalid_quota");

        if (!double.IsFinite(used) || used is < 0 or > 1)
            return KimiQuotaPoint.Error("invalid_quota");
        return KimiQuotaPoint.Fresh(MetricValue.From((1d - used) * 100d));
    }

    private static KimiQuotaPoint ReadReset(JsonElement window)
    {
        if (!window.TryGetProperty("reset_time", out var reset) || reset.ValueKind is JsonValueKind.Null)
            return KimiQuotaPoint.Unavailable();
        if (reset.ValueKind != JsonValueKind.String) return KimiQuotaPoint.Error("invalid_reset");

        var text = reset.GetString();
        if (string.IsNullOrWhiteSpace(text) || !HasExplicitOffset(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return KimiQuotaPoint.Error("invalid_reset");
        return KimiQuotaPoint.Fresh(MetricValue.From(value));
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z')) return true;
        var time = value.IndexOf('T');
        return time >= 0 && (value.LastIndexOf('+') > time || value.LastIndexOf('-') > time);
    }

    private readonly record struct QuotaWindow(KimiQuotaPoint Remaining, KimiQuotaPoint Reset)
    {
        public static QuotaWindow Missing => new(KimiQuotaPoint.Unavailable(), KimiQuotaPoint.Unavailable());
        public static QuotaWindow Invalid => new(KimiQuotaPoint.Error("schema_invalid"),
            KimiQuotaPoint.Error("schema_invalid"));
        public static QuotaWindow Elapsed => new(KimiQuotaPoint.Unavailable("reset_elapsed"),
            KimiQuotaPoint.Unavailable("reset_elapsed"));
    }
}
