#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal readonly record struct CursorQuotaPoint(MetricState State, MetricValue? Value, string? ErrorCode)
{
    public static CursorQuotaPoint Fresh(MetricValue value) => new(MetricState.Fresh, value, null);
    public static CursorQuotaPoint Stale(MetricValue value, string errorCode) => new(MetricState.Stale, value, errorCode);
    public static CursorQuotaPoint Unavailable(string? code = null) => new(MetricState.Unavailable, null, code);
    public static CursorQuotaPoint Error(string code) => new(MetricState.Error, null, code);
}

internal readonly record struct CursorSandAccess(bool Granted, string? ErrorCode);

internal static class CursorQuotaParser
{
    internal const int PlanPointCount = 5;
    internal const int SandPointCount = 3;

    public static CursorQuotaPoint[] ParseUsageSummary(ReadOnlyMemory<byte> payload, DateTimeOffset observedAt)
    {
        using JsonDocument? document = ParseJson(payload, PlanPointCount, out var failure);
        if (document is null) return failure;

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Repeat(PlanPointCount, CursorQuotaPoint.Error("schema_invalid"));

        var points = new CursorQuotaPoint[PlanPointCount];
        if (!root.TryGetProperty("individualUsage", out var individual) || individual.ValueKind == JsonValueKind.Null)
        {
            points[0] = points[1] = points[2] = CursorQuotaPoint.Unavailable();
        }
        else if (individual.ValueKind != JsonValueKind.Object)
        {
            points[0] = points[1] = points[2] = CursorQuotaPoint.Error("schema_invalid");
        }
        else if (!individual.TryGetProperty("plan", out var plan) || plan.ValueKind == JsonValueKind.Null)
        {
            points[0] = points[1] = points[2] = CursorQuotaPoint.Unavailable();
        }
        else if (plan.ValueKind != JsonValueKind.Object)
        {
            points[0] = points[1] = points[2] = CursorQuotaPoint.Error("schema_invalid");
        }
        else
        {
            points[0] = ReadPercentUsed(plan, "totalPercentUsed");
            points[1] = ReadPercentUsed(plan, "autoPercentUsed");
            points[2] = ReadPercentUsed(plan, "apiPercentUsed");
        }

        var cycle = ReadBillingCycle(root);
        points[3] = cycle.Reset;
        points[4] = cycle.Duration;
        if (cycle.ResetAt is DateTimeOffset resetAt && resetAt <= observedAt)
        {
            points[0] = points[1] = points[2] = CursorQuotaPoint.Unavailable("reset_elapsed");
            points[3] = points[4] = CursorQuotaPoint.Unavailable("reset_elapsed");
        }
        return points;
    }

    public static CursorSandAccess ParseSandAccess(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var fields = ReadProtoFields(payload);
            var status = FindLastField(fields, 1);
            if (status is null || status.Value.WireType != 0) return new CursorSandAccess(false, "schema_invalid");
            return new CursorSandAccess(status.Value.Varint == 1, null);
        }
        catch (InvalidDataException) { return new CursorSandAccess(false, "schema_invalid"); }
    }

    public static CursorQuotaPoint[] ParseSandUsage(ReadOnlyMemory<byte> payload, bool granted,
        DateTimeOffset observedAt)
    {
        if (!granted) return Repeat(SandPointCount, CursorQuotaPoint.Unavailable());

        List<ProtoField> fields;
        try { fields = ReadProtoFields(payload); }
        catch (InvalidDataException) { return Repeat(SandPointCount, CursorQuotaPoint.Error("schema_invalid")); }

        var usageField = FindLastField(fields, 3);
        var startField = FindLastField(fields, 1);
        var resetField = FindLastField(fields, 2);
        var includedLimitField = FindLastField(fields, 4);
        if (includedLimitField is ProtoField includedLimit)
        {
            if (includedLimit.WireType != 0)
                return Repeat(SandPointCount, CursorQuotaPoint.Error("schema_invalid"));
            if (includedLimit.Varint != 0)
                return Repeat(SandPointCount, CursorQuotaPoint.Unavailable());
        }

        var result = new CursorQuotaPoint[SandPointCount];

        var resetAt = ReadTimestamp(resetField, "invalid_reset");
        result[1] = resetAt.Point;
        var periodStart = ReadTimestamp(startField, "invalid_duration");
        if (resetAt.Value is DateTimeOffset reset && reset <= observedAt)
        {
            return Repeat(SandPointCount, CursorQuotaPoint.Unavailable("reset_elapsed"));
        }

        if (periodStart.Value is DateTimeOffset start && resetAt.Value is DateTimeOffset end)
        {
            result[2] = end > start
                ? CursorQuotaPoint.Fresh(MetricValue.From(end - start))
                : CursorQuotaPoint.Error("invalid_duration");
        }
        else
        {
            result[2] = periodStart.Point.State == MetricState.Error
                ? periodStart.Point
                : resetAt.Point.State == MetricState.Error
                    ? CursorQuotaPoint.Error("invalid_duration")
                    : CursorQuotaPoint.Unavailable();
        }

        if (usageField is null)
            result[0] = CursorQuotaPoint.Unavailable();
        else if (usageField.Value.WireType != 1)
            result[0] = CursorQuotaPoint.Error("schema_invalid");
        else
        {
            var used = BitConverter.Int64BitsToDouble(unchecked((long)usageField.Value.Fixed64));
            result[0] = double.IsFinite(used) && used is >= 0 and <= 100
                ? CursorQuotaPoint.Fresh(MetricValue.From(used))
                : CursorQuotaPoint.Error("invalid_quota");
        }

        return result;
    }

    private static JsonDocument? ParseJson(ReadOnlyMemory<byte> payload, int count, out CursorQuotaPoint[] failure)
    {
        try
        {
            failure = [];
            return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
        }
        catch (JsonException)
        {
            failure = Repeat(count, CursorQuotaPoint.Error("schema_invalid"));
            return null;
        }
    }

    private static CursorQuotaPoint ReadPercentUsed(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var used) || used.ValueKind == JsonValueKind.Null)
            return CursorQuotaPoint.Unavailable();
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percentage) ||
            !double.IsFinite(percentage) || percentage is < 0 or > 100)
            return CursorQuotaPoint.Error("invalid_quota");
        return CursorQuotaPoint.Fresh(MetricValue.From(percentage));
    }

    private static (CursorQuotaPoint Reset, CursorQuotaPoint Duration, DateTimeOffset? ResetAt)
        ReadBillingCycle(JsonElement root)
    {
        var reset = ReadIsoTimestamp(root, "billingCycleEnd", "invalid_reset");
        var start = ReadIsoTimestamp(root, "billingCycleStart", "invalid_duration");
        var duration = reset.Value is DateTimeOffset end && start.Value is DateTimeOffset beginning
            ? end > beginning
                ? CursorQuotaPoint.Fresh(MetricValue.From(end - beginning))
                : CursorQuotaPoint.Error("invalid_duration")
            : start.Point.State == MetricState.Error
                ? start.Point
                : reset.Point.State == MetricState.Error
                    ? CursorQuotaPoint.Error("invalid_duration")
                    : CursorQuotaPoint.Unavailable();

        return (reset.Point, duration, reset.Value);
    }

    private static (CursorQuotaPoint Point, DateTimeOffset? Value) ReadIsoTimestamp(JsonElement parent,
        string property, string errorCode)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return (CursorQuotaPoint.Unavailable(), null);
        if (value.ValueKind != JsonValueKind.String || !TryParseExplicitOffset(value.GetString(), out var timestamp))
            return (CursorQuotaPoint.Error(errorCode), null);
        return (CursorQuotaPoint.Fresh(MetricValue.From(timestamp)), timestamp);
    }

    private static (CursorQuotaPoint Point, DateTimeOffset? Value) ReadTimestamp(ProtoField? field, string errorCode)
    {
        if (field is null) return (CursorQuotaPoint.Unavailable(), null);
        if (field.Value.WireType != 2) return (CursorQuotaPoint.Error(errorCode), null);
        try
        {
            var timestampFields = ReadProtoFields(field.Value.Bytes);
            var secondsField = FindLastField(timestampFields, 1);
            var nanosField = FindLastField(timestampFields, 2);
            if (secondsField is null || secondsField.Value.WireType != 0 ||
                nanosField is { WireType: not 0 })
                return (CursorQuotaPoint.Error(errorCode), null);

            var seconds = unchecked((long)secondsField.Value.Varint);
            var nanos = nanosField?.Varint ?? 0;
            if (nanos > 999_999_999) return (CursorQuotaPoint.Error(errorCode), null);
            var result = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks((long)(nanos / 100));
            return (CursorQuotaPoint.Fresh(MetricValue.From(result)), result);
        }
        catch (ArgumentOutOfRangeException) { return (CursorQuotaPoint.Error(errorCode), null); }
        catch (InvalidDataException) { return (CursorQuotaPoint.Error(errorCode), null); }
    }

    private static bool TryParseExplicitOffset(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('T')) return false;
        var hasUtcSuffix = value.EndsWith('Z') || value.EndsWith('z');
        var hasNumericOffset = value.Length >= 6 &&
            (value[^6] is '+' or '-') && value[^3] == ':' &&
            char.IsAsciiDigit(value[^5]) && char.IsAsciiDigit(value[^4]) &&
            char.IsAsciiDigit(value[^2]) && char.IsAsciiDigit(value[^1]);
        return (hasUtcSuffix || hasNumericOffset) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static List<ProtoField> ReadProtoFields(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        var fields = new List<ProtoField>();
        var offset = 0;
        while (offset < span.Length)
        {
            var tag = ReadVarint(span, ref offset);
            var number = tag >> 3;
            var wireType = (int)(tag & 7);
            if (number is 0 or > int.MaxValue) throw new InvalidDataException();

            switch (wireType)
            {
                case 0:
                    fields.Add(new ProtoField((int)number, wireType, ReadVarint(span, ref offset), 0, default));
                    break;
                case 1:
                    if (span.Length - offset < 8) throw new InvalidDataException();
                    fields.Add(new ProtoField((int)number, wireType, 0,
                        BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, 8)), default));
                    offset += 8;
                    break;
                case 2:
                    var length = ReadVarint(span, ref offset);
                    if (length > int.MaxValue || (ulong)(span.Length - offset) < length) throw new InvalidDataException();
                    fields.Add(new ProtoField((int)number, wireType, 0, 0,
                        payload.Slice(offset, (int)length)));
                    offset += (int)length;
                    break;
                case 5:
                    if (span.Length - offset < 4) throw new InvalidDataException();
                    offset += 4;
                    break;
                default:
                    throw new InvalidDataException();
            }
        }
        return fields;
    }

    private static ProtoField? FindLastField(List<ProtoField> fields, int number)
    {
        for (var index = fields.Count - 1; index >= 0; index--)
            if (fields[index].Number == number) return fields[index];
        return null;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int offset)
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            if (offset >= span.Length) throw new InvalidDataException();
            var current = span[offset++];
            if (index == 9 && current > 1) throw new InvalidDataException();
            value |= (ulong)(current & 0x7f) << (index * 7);
            if ((current & 0x80) == 0) return value;
        }
        throw new InvalidDataException();
    }

    private static CursorQuotaPoint[] Repeat(int count, CursorQuotaPoint point)
    {
        var result = new CursorQuotaPoint[count];
        Array.Fill(result, point);
        return result;
    }

    private readonly record struct ProtoField(int Number, int WireType, ulong Varint, ulong Fixed64,
        ReadOnlyMemory<byte> Bytes);
}
