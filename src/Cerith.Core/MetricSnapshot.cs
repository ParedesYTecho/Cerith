#nullable enable

using System;

namespace Cerith.Core;

public enum MetricState
{
    Fresh,
    Stale,
    Unavailable,
    Stopped,
    Error,
    Unlimited
}

public enum MetricConfidence
{
    Unknown,
    Measured,
    Estimated
}

public sealed class MetricSnapshot
{
    private const int MaxErrorCodeLength = 64;

    public MetricId MetricId { get; }

    public MetricValue? Value { get; }

    public DateTimeOffset ObservedAt { get; }

    public MetricState State { get; }

    public MetricConfidence Confidence { get; }

    public string? Source { get; }

    public string? ErrorCode { get; }

    public MetricSnapshot(
        MetricId metricId,
        MetricValue? value,
        DateTimeOffset observedAt,
        MetricState state,
        MetricConfidence confidence,
        string? source = null,
        string? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        if (!Enum.IsDefined(typeof(MetricState), state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown metric state.");
        }

        if (!Enum.IsDefined(typeof(MetricConfidence), confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Unknown metric confidence.");
        }

        if (source is not null && string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Source cannot be whitespace when provided.", nameof(source));
        }

        if (errorCode is not null && !IsSafeErrorCode(errorCode))
        {
            throw new ArgumentException("Error code must be a short ASCII identifier.", nameof(errorCode));
        }

        if (value is MetricValue presentValue && presentValue.Kind == MetricValueKind.None)
        {
            throw new ArgumentException("A present value must have a concrete kind.", nameof(value));
        }

        ValidateValueAndConfidence(state, value, confidence);

        MetricId = metricId;
        Value = value;
        ObservedAt = observedAt;
        State = state;
        Confidence = confidence;
        Source = source;
        ErrorCode = errorCode;
    }

    private static void ValidateValueAndConfidence(
        MetricState state,
        MetricValue? value,
        MetricConfidence confidence)
    {
        var hasValue = value.HasValue;

        switch (state)
        {
            case MetricState.Fresh:
            case MetricState.Stale:
                if (!hasValue)
                {
                    throw new ArgumentException($"{state} metrics require a value.", nameof(value));
                }

                break;
            case MetricState.Unavailable:
            case MetricState.Stopped:
            case MetricState.Error:
            case MetricState.Unlimited:
                if (hasValue)
                {
                    throw new ArgumentException($"{state} metrics cannot contain a value.", nameof(value));
                }

                break;
        }

        if (!hasValue && confidence != MetricConfidence.Unknown)
        {
            throw new ArgumentException("Metrics without a value must have unknown confidence.", nameof(confidence));
        }
    }

    private static bool IsSafeErrorCode(string value)
    {
        if (value.Length is 0 or > MaxErrorCodeLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }
}
