#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Cerith.Core;

namespace Cerith.WidgetHost;

internal enum CompositionKind { Text, Bar, Ring }

internal sealed record CompositionRow(CompositionKind Kind, string Id, string Format, double? Minimum = null, double? Maximum = null)
{
    internal void Validate()
    {
        if (Id is null) throw new InvalidDataException("Invalid metric identifier.");
        int bytes;
        try { bytes = new UTF8Encoding(false, true).GetByteCount(Id); }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid metric identifier.", error); }
        if (!Enum.IsDefined(Kind) || Id.Length == 0 || bytes > 256 || Id.Any(char.IsControl))
            throw new InvalidDataException("Invalid metric identifier.");
        try { _ = new MetricId(Id); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid metric identifier.", exception); }
        if (Format is not ("0" or "0.0" or "0.00" or "0.000"))
            throw new InvalidDataException("Invalid numeric format.");
        if (Kind == CompositionKind.Text)
        {
            if (Minimum is not null || Maximum is not null) throw new InvalidDataException("Text must have no range.");
        }
        else if (Minimum is not double min || Maximum is not double max || !double.IsFinite(min) ||
                 !double.IsFinite(max) || max <= min || !double.IsFinite(max - min))
            throw new InvalidDataException("Range must have a finite positive span.");
    }
}

internal sealed class MetricComposition
{
    internal ImmutableArray<CompositionRow> Rows { get; }

    internal MetricComposition(IEnumerable<CompositionRow> rows)
    {
        Rows = rows.ToImmutableArray();
        if (Rows.Length is < 1 or > 16) throw new InvalidDataException("Composition must have 1–16 rows.");
        foreach (var row in Rows) row.Validate();
    }

    internal static MetricComposition Default => new([
        new(CompositionKind.Text, "cpu.total.usage", "0.0"),
        new(CompositionKind.Bar, "cpu.total.usage", "0.0", 0, 100),
        new(CompositionKind.Ring, "cpu.total.usage", "0.0", 0, 100),
        new(CompositionKind.Text, "ram.usage", "0.0"),
        new(CompositionKind.Text, "game.fps.display", "0.0"),
        new(CompositionKind.Text, "ai.codex.quota.primary.remaining", "0.0"),
        new(CompositionKind.Bar, "ai.codex.quota.primary.remaining", "0.0", 0, 100)
    ]);

    internal string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("rows");
            foreach (var row in Rows)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", row.Kind.ToString());
                writer.WriteString("id", row.Id);
                writer.WriteString("format", row.Format);
                if (row.Minimum is double min) writer.WriteNumber("min", min);
                if (row.Maximum is double max) writer.WriteNumber("max", max);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (stream.Length > 65536) throw new InvalidDataException("Composition exceeds 64 KiB.");
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static MetricComposition Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > 65536) throw new InvalidDataException("Invalid composition size.");
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            RequireProperties(root, "version", "rows");
            if (root.GetProperty("version").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("version").TryGetInt32(out int version) || version != 1)
                throw new NotSupportedException("Unsupported composition version.");
            var rows = root.GetProperty("rows");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 16)
                throw new InvalidDataException("Invalid composition row count.");
            var values = new List<CompositionRow>();
            foreach (var element in rows.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid row.");
                var kindString = element.GetProperty("kind").GetString();
                if (kindString is not ("Text" or "Bar" or "Ring"))
                    throw new InvalidDataException("Unknown composition kind.");
                var kind = Enum.Parse<CompositionKind>(kindString);
                RequireProperties(element, kind == CompositionKind.Text
                    ? ["kind", "id", "format"] : ["kind", "id", "format", "min", "max"]);
                values.Add(new CompositionRow(kind, element.GetProperty("id").GetString()!,
                    element.GetProperty("format").GetString()!,
                    kind == CompositionKind.Text ? null : element.GetProperty("min").GetDouble(),
                    kind == CompositionKind.Text ? null : element.GetProperty("max").GetDouble()));
            }
            return new MetricComposition(values);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid composition JSON.", error); }
        catch (KeyNotFoundException error) { throw new InvalidDataException("Missing composition property.", error); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid composition property type.", error); }
        catch (FormatException error) { throw new InvalidDataException("Invalid composition number.", error); }
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate composition property.");
        if (seen.Count != names.Length) throw new InvalidDataException("Missing composition property.");
    }
}
