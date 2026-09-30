#nullable enable

using System;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal readonly record struct CodexQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static CodexQuotaPoint Fresh(MetricValue value, string? errorCode = null) =>
        new(MetricState.Fresh, value, errorCode);
    public static CodexQuotaPoint Unavailable(string? errorCode = null) =>
        new(MetricState.Unavailable, null, errorCode);
    public static CodexQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record CodexQuotaReading(CodexQuotaPoint[] Points)
{
    public static CodexQuotaReading Failure(string code) => new(EnumerablePoints(CodexQuotaPoint.Error(code)));
    public static CodexQuotaReading Missing() => new(EnumerablePoints(CodexQuotaPoint.Unavailable()));

    private static CodexQuotaPoint[] EnumerablePoints(CodexQuotaPoint point) =>
        [point, point, point, point, point, point];
}

internal static class CodexQuotaParser
{
    internal const int PrimaryRemaining = 0;
    internal const int SecondaryRemaining = 1;
    internal const int PrimaryReset = 2;
    internal const int SecondaryReset = 3;
    internal const int PrimaryDuration = 4;
    internal const int SecondaryDuration = 5;

    public static CodexQuotaReading Parse(ReadOnlyMemory<byte> payload, string selectedAccountId, string selectedUserId,
        DateTimeOffset observedAt, TimeSpan requestDuration)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return CodexQuotaReading.Failure("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return CodexQuotaReading.Failure("schema_invalid");
            if (root.TryGetProperty("account_id", out var account))
            {
                if (account.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(account.GetString(), selectedAccountId))
                    return CodexQuotaReading.Failure("identity_mismatch");
            }
            if (root.TryGetProperty("user_id", out var user))
            {
                if (user.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(user.GetString(), selectedUserId))
                    return CodexQuotaReading.Failure("identity_mismatch");
            }

            if (!root.TryGetProperty("rate_limit", out var rateLimit) || rateLimit.ValueKind == JsonValueKind.Null)
                return CodexQuotaReading.Missing();
            if (rateLimit.ValueKind != JsonValueKind.Object) return CodexQuotaReading.Failure("schema_invalid");

            var primary = ParseWindow(rateLimit, "primary_window", observedAt, requestDuration);
            var secondary = ParseWindow(rateLimit, "secondary_window", observedAt, requestDuration);
            return new CodexQuotaReading(
            [
                primary.Remaining,
                secondary.Remaining,
                primary.Reset,
                secondary.Reset,
                primary.Duration,
                secondary.Duration
            ]);
        }
    }

    private static Window ParseWindow(JsonElement rateLimit, string name, DateTimeOffset observedAt,
        TimeSpan requestDuration)
    {
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null)
            return Window.Missing;
        if (window.ValueKind != JsonValueKind.Object) return Window.InvalidSchema;

        var remaining = ReadRemaining(window);
        var reset = ReadReset(window);
        if (reset.Value is MetricValue resetValue && resetValue.AsDateTimeOffset() <= observedAt)
            return Window.Elapsed;
        var duration = ReadDuration(window);

        if (reset.State == MetricState.Fresh && window.TryGetProperty("reset_after_seconds", out var countdown) &&
            countdown.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (countdown.ValueKind != JsonValueKind.Number || !countdown.TryGetDouble(out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0)
                reset = CodexQuotaPoint.Fresh(reset.Value!.Value, "invalid_reset_after");
            else
            {
                var resetAt = reset.Value!.Value.AsDateTimeOffset();
                var remainingSeconds = (resetAt - observedAt).TotalSeconds;
                var timingTolerance = requestDuration.TotalSeconds + 1d;
                if (Math.Abs(remainingSeconds - seconds) > timingTolerance)
                    reset = CodexQuotaPoint.Fresh(reset.Value.Value, "source_disagreement");
            }
        }

        return new Window(remaining, reset, duration);
    }

    private static CodexQuotaPoint ReadRemaining(JsonElement window)
    {
        if (!window.TryGetProperty("used_percent", out var used) || used.ValueKind == JsonValueKind.Null)
            return CodexQuotaPoint.Unavailable();
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percentage) ||
            !double.IsFinite(percentage) || percentage is < 0 or > 100)
            return CodexQuotaPoint.Error("invalid_quota");
        return CodexQuotaPoint.Fresh(MetricValue.From(100d - percentage));
    }

    private static CodexQuotaPoint ReadReset(JsonElement window)
    {
        if (!window.TryGetProperty("reset_at", out var reset) || reset.ValueKind == JsonValueKind.Null)
            return CodexQuotaPoint.Unavailable();
        if (reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var unixSeconds))
            return CodexQuotaPoint.Error("invalid_reset");
        try { return CodexQuotaPoint.Fresh(MetricValue.From(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))); }
        catch (ArgumentOutOfRangeException) { return CodexQuotaPoint.Error("invalid_reset"); }
    }

    private static CodexQuotaPoint ReadDuration(JsonElement window)
    {
        if (!window.TryGetProperty("limit_window_seconds", out var seconds) || seconds.ValueKind == JsonValueKind.Null)
            return CodexQuotaPoint.Unavailable();
        if (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetDecimal(out var value) ||
            value <= 0 || decimal.Truncate(value) != value || value > TimeSpan.MaxValue.Ticks / (decimal)TimeSpan.TicksPerSecond)
            return CodexQuotaPoint.Error("invalid_duration");
        var ticks = decimal.ToInt64(value * TimeSpan.TicksPerSecond);
        return CodexQuotaPoint.Fresh(MetricValue.From(TimeSpan.FromTicks(ticks)));
    }

    private readonly record struct Window(CodexQuotaPoint Remaining, CodexQuotaPoint Reset,
        CodexQuotaPoint Duration)
    {
        public static Window Missing => new(CodexQuotaPoint.Unavailable(), CodexQuotaPoint.Unavailable(),
            CodexQuotaPoint.Unavailable());
        public static Window InvalidSchema => new(CodexQuotaPoint.Error("schema_invalid"),
            CodexQuotaPoint.Error("schema_invalid"), CodexQuotaPoint.Error("schema_invalid"));
        public static Window Elapsed => new(CodexQuotaPoint.Unavailable("reset_elapsed"),
            CodexQuotaPoint.Unavailable("reset_elapsed"), CodexQuotaPoint.Unavailable("reset_elapsed"));
    }
}
