#nullable enable

using System;
using System.Globalization;
using System.IO;
using Cerith.Core;

namespace Cerith.WidgetHost;

internal sealed record MetricPresentationSpec
{
    public MetricId MetricId { get; }
    public string Label { get; }
    public string NumberFormat { get; }
    public string? Unit { get; }

    public MetricPresentationSpec(MetricId metricId, string label, string numberFormat, string? unit)
    {
        MetricId = metricId ?? throw new ArgumentNullException(nameof(metricId));
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(numberFormat);
        if (unit is not null && string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("Unit cannot be whitespace when provided.", nameof(unit));
        Label = label;
        NumberFormat = numberFormat;
        Unit = unit;
    }
}

internal readonly record struct MetricPresentationResult(
    string ValueText,
    string StateText,
    string ConfidenceText,
    MetricState State);

internal static class MetricPresentation
{
    public static MetricPresentationResult Create(MetricPresentationSpec spec, MetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.MetricId != spec.MetricId)
            throw new InvalidDataException("Snapshot MetricId does not match presentation spec.");

        var valueText = snapshot.State switch
        {
            MetricState.Fresh or MetricState.Stale => FormatValue(snapshot.Value!.Value, spec),
            MetricState.Unlimited => "Unlimited",
            _ => "—"
        };
        var stateText = snapshot.State == MetricState.Error && snapshot.ErrorCode is not null
            ? $"Error · {snapshot.ErrorCode}"
            : snapshot.State.ToString();

        return new MetricPresentationResult(valueText, stateText, snapshot.Confidence.ToString(), snapshot.State);
    }

    private static string FormatValue(MetricValue value, MetricPresentationSpec spec)
    {
        var text = value.Kind switch
        {
            MetricValueKind.Double => value.AsDouble().ToString(spec.NumberFormat, CultureInfo.InvariantCulture),
            MetricValueKind.Int64 => value.AsInt64().ToString(spec.NumberFormat, CultureInfo.InvariantCulture),
            MetricValueKind.Decimal => value.AsDecimal().ToString(spec.NumberFormat, CultureInfo.InvariantCulture),
            MetricValueKind.Boolean => value.AsBoolean() ? "True" : "False",
            MetricValueKind.String => value.AsString(),
            MetricValueKind.DateTimeOffset => value.AsDateTimeOffset().ToString("g", CultureInfo.CurrentCulture),
            MetricValueKind.TimeSpan => value.AsTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException("Metric value kind cannot be presented.")
        };
        return spec.Unit is null ? text : $"{text} {spec.Unit}";
    }
}
