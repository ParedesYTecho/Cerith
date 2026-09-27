#nullable enable

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.Core;

public static class MetricIpcProtocol
{
    public const string LocalPipeName = "Cerith.Metrics.M0";
    public const int Version = 1;
    public const int MaxFrameSize = 64 * 1024;

    public static Task WriteSubscribeAsync(Stream stream, MetricId id, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, new Message(Version, "subscribe", id.Value), cancellationToken);

    public static async Task<MetricId> ReadSubscribeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var message = await ReadAsync(stream, "subscribe", cancellationToken).ConfigureAwait(false);
        try { return new MetricId(message.MetricId ?? throw new InvalidDataException("Missing metric ID.")); }
        catch (ArgumentException ex) { throw new InvalidDataException("Invalid metric ID.", ex); }
    }

    public static Task WriteAcceptedAsync(Stream stream, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, new Message(Version, "accepted"), cancellationToken);

    public static async Task ReadAcceptedAsync(Stream stream, CancellationToken cancellationToken = default) =>
        _ = await ReadAsync(stream, "accepted", cancellationToken).ConfigureAwait(false);

    public static Task WriteSnapshotAsync(Stream stream, MetricSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var value = snapshot.Value;
        return WriteAsync(stream, new Message(Version, "snapshot", snapshot.MetricId.Value,
            value?.Kind, value is null ? null : Format(value.Value), snapshot.ObservedAt,
            snapshot.State, snapshot.Confidence, snapshot.Source, snapshot.ErrorCode), cancellationToken);
    }

    public static async Task<MetricSnapshot> ReadSnapshotAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var message = await ReadAsync(stream, "snapshot", cancellationToken).ConfigureAwait(false);
        try
        {
            MetricValue? value = message.ValueKind is null ? null : Parse(message.ValueKind.Value,
                message.Value ?? throw new InvalidDataException("Missing value."));
            if (message.ValueKind is null && message.Value is not null)
                throw new InvalidDataException("Value kind is missing.");
            return new MetricSnapshot(new MetricId(message.MetricId ?? throw new InvalidDataException("Missing metric ID.")),
                value, message.ObservedAt ?? throw new InvalidDataException("Missing timestamp."),
                message.State ?? throw new InvalidDataException("Missing state."),
                message.Confidence ?? throw new InvalidDataException("Missing confidence."),
                message.Source, message.ErrorCode);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid snapshot payload.", ex);
        }
    }

    private static string Format(MetricValue value) => value.Kind switch
    {
        MetricValueKind.Double => value.AsDouble().ToString("R", CultureInfo.InvariantCulture),
        MetricValueKind.Int64 => value.AsInt64().ToString(CultureInfo.InvariantCulture),
        MetricValueKind.Decimal => value.AsDecimal().ToString(CultureInfo.InvariantCulture),
        MetricValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        MetricValueKind.String => value.AsString(),
        MetricValueKind.DateTimeOffset => value.AsDateTimeOffset().ToString("O", CultureInfo.InvariantCulture),
        MetricValueKind.TimeSpan => value.AsTimeSpan().ToString("c", CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("Unknown value kind.")
    };

    private static MetricValue Parse(MetricValueKind kind, string text) => kind switch
    {
        MetricValueKind.Double => MetricValue.From(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
        MetricValueKind.Int64 => MetricValue.From(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
        MetricValueKind.Decimal => MetricValue.From(decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)),
        MetricValueKind.Boolean => MetricValue.From(bool.Parse(text)),
        MetricValueKind.String => MetricValue.From(text),
        MetricValueKind.DateTimeOffset => MetricValue.From(DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None)),
        MetricValueKind.TimeSpan => MetricValue.From(TimeSpan.ParseExact(text, "c", CultureInfo.InvariantCulture)),
        _ => throw new InvalidDataException("Unknown value kind.")
    };

    private static async Task WriteAsync(Stream stream, Message message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaxFrameSize) throw new InvalidDataException("Frame exceeds limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Message> ReadAsync(Stream stream, string expectedKind, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
            throw new EndOfStreamException();
        try { await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated frame header.", ex); }
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxFrameSize) throw new InvalidDataException("Invalid frame length.");
        var payload = new byte[length];
        try { await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated frame payload.", ex); }
        Message message;
        try { message = JsonSerializer.Deserialize<Message>(payload) ?? throw new InvalidDataException("Empty message."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid frame payload.", ex); }
        if (message.Version != Version) throw new InvalidDataException("Unsupported protocol version.");
        if (message.Kind != expectedKind) throw new InvalidDataException("Unexpected message kind.");
        return message;
    }

    private sealed record Message(int Version, string Kind, string? MetricId = null,
        MetricValueKind? ValueKind = null, string? Value = null, DateTimeOffset? ObservedAt = null,
        MetricState? State = null, MetricConfidence? Confidence = null, string? Source = null,
        string? ErrorCode = null);
}
