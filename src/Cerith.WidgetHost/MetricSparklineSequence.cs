#nullable enable

using System;
using System.Collections.Generic;
using Cerith.Core;

namespace Cerith.WidgetHost;

// Private to the visual component; the transport intentionally does not retain history.
internal sealed class MetricSparklineSequence(MetricRangeSpec spec)
{
    private const int MaxSamples = 60;
    internal readonly record struct Sample(DateTimeOffset Time, double Fraction);

    private readonly List<Sample> _samples = [];
    private string? _source;
    private MetricConfidence _confidence;
    private (string? Unit, string? Semantic, MetricValueKind? Kind) _metadata;
    private bool _stale;

    internal IReadOnlyList<Sample> Samples => _samples;
    internal TimeSpan Span => _samples.Count < 2 ? TimeSpan.Zero : _samples[^1].Time - _samples[0].Time;
    internal bool IsStale => _stale;

    internal bool Apply(MetricDescriptor? descriptor, MetricSnapshot? snapshot)
    {
        if (snapshot?.State is not (MetricState.Fresh or MetricState.Stale))
        {
            return Clear();
        }

        var range = MetricRangePresentation.Create(spec, descriptor, snapshot);
        if (range.FillFraction is not { } fraction || !double.IsFinite(fraction))
        {
            return Clear();
        }

        var metadata = (descriptor?.Unit, descriptor?.SemanticType, descriptor?.ValueKind);
        var changed = false;
        if (_samples.Count > 0 &&
            (_source != snapshot.Source || _confidence != snapshot.Confidence || _metadata != metadata ||
             snapshot.ObservedAt.UtcTicks < _samples[^1].Time.UtcTicks))
            changed = Clear();

        if (snapshot.State == MetricState.Stale)
        {
            if (_samples.Count > 0) _stale = true;
            return changed;
        }

        if (_stale) changed = Clear();

        _source = snapshot.Source;
        _confidence = snapshot.Confidence;
        _metadata = metadata;
        var sample = new Sample(snapshot.ObservedAt, fraction);
        if (_samples.Count > 0 && _samples[^1].Time.UtcTicks == snapshot.ObservedAt.UtcTicks)
        {
            if (_samples[^1].Fraction == fraction) return changed;
            _samples[^1] = sample;
        }
        else
        {
            if (_samples.Count == MaxSamples) _samples.RemoveAt(0);
            _samples.Add(sample);
        }
        return true;
    }

    private bool Clear()
    {
        var changed = _samples.Count > 0;
        _samples.Clear();
        _source = null;
        _metadata = default;
        _stale = false;
        return changed;
    }
}
