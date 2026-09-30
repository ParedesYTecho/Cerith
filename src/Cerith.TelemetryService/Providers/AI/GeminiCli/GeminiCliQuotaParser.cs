#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.GeminiCli;

internal readonly record struct GeminiCliQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static GeminiCliQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static GeminiCliQuotaPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static GeminiCliQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal sealed record GeminiCliModelQuota(string ModelId, GeminiCliQuotaPoint Remaining, GeminiCliQuotaPoint Reset);

internal sealed record GeminiCliQuotaReading(bool IsValid, string? ErrorCode,
    IReadOnlyDictionary<string, GeminiCliModelQuota> Models)
{
    public static GeminiCliQuotaReading Failure(string code) =>
        new(false, code, new Dictionary<string, GeminiCliModelQuota>(StringComparer.Ordinal));
}

internal static class GeminiCliQuotaParser
{
    internal const int MaxModelIdLength = 128;
    internal const int MaxModelCount = 64;

    public static GeminiCliQuotaReading Parse(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return GeminiCliQuotaReading.Failure("schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return GeminiCliQuotaReading.Failure("schema_invalid");
            var models = new Dictionary<string, GeminiCliModelQuota>(StringComparer.Ordinal);
            if (!root.TryGetProperty("buckets", out var buckets) || buckets.ValueKind == JsonValueKind.Null)
                return new GeminiCliQuotaReading(true, null, models);
            if (buckets.ValueKind != JsonValueKind.Array) return GeminiCliQuotaReading.Failure("schema_invalid");

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) return GeminiCliQuotaReading.Failure("schema_invalid");
                if (!bucket.TryGetProperty("modelId", out var modelProperty)) continue;
                if (modelProperty.ValueKind != JsonValueKind.String) return GeminiCliQuotaReading.Failure("schema_invalid");
                var modelId = modelProperty.GetString();
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                if (!IsSafeModelId(modelId)) return GeminiCliQuotaReading.Failure("schema_invalid");
                if (models.ContainsKey(modelId)) return GeminiCliQuotaReading.Failure("duplicate_model_bucket");
                if (models.Count >= MaxModelCount) return GeminiCliQuotaReading.Failure("model_limit_exceeded");

                var remaining = ReadRemaining(bucket);
                var reset = ReadReset(bucket);
                if (reset.Value is MetricValue resetValue && resetValue.AsDateTimeOffset() <= observedAt)
                {
                    remaining = GeminiCliQuotaPoint.Unavailable("reset_elapsed");
                    reset = GeminiCliQuotaPoint.Unavailable("reset_elapsed");
                }

                models.Add(modelId, new GeminiCliModelQuota(modelId, remaining, reset));
            }

            return new GeminiCliQuotaReading(true, null, models);
        }
    }

    public static (string? ProjectId, string? ErrorCode) ParseProject(ReadOnlyMemory<byte> payload,
        string? selectedProjectId = null)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { return (null, "schema_invalid"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "schema_invalid");
            if (!root.TryGetProperty("currentTier", out var tier) || tier.ValueKind == JsonValueKind.Null)
                return (null, "project_unavailable");
            if (tier.ValueKind != JsonValueKind.Object || !tier.TryGetProperty("id", out var tierId) ||
                tierId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tierId.GetString()) ||
                tierId.GetString()!.Length > 128)
                return (null, "schema_invalid");

            string? returnedProjectId = null;
            if (root.TryGetProperty("cloudaicompanionProject", out var project) && project.ValueKind != JsonValueKind.Null)
            {
                returnedProjectId = project.ValueKind switch
                {
                    JsonValueKind.String => project.GetString(),
                    JsonValueKind.Object => ReadProjectObject(project),
                    _ => null
                };
                if (returnedProjectId is null || !IsSafeProjectId(returnedProjectId))
                    return (null, "schema_invalid");
            }

            if (selectedProjectId is not null && returnedProjectId is not null &&
                !StringComparer.Ordinal.Equals(selectedProjectId, returnedProjectId))
                return (null, "project_mismatch");

            var projectId = selectedProjectId ?? returnedProjectId;
            if (projectId is null) return (null, "project_unavailable");
            if (!IsSafeProjectId(projectId)) return (null, "schema_invalid");
            return (projectId, null);
        }
    }

    private static string? ReadProjectObject(JsonElement project)
    {
        if (project.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) return id.GetString();
        if (project.TryGetProperty("projectId", out var projectId) && projectId.ValueKind == JsonValueKind.String)
            return projectId.GetString();
        return null;
    }

    private static GeminiCliQuotaPoint ReadRemaining(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("remainingFraction", out var fraction) || fraction.ValueKind == JsonValueKind.Null)
            return GeminiCliQuotaPoint.Unavailable();
        if (fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value is < 0 or > 1)
            return GeminiCliQuotaPoint.Error("invalid_quota");
        return GeminiCliQuotaPoint.Fresh(MetricValue.From(value * 100d));
    }

    private static GeminiCliQuotaPoint ReadReset(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("resetTime", out var reset) || reset.ValueKind == JsonValueKind.Null)
            return GeminiCliQuotaPoint.Unavailable();
        if (reset.ValueKind != JsonValueKind.String) return GeminiCliQuotaPoint.Error("invalid_reset");
        var value = reset.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !HasExplicitOffset(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return GeminiCliQuotaPoint.Error("invalid_reset");
        return GeminiCliQuotaPoint.Fresh(MetricValue.From(parsed));
    }

    private static bool IsSafeModelId(string value)
    {
        if (value.Length > MaxModelIdLength) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-') return false;
        return true;
    }

    internal static bool IsSafeProjectId(string value)
    {
        if (value.Length is 0 or > 128) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-') return false;
        return true;
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z')) return true;
        if (value.Length < 6) return false;
        var offset = value.AsSpan(value.Length - 6);
        return (offset[0] == '+' || offset[0] == '-') && char.IsAsciiDigit(offset[1]) &&
            char.IsAsciiDigit(offset[2]) && offset[3] == ':' && char.IsAsciiDigit(offset[4]) &&
            char.IsAsciiDigit(offset[5]);
    }
}
