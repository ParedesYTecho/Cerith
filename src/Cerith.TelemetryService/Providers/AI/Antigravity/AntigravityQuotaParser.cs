#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Antigravity;

internal readonly record struct AntigravityQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static AntigravityQuotaPoint Fresh(MetricValue value) =>
        new(MetricState.Fresh, value, null);

    public static AntigravityQuotaPoint Unavailable(string? errorCode = null) =>
        new(MetricState.Unavailable, null, errorCode);

    public static AntigravityQuotaPoint Error(string code) =>
        new(MetricState.Error, null, code);
}

internal sealed record AntigravityQuotaReading(AntigravityQuotaPoint[] Points, DateTimeOffset ObservedAt)
{
    public static AntigravityQuotaReading Failure(string code, DateTimeOffset observedAt) =>
        new(Enumerable.Repeat(AntigravityQuotaPoint.Error(code), AntigravityQuotaParser.MetricCount).ToArray(), observedAt);
}

internal static class AntigravityQuotaParser
{
    internal const int MetricCount = 8;
    internal const int MaxJsonDepth = 32;

    internal static readonly string[] BucketIds =
    [
        "3p-weekly",
        "3p-5h",
        "gemini-weekly",
        "gemini-5h"
    ];

    internal static readonly string[] MetricIds =
    [
        "ai.antigravity.quota.3p-weekly.remaining",
        "ai.antigravity.quota.3p-5h.remaining",
        "ai.antigravity.quota.gemini-weekly.remaining",
        "ai.antigravity.quota.gemini-5h.remaining",
        "ai.antigravity.quota.3p-weekly.reset",
        "ai.antigravity.quota.3p-5h.reset",
        "ai.antigravity.quota.gemini-weekly.reset",
        "ai.antigravity.quota.gemini-5h.reset"
    ];

    public static AntigravityQuotaReading Parse(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException)
        {
            return AntigravityQuotaReading.Failure("schema_invalid", observedAt);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasSuccessfulCode(root))
                return AntigravityQuotaReading.Failure("schema_invalid", observedAt);

            if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array ||
                groups.GetArrayLength() == 0)
                return AntigravityQuotaReading.Failure("schema_invalid", observedAt);

            var points = Enumerable.Repeat(AntigravityQuotaPoint.Unavailable(), MetricCount).ToArray();
            var seen = new bool[BucketIds.Length];
            var recognized = 0;

            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("buckets", out var buckets) ||
                    buckets.ValueKind != JsonValueKind.Array)
                    return AntigravityQuotaReading.Failure("schema_invalid", observedAt);

                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object || !bucket.TryGetProperty("bucketId", out var id) ||
                        id.ValueKind != JsonValueKind.String)
                        return AntigravityQuotaReading.Failure("schema_invalid", observedAt);

                    var index = Array.IndexOf(BucketIds, id.GetString());
                    if (index < 0)
                        continue;

                    recognized++;
                    if (seen[index])
                    {
                        SetBucketError(points, index, "duplicate_bucket");
                        continue;
                    }

                    seen[index] = true;
                    ParseBucket(bucket, index, observedAt, points);
                }
            }

            return recognized == 0
                ? AntigravityQuotaReading.Failure("schema_invalid", observedAt)
                : new AntigravityQuotaReading(points, observedAt);
        }
    }

    private static bool HasSuccessfulCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code) || code.ValueKind == JsonValueKind.Null)
            return true;
        if (code.ValueKind == JsonValueKind.Number)
            return code.TryGetInt32(out var number) && number == 0;
        if (code.ValueKind != JsonValueKind.String)
            return false;

        var text = code.GetString();
        return StringComparer.OrdinalIgnoreCase.Equals(text, "OK") ||
            StringComparer.OrdinalIgnoreCase.Equals(text, "SUCCESS") ||
            StringComparer.Ordinal.Equals(text, "0");
    }

    private static void ParseBucket(JsonElement bucket, int bucketIndex, DateTimeOffset observedAt,
        AntigravityQuotaPoint[] points)
    {
        var remainingIndex = bucketIndex;
        var resetIndex = bucketIndex + BucketIds.Length;
        points[remainingIndex] = ReadRemaining(bucket);
        points[resetIndex] = ReadReset(bucket);

        if (points[resetIndex].Value is MetricValue reset && reset.AsDateTimeOffset() <= observedAt)
        {
            points[remainingIndex] = AntigravityQuotaPoint.Unavailable("reset_elapsed");
            points[resetIndex] = AntigravityQuotaPoint.Unavailable("reset_elapsed");
        }
    }

    private static AntigravityQuotaPoint ReadRemaining(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("remainingFraction", out var fraction) || fraction.ValueKind == JsonValueKind.Null)
            return AntigravityQuotaPoint.Unavailable();
        if (fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value is < 0 or > 1)
            return AntigravityQuotaPoint.Error("invalid_quota");

        return AntigravityQuotaPoint.Fresh(MetricValue.From(value * 100d));
    }

    private static AntigravityQuotaPoint ReadReset(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("resetTime", out var reset) || reset.ValueKind == JsonValueKind.Null)
            return AntigravityQuotaPoint.Unavailable();

        if (reset.ValueKind != JsonValueKind.String || !TryParseResetString(reset.GetString(), out var instant))
        {
            return AntigravityQuotaPoint.Error("invalid_reset");
        }

        return AntigravityQuotaPoint.Fresh(MetricValue.From(instant));
    }

    private static bool TryParseResetString(string? text, out DateTimeOffset instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasExplicitZone = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:\d{2}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return hasExplicitZone && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out instant);
    }

    private static void SetBucketError(AntigravityQuotaPoint[] points, int bucketIndex, string code)
    {
        points[bucketIndex] = AntigravityQuotaPoint.Error(code);
        points[bucketIndex + BucketIds.Length] = AntigravityQuotaPoint.Error(code);
    }
}
