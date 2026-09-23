#nullable enable

using System;

namespace Cerith.Core;

public sealed record MetricId
{
    public string Value { get; }

    public MetricId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Metric ID cannot be empty or whitespace.", nameof(value));
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException("Metric ID cannot start or end with whitespace.", nameof(value));
        }

        Value = value;
    }

    public override string ToString() => Value;
}
