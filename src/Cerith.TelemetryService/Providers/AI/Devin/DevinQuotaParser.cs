#nullable enable

using System;
using System.Globalization;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal readonly record struct DevinQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static DevinQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static DevinQuotaPoint Stale(MetricValue value, string? code) => new(MetricState.Stale, value, code);
    public static DevinQuotaPoint Unavailable(string code) => new(MetricState.Unavailable, null, code);
    public static DevinQuotaPoint Error(string code) => new(MetricState.Error, null, code);
    public static DevinQuotaPoint Stopped() => new(MetricState.Stopped, null, null);
}

internal static class DevinQuotaParser
{
    internal const int DailyRemaining = 0;
    internal const int DailyReset = 1;
    internal const int WeeklyRemaining = 2;
    internal const int WeeklyReset = 3;
    internal const int PointCount = 4;

    public static DevinQuotaPoint[] Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("planStatus", out var planStatus) ||
                planStatus.ValueKind != JsonValueKind.Object ||
                !planStatus.TryGetProperty("planInfo", out var planInfo) ||
                planInfo.ValueKind != JsonValueKind.Object)
                return AllError("response_schema_invalid");

            var points = new DevinQuotaPoint[PointCount];
            if (!planInfo.TryGetProperty("billingStrategy", out var billingStrategy))
                return AllUnavailable("billing_strategy_missing");
            if (billingStrategy.ValueKind != JsonValueKind.String)
                return AllError("billing_strategy_invalid");

            var strategy = billingStrategy.GetString();
            if (strategy != "BILLING_STRATEGY_QUOTA")
            {
                var code = strategy == "BILLING_STRATEGY_ACU"
                    ? "non_quota_billing"
                    : "billing_strategy_unsupported";
                return AllUnavailable(code);
            }

            ReadWindow(points, planStatus, planInfo, "hideDailyQuota", "dailyQuotaRemainingPercent",
                "dailyQuotaResetAtUnix", DailyRemaining, DailyReset);
            ReadWindow(points, planStatus, planInfo, "hideWeeklyQuota", "weeklyQuotaRemainingPercent",
                "weeklyQuotaResetAtUnix", WeeklyRemaining, WeeklyReset);
            return points;
        }
        catch (JsonException)
        {
            return AllError("response_invalid");
        }
    }

    private static void ReadWindow(DevinQuotaPoint[] points, JsonElement planStatus, JsonElement planInfo,
        string hideProperty, string remainingProperty, string resetProperty, int remainingIndex, int resetIndex)
    {
        if (!planInfo.TryGetProperty(hideProperty, out var hidden))
        {
            points[remainingIndex] = DevinQuotaPoint.Unavailable("hide_flag_missing");
            points[resetIndex] = DevinQuotaPoint.Unavailable("hide_flag_missing");
            return;
        }

        if (hidden.ValueKind == JsonValueKind.True)
        {
            points[remainingIndex] = DevinQuotaPoint.Unavailable("quota_hidden");
            points[resetIndex] = DevinQuotaPoint.Unavailable("quota_hidden");
            return;
        }

        if (hidden.ValueKind != JsonValueKind.False)
        {
            points[remainingIndex] = DevinQuotaPoint.Error("hide_flag_invalid");
            points[resetIndex] = DevinQuotaPoint.Error("hide_flag_invalid");
            return;
        }

        points[remainingIndex] = ReadRemaining(planStatus, remainingProperty);
        points[resetIndex] = ReadReset(planStatus, resetProperty);
    }

    private static DevinQuotaPoint ReadRemaining(JsonElement planStatus, string property)
    {
        if (!planStatus.TryGetProperty(property, out var value))
            return DevinQuotaPoint.Unavailable("remaining_missing");

        int remaining;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt32(out remaining)) return DevinQuotaPoint.Error("remaining_invalid");
        }
        else if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out remaining))
        {
        }
        else
        {
            return DevinQuotaPoint.Error("remaining_invalid");
        }

        return remaining is >= 0 and <= 100
            ? DevinQuotaPoint.Fresh(MetricValue.From((double)remaining))
            : DevinQuotaPoint.Error("remaining_out_of_range");
    }

    private static DevinQuotaPoint ReadReset(JsonElement planStatus, string property)
    {
        if (!planStatus.TryGetProperty(property, out var value))
            return DevinQuotaPoint.Unavailable("reset_missing");

        long unixSeconds;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out unixSeconds)) return DevinQuotaPoint.Error("reset_invalid");
        }
        else if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out unixSeconds))
        {
        }
        else
        {
            return DevinQuotaPoint.Error("reset_invalid");
        }

        try
        {
            return DevinQuotaPoint.Fresh(MetricValue.From(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DevinQuotaPoint.Error("reset_invalid");
        }
    }

    private static DevinQuotaPoint[] AllUnavailable(string code)
    {
        var points = new DevinQuotaPoint[PointCount];
        Array.Fill(points, DevinQuotaPoint.Unavailable(code));
        return points;
    }

    private static DevinQuotaPoint[] AllError(string code)
    {
        var points = new DevinQuotaPoint[PointCount];
        Array.Fill(points, DevinQuotaPoint.Error(code));
        return points;
    }
}
