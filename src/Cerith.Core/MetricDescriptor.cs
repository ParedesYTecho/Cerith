#nullable enable

using System;

namespace Cerith.Core;

public sealed class MetricDescriptor
{
    public MetricId Id { get; }

    public string DisplayName { get; }

    public string? Description { get; }

    public string SemanticType { get; }

    public string? Unit { get; }

    public MetricValueKind ValueKind { get; }

    public MetricDescriptor(
        MetricId id,
        string displayName,
        string semanticType,
        MetricValueKind valueKind,
        string? description = null,
        string? unit = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticType);

        if (description is not null && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description cannot be whitespace when provided.", nameof(description));
        }

        if (unit is not null && string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException("Unit cannot be whitespace when provided.", nameof(unit));
        }

        if (!Enum.IsDefined(typeof(MetricValueKind), valueKind) || valueKind == MetricValueKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(valueKind), valueKind, "A concrete metric value kind is required.");
        }

        Id = id;
        DisplayName = displayName;
        Description = description;
        SemanticType = semanticType;
        Unit = unit;
        ValueKind = valueKind;
    }
}
