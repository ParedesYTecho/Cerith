#nullable enable

using System;
using System.Globalization;
using System.IO;
using Cerith.Core;

namespace Cerith.WidgetHost;

internal sealed record MetricPresentationSpec
{
    public MetricId MetricId { get; }
    public string NumberFormat { get; }

    public MetricPresentationSpec(MetricId metricId, string numberFormat)
    {
        MetricId = metricId ?? throw new ArgumentNullException(nameof(metricId));
        ArgumentException.ThrowIfNullOrWhiteSpace(numberFormat);
        NumberFormat = numberFormat;
    }
}

internal readonly record struct MetricPresentationResult(
    string ValueText,
    string StateText,
    string ConfidenceText,
    MetricState State);

internal static class MetricPresentation
{
    public static MetricPresentationResult Create(MetricPresentationSpec spec, MetricDescriptor? descriptor,
        MetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.MetricId != spec.MetricId)
            throw new InvalidDataException("Snapshot MetricId does not match presentation spec.");
        if (descriptor is not null && descriptor.Id != spec.MetricId)
            throw new InvalidDataException("Descriptor MetricId does not match presentation spec.");
        if (descriptor is not null && snapshot.Value is { } metricValue && metricValue.Kind != descriptor.ValueKind)
            throw new InvalidDataException("Snapshot value kind does not match metric descriptor.");

        var valueText = snapshot.State switch
        {
            MetricState.Fresh or MetricState.Stale => FormatValue(snapshot.Value!.Value, spec, descriptor?.Unit),
            MetricState.Unlimited => "Unlimited",
            _ => "—"
        };
        var stateText = snapshot.State == MetricState.Error && snapshot.ErrorCode is not null
            ? $"Error · {snapshot.ErrorCode}"
            : snapshot.State.ToString();

        return new MetricPresentationResult(valueText, stateText, snapshot.Confidence.ToString(), snapshot.State);
    }

    private static string FormatValue(MetricValue value, MetricPresentationSpec spec, string? unit)
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
        return unit is null ? text : $"{text} {unit}";
    }
}
