#nullable enable

using System;

namespace Cerith.Core;

public enum MetricValueKind
{
    None,
    Double,
    Int64,
    Decimal,
    Boolean,
    String,
    DateTimeOffset,
    TimeSpan
}

public readonly struct MetricValue : IEquatable<MetricValue>
{
    private readonly double _doubleValue;
    private readonly long _int64Value;
    private readonly decimal _decimalValue;
    private readonly bool _booleanValue;
    private readonly string? _stringValue;
    private readonly DateTimeOffset _dateTimeOffsetValue;
    private readonly TimeSpan _timeSpanValue;

    public MetricValueKind Kind { get; }

    private MetricValue(MetricValueKind kind)
    {
        Kind = kind;
        _doubleValue = default;
        _int64Value = default;
        _decimalValue = default;
        _booleanValue = default;
        _stringValue = default;
        _dateTimeOffsetValue = default;
        _timeSpanValue = default;
    }

    private MetricValue(double value) : this(MetricValueKind.Double) => _doubleValue = value;

    private MetricValue(long value) : this(MetricValueKind.Int64) => _int64Value = value;

    private MetricValue(decimal value) : this(MetricValueKind.Decimal) => _decimalValue = value;

    private MetricValue(bool value) : this(MetricValueKind.Boolean) => _booleanValue = value;

    private MetricValue(string value) : this(MetricValueKind.String) => _stringValue = value;

    private MetricValue(DateTimeOffset value) : this(MetricValueKind.DateTimeOffset) => _dateTimeOffsetValue = value;

    private MetricValue(TimeSpan value) : this(MetricValueKind.TimeSpan) => _timeSpanValue = value;

    public static MetricValue From(double value) => new(value);

    public static MetricValue From(long value) => new(value);

    public static MetricValue From(decimal value) => new(value);

    public static MetricValue From(bool value) => new(value);

    public static MetricValue From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new MetricValue(value);
    }

    public static MetricValue From(DateTimeOffset value) => new(value);

    public static MetricValue From(TimeSpan value) => new(value);

    public double AsDouble()
    {
        EnsureKind(MetricValueKind.Double);
        return _doubleValue;
    }

    public long AsInt64()
    {
        EnsureKind(MetricValueKind.Int64);
        return _int64Value;
    }

    public decimal AsDecimal()
    {
        EnsureKind(MetricValueKind.Decimal);
        return _decimalValue;
    }

    public bool AsBoolean()
    {
        EnsureKind(MetricValueKind.Boolean);
        return _booleanValue;
    }

    public string AsString()
    {
        EnsureKind(MetricValueKind.String);
        return _stringValue!;
    }

    public DateTimeOffset AsDateTimeOffset()
    {
        EnsureKind(MetricValueKind.DateTimeOffset);
        return _dateTimeOffsetValue;
    }

    public TimeSpan AsTimeSpan()
    {
        EnsureKind(MetricValueKind.TimeSpan);
        return _timeSpanValue;
    }

    public bool Equals(MetricValue other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            MetricValueKind.None => true,
            MetricValueKind.Double => _doubleValue.Equals(other._doubleValue),
            MetricValueKind.Int64 => _int64Value == other._int64Value,
            MetricValueKind.Decimal => _decimalValue == other._decimalValue,
            MetricValueKind.Boolean => _booleanValue == other._booleanValue,
            MetricValueKind.String => StringComparer.Ordinal.Equals(_stringValue, other._stringValue),
            MetricValueKind.DateTimeOffset => _dateTimeOffsetValue.EqualsExact(other._dateTimeOffsetValue),
            MetricValueKind.TimeSpan => _timeSpanValue == other._timeSpanValue,
            _ => false
        };
    }

    public override bool Equals(object? obj) => obj is MetricValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        MetricValueKind.None => HashCode.Combine(Kind),
        MetricValueKind.Double => HashCode.Combine(Kind, _doubleValue),
        MetricValueKind.Int64 => HashCode.Combine(Kind, _int64Value),
        MetricValueKind.Decimal => HashCode.Combine(Kind, _decimalValue),
        MetricValueKind.Boolean => HashCode.Combine(Kind, _booleanValue),
        MetricValueKind.String => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(_stringValue!)),
        MetricValueKind.DateTimeOffset => HashCode.Combine(Kind, _dateTimeOffsetValue),
        MetricValueKind.TimeSpan => HashCode.Combine(Kind, _timeSpanValue),
        _ => HashCode.Combine(Kind)
    };

    private void EnsureKind(MetricValueKind expected)
    {
        if (Kind != expected)
        {
            throw new InvalidOperationException($"Expected {expected}, but value kind is {Kind}.");
        }
    }
}
