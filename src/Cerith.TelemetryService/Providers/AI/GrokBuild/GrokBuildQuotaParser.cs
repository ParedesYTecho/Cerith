#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.GrokBuild;

internal readonly record struct GrokBuildQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static GrokBuildQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);

    public static GrokBuildQuotaPoint Unavailable(string? errorCode = null) =>
        new(MetricState.Unavailable, null, errorCode);

    public static GrokBuildQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record GrokBuildQuotaReading(GrokBuildQuotaPoint[] Points)
{
    public static GrokBuildQuotaReading Missing() =>
        new(Enumerable.Repeat(GrokBuildQuotaPoint.Unavailable(), GrokBuildQuotaProvider.MetricCount).ToArray());

    public static GrokBuildQuotaReading Failure(string code) =>
        new(Enumerable.Repeat(GrokBuildQuotaPoint.Error(code), GrokBuildQuotaProvider.MetricCount).ToArray());
}

internal static class GrokBuildQuotaParser
{
    internal const int IncludedUsedPercent = 0;
    internal const int IncludedRemainingPercent = 1;
    internal const int PeriodType = 2;
    internal const int PeriodStart = 3;
    internal const int PeriodEnd = 4;
    internal const int PeriodDuration = 5;
    internal const int OnDemandUsed = 6;
    internal const int OnDemandCap = 7;
    internal const int PrepaidBalance = 8;
    internal const int UnifiedBilling = 9;
    internal const int ProductUsagePercent = 10;

    public static GrokBuildQuotaReading Parse(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return GrokBuildQuotaReading.Failure("schema_invalid");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return GrokBuildQuotaReading.Failure("schema_invalid");

            if (!root.TryGetProperty("config", out var config) || config.ValueKind is JsonValueKind.Null)
                return GrokBuildQuotaReading.Missing();
            if (config.ValueKind != JsonValueKind.Object)
                return GrokBuildQuotaReading.Failure("schema_invalid");

            var points = Enumerable.Repeat(GrokBuildQuotaPoint.Unavailable(), GrokBuildQuotaProvider.MetricCount)
                .ToArray();
            ParsePeriod(config, observedAt, points);
            ParseIncludedUsage(config, observedAt, points);
            points[OnDemandUsed] = ReadCent(config, "onDemandUsed");
            points[OnDemandCap] = ReadCent(config, "onDemandCap");
            points[PrepaidBalance] = ReadCent(config, "prepaidBalance");
            points[UnifiedBilling] = ReadBoolean(config, "isUnifiedBillingUser");
            points[ProductUsagePercent] = ReadProductUsage(config);
            return new GrokBuildQuotaReading(points);
        }
    }

    private static void ParseIncludedUsage(JsonElement config, DateTimeOffset observedAt, GrokBuildQuotaPoint[] points)
    {
        if (!config.TryGetProperty("creditUsagePercent", out var used) || used.ValueKind == JsonValueKind.Null)
            return;
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percentage) ||
            !double.IsFinite(percentage) || percentage is < 0 or > 100)
        {
            points[IncludedUsedPercent] = GrokBuildQuotaPoint.Error("invalid_quota");
            points[IncludedRemainingPercent] = GrokBuildQuotaPoint.Error("invalid_quota");
            return;
        }

        if (points[PeriodEnd].Value is MetricValue endValue &&
            endValue.AsDateTimeOffset() <= observedAt)
        {
            points[IncludedUsedPercent] = GrokBuildQuotaPoint.Unavailable("reset_elapsed");
            points[IncludedRemainingPercent] = GrokBuildQuotaPoint.Unavailable("reset_elapsed");
            return;
        }

        points[IncludedUsedPercent] = GrokBuildQuotaPoint.Fresh(MetricValue.From(percentage));
        points[IncludedRemainingPercent] = GrokBuildQuotaPoint.Fresh(MetricValue.From(100d - percentage));
    }

    private static void ParsePeriod(JsonElement config, DateTimeOffset observedAt, GrokBuildQuotaPoint[] points)
    {
        if (!config.TryGetProperty("currentPeriod", out var period) || period.ValueKind == JsonValueKind.Null)
            return;
        if (period.ValueKind != JsonValueKind.Object)
        {
            for (var i = PeriodType; i <= PeriodDuration; i++)
                points[i] = GrokBuildQuotaPoint.Error("schema_invalid");
            return;
        }

        if (period.TryGetProperty("type", out var type) && type.ValueKind != JsonValueKind.Null)
        {
            if (type.ValueKind == JsonValueKind.String && type.GetString() is { Length: > 0 and <= 64 } value)
                points[PeriodType] = GrokBuildQuotaPoint.Fresh(MetricValue.From(value));
            else
                points[PeriodType] = GrokBuildQuotaPoint.Error("invalid_period_type");
        }

        var start = ReadPeriodDate(period, "start", PeriodStart, points);
        var end = ReadPeriodDate(period, "end", PeriodEnd, points);
        if (start is DateTimeOffset startAt && end is DateTimeOffset endAt)
        {
            if (endAt <= startAt)
                points[PeriodDuration] = GrokBuildQuotaPoint.Error("invalid_period");
            else
                points[PeriodDuration] = GrokBuildQuotaPoint.Fresh(MetricValue.From(endAt - startAt));
        }

        if (end is DateTimeOffset resetAt && resetAt <= observedAt)
        {
            points[IncludedUsedPercent] = GrokBuildQuotaPoint.Unavailable("reset_elapsed");
            points[IncludedRemainingPercent] = GrokBuildQuotaPoint.Unavailable("reset_elapsed");
        }
    }

    private static DateTimeOffset? ReadPeriodDate(JsonElement period, string name, int index,
        GrokBuildQuotaPoint[] points)
    {
        if (!period.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is null || !HasExplicitOffset(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            points[index] = GrokBuildQuotaPoint.Error("invalid_period_date");
            return null;
        }

        points[index] = GrokBuildQuotaPoint.Fresh(MetricValue.From(parsed));
        return parsed;
    }

    private static bool HasExplicitOffset(string value)
    {
        var separator = value.IndexOf('T');
        if (separator < 0)
            return false;
        if (value.EndsWith('Z') || value.EndsWith('z'))
            return true;
        return value.LastIndexOf('+') > separator || value.LastIndexOf('-') > separator;
    }

    private static GrokBuildQuotaPoint ReadCent(JsonElement config, string name)
    {
        if (!config.TryGetProperty(name, out var cent) || cent.ValueKind == JsonValueKind.Null)
            return GrokBuildQuotaPoint.Unavailable();
        if (cent.ValueKind != JsonValueKind.Object)
            return GrokBuildQuotaPoint.Error("invalid_balance");

        // The official response uses proto3 Cent values; an omitted scalar inside a present Cent object is zero.
        if (!cent.TryGetProperty("val", out var value))
            return GrokBuildQuotaPoint.Fresh(MetricValue.From(0L));
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var cents) || cents < 0)
            return GrokBuildQuotaPoint.Error("invalid_balance");
        return GrokBuildQuotaPoint.Fresh(MetricValue.From(cents));
    }

    private static GrokBuildQuotaPoint ReadBoolean(JsonElement config, string name)
    {
        if (!config.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return GrokBuildQuotaPoint.Unavailable();
        return value.ValueKind switch
        {
            JsonValueKind.True => GrokBuildQuotaPoint.Fresh(MetricValue.From(true)),
            JsonValueKind.False => GrokBuildQuotaPoint.Fresh(MetricValue.From(false)),
            _ => GrokBuildQuotaPoint.Error("invalid_billing_mode")
        };
    }

    private static GrokBuildQuotaPoint ReadProductUsage(JsonElement config)
    {
        if (!config.TryGetProperty("productUsage", out var products) || products.ValueKind == JsonValueKind.Null)
            return GrokBuildQuotaPoint.Unavailable();
        if (products.ValueKind != JsonValueKind.Array)
            return GrokBuildQuotaPoint.Error("schema_invalid");

        JsonElement? grokBuild = null;
        foreach (var product in products.EnumerateArray())
        {
            if (product.ValueKind != JsonValueKind.Object)
                return GrokBuildQuotaPoint.Error("schema_invalid");
            if (!product.TryGetProperty("product", out var name) || name.ValueKind != JsonValueKind.String)
                continue;
            if (!StringComparer.Ordinal.Equals(name.GetString(), "PRODUCT_GROK_BUILD"))
                continue;
            if (grokBuild.HasValue)
                return GrokBuildQuotaPoint.Error("duplicate_product_usage");
            grokBuild = product;
        }

        if (!grokBuild.HasValue)
            return GrokBuildQuotaPoint.Unavailable();
        var entry = grokBuild.Value;
        if (!entry.TryGetProperty("usagePercent", out var usage) || usage.ValueKind == JsonValueKind.Null)
            return GrokBuildQuotaPoint.Unavailable();
        if (usage.ValueKind != JsonValueKind.Number || !usage.TryGetDouble(out var percentage) ||
            !double.IsFinite(percentage) || percentage is < 0 or > 100)
            return GrokBuildQuotaPoint.Error("invalid_product_usage");
        return GrokBuildQuotaPoint.Fresh(MetricValue.From(percentage));
    }
}
