#nullable enable

using System;
using System.Globalization;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.CommandCode;

internal sealed record CommandCodeParsedPoint(
    MetricValue? Value,
    MetricState State,
    MetricConfidence Confidence,
    string? ErrorCode)
{
    internal static CommandCodeParsedPoint Fresh(MetricValue value) =>
        new(value, MetricState.Fresh, MetricConfidence.Measured, null);

    internal static CommandCodeParsedPoint Unavailable(string? errorCode = null) =>
        new(null, MetricState.Unavailable, MetricConfidence.Unknown, errorCode);

    internal static CommandCodeParsedPoint Error(string errorCode) =>
        new(null, MetricState.Error, MetricConfidence.Unknown, errorCode);
}

internal sealed record CommandCodeParsedWindow(
    CommandCodeParsedPoint UsedPercent,
    CommandCodeParsedPoint Reset,
    DateTimeOffset? ResetAt);

internal sealed record CommandCodeParsedSnapshot(
    CommandCodeParsedWindow FiveHour,
    CommandCodeParsedWindow Weekly,
    CommandCodeParsedPoint SubscriptionStatus,
    CommandCodeParsedPoint SubscriptionPlan);

internal readonly struct CommandCodeOrgScope
{
    internal CommandCodeOrgScope(bool isValid, string? orgId)
    {
        IsValid = isValid;
        OrgId = orgId;
    }

    internal bool IsValid { get; }
    internal string? OrgId { get; }
}

internal static class CommandCodeQuotaParser
{
    internal static CommandCodeOrgScope ParseOrgScope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return new(false, null);

        string? rootOrgId = null;
        string? dataOrgId = null;
        if (!TryParseOrg(root, out rootOrgId)) return new(false, null);
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind != JsonValueKind.Null &&
                (data.ValueKind != JsonValueKind.Object || !TryParseOrg(data, out dataOrgId)))
                return new(false, null);
        }

        if (rootOrgId is not null && dataOrgId is not null &&
            !string.Equals(rootOrgId, dataOrgId, StringComparison.Ordinal))
            return new(false, null);

        return new(true, dataOrgId ?? rootOrgId);
    }

    internal static CommandCodeParsedSnapshot Parse(
        JsonElement credits,
        JsonElement subscription)
    {
        var fiveHour = ParseWindow(credits, "fiveHour");
        var weekly = ParseWindow(credits, "weekly");
        var (status, plan) = ParseSubscription(subscription);
        return new CommandCodeParsedSnapshot(fiveHour, weekly, status, plan);
    }

    private static CommandCodeParsedWindow ParseWindow(JsonElement credits, string propertyName)
    {
        if (credits.ValueKind != JsonValueKind.Object)
            return InvalidWindow("schema_invalid");
        if (!credits.TryGetProperty("windowLimits", out var limits) || limits.ValueKind == JsonValueKind.Null)
            return MissingWindow();
        if (limits.ValueKind != JsonValueKind.Object)
            return InvalidWindow("schema_invalid");
        if (!limits.TryGetProperty(propertyName, out var window) || window.ValueKind == JsonValueKind.Null)
            return MissingWindow();
        if (window.ValueKind != JsonValueKind.Object)
            return InvalidWindow("schema_invalid");

        var usedPoint = ParseUsedPercent(window);
        var (resetPoint, resetAt) = ParseReset(window);
        return new CommandCodeParsedWindow(usedPoint, resetPoint, resetAt);
    }

    private static CommandCodeParsedPoint ParseUsedPercent(JsonElement window)
    {
        if (!window.TryGetProperty("used", out var used) || used.ValueKind == JsonValueKind.Null ||
            !window.TryGetProperty("cap", out var cap) || cap.ValueKind == JsonValueKind.Null)
            return CommandCodeParsedPoint.Unavailable("quota_missing");

        if (!TryNumber(used, out var usedValue) || !TryNumber(cap, out var capValue) ||
            usedValue < 0 || capValue <= 0)
            return CommandCodeParsedPoint.Error("invalid_quota");

        var percentage = usedValue / capValue * 100d;
        if (!double.IsFinite(percentage)) return CommandCodeParsedPoint.Error("invalid_quota");
        return CommandCodeParsedPoint.Fresh(MetricValue.From(percentage));
    }

    private static (CommandCodeParsedPoint Point, DateTimeOffset? ResetAt) ParseReset(JsonElement window)
    {
        if (!window.TryGetProperty("resetAt", out var reset) || reset.ValueKind == JsonValueKind.Null)
            return (CommandCodeParsedPoint.Unavailable("reset_missing"), null);
        if (reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var epochMilliseconds))
            return (CommandCodeParsedPoint.Error("invalid_reset"), null);

        try
        {
            var resetAt = DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);
            return (CommandCodeParsedPoint.Fresh(MetricValue.From(resetAt)), resetAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (CommandCodeParsedPoint.Error("invalid_reset"), null);
        }
    }

    private static (CommandCodeParsedPoint Status, CommandCodeParsedPoint Plan) ParseSubscription(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (CommandCodeParsedPoint.Error("schema_invalid"), CommandCodeParsedPoint.Error("schema_invalid"));

        var source = root;
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind != JsonValueKind.Object)
                return (CommandCodeParsedPoint.Error("schema_invalid"), CommandCodeParsedPoint.Error("schema_invalid"));
            source = data;
        }

        var status = ParseStatus(source);
        var plan = ParsePlan(source);
        return (status, plan);
    }

    private static CommandCodeParsedPoint ParseStatus(JsonElement source)
    {
        if (!source.TryGetProperty("status", out var status) || status.ValueKind == JsonValueKind.Null)
            return CommandCodeParsedPoint.Unavailable("subscription_status_missing");
        if (status.ValueKind != JsonValueKind.String || !TrySafeToken(status.GetString(), out var value))
            return CommandCodeParsedPoint.Error("invalid_subscription_status");
        return CommandCodeParsedPoint.Fresh(MetricValue.From(value));
    }

    private static CommandCodeParsedPoint ParsePlan(JsonElement source)
    {
        if (!source.TryGetProperty("planId", out var planId) || planId.ValueKind == JsonValueKind.Null)
            return CommandCodeParsedPoint.Unavailable("subscription_plan_missing");
        if (planId.ValueKind != JsonValueKind.String || !TryPlanLabel(planId.GetString(), out var label))
            return CommandCodeParsedPoint.Unavailable("subscription_plan_unknown");
        return CommandCodeParsedPoint.Fresh(MetricValue.From(label));
    }

    private static bool TryPlanLabel(string? planId, out string label)
    {
        label = string.Empty;
        if (string.IsNullOrWhiteSpace(planId) || planId.Length > 128) return false;
        var normalized = planId.Trim().ToLowerInvariant().Replace('_', '-');

        label = normalized switch
        {
            var id when id.StartsWith("individual-pro-v1", StringComparison.Ordinal) => "Pro",
            var id when id.StartsWith("individual-provider", StringComparison.Ordinal) => "Provider",
            var id when id.StartsWith("individual-goat", StringComparison.Ordinal) => "GOAT",
            var id when id.StartsWith("individual-go", StringComparison.Ordinal) => "Go",
            var id when id.StartsWith("individual-pro", StringComparison.Ordinal) => "Pro",
            var id when id.StartsWith("individual-max", StringComparison.Ordinal) => "Max",
            var id when id.StartsWith("individual-ultra", StringComparison.Ordinal) => "Ultra",
            var id when id.StartsWith("teams-pro", StringComparison.Ordinal) => "Teams Pro",
            _ => string.Empty
        };
        return label.Length > 0;
    }

    private static bool TrySafeToken(string? value, out string safe)
    {
        safe = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        foreach (var character in value)
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return false;
        safe = value;
        return true;
    }

    private static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out number) && double.IsFinite(number);
        if (value.ValueKind == JsonValueKind.String)
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                   double.IsFinite(number);
        return false;
    }

    private static bool TryParseOrg(JsonElement parent, out string? orgId)
    {
        orgId = null;
        if (!parent.TryGetProperty("org", out var org) || org.ValueKind == JsonValueKind.Null) return true;
        if (org.ValueKind != JsonValueKind.Object) return false;
        if (!org.TryGetProperty("id", out var id) || id.ValueKind == JsonValueKind.Null) return true;

        var value = id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        foreach (var character in value)
            if (char.IsControl(character)) return false;
        orgId = value;
        return true;
    }

    private static CommandCodeParsedWindow MissingWindow() =>
        new(CommandCodeParsedPoint.Unavailable("quota_missing"),
            CommandCodeParsedPoint.Unavailable("reset_missing"), null);

    private static CommandCodeParsedWindow InvalidWindow(string errorCode) =>
        new(CommandCodeParsedPoint.Error(errorCode), CommandCodeParsedPoint.Error(errorCode), null);
}
