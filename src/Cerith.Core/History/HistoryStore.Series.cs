#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Cerith.Core.History;

public enum HistoryResolution
{
    Raw,
    Minute,
    Hour
}

public sealed record HistorySeriesPoint(DateTimeOffset At, double Min, double Max, double Average);

/// <summary>Chart-ready numeric series. Points exist only where Fresh values were recorded; a missing stretch is a gap, never zero.</summary>
public sealed record HistorySeries(HistoryResolution Resolution, IReadOnlyList<HistorySeriesPoint> Points);

public sealed partial class HistoryStore
{
    private static readonly TimeSpan RawSpanLimit = TimeSpan.FromHours(2), MinuteSpanLimit = TimeSpan.FromDays(3);

    /// <summary>Every metric the store has recorded, by name.</summary>
    public IReadOnlyList<MetricId> ReadMetrics()
    {
        var ids = new List<MetricId>();
        using var statement = _db.Prepare("SELECT name FROM metric ORDER BY name");
        while (statement.Step()) ids.Add(new MetricId(statement.Text(0)!));
        return ids;
    }

    /// <summary>
    /// Numeric series for the range. Resolution follows the span (raw up to 2 h, minute up to 3 days, otherwise hour) so the point count
    /// stays bounded. Rollups lag by five minutes, so the not-yet-rolled tail is aggregated from the finer tier.
    /// </summary>
    public HistorySeries ReadSeries(MetricId id, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(id);
        var span = to - from;
        var resolution = span <= RawSpanLimit ? HistoryResolution.Raw : span <= MinuteSpanLimit ? HistoryResolution.Minute : HistoryResolution.Hour;
        if (!TryLookup(id, out var metric) || metric.Kind is not (MetricValueKind.Double or MetricValueKind.Int64))
            return new HistorySeries(resolution, []);

        if (resolution == HistoryResolution.Raw)
        {
            var raw = ReadRaw(id, from, to)
                .Where(p => p.State == MetricState.Fresh && p.Value.HasValue)
                .Select(p => new HistorySeriesPoint(p.At, Number(p.Value!.Value), Number(p.Value!.Value), Number(p.Value!.Value)))
                .ToArray();
            return new HistorySeries(resolution, raw);
        }

        var width = resolution == HistoryResolution.Minute ? MinuteMs : HourMs;
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();
        var minuteUntil = Meta("minute_until") ?? fromMs;
        var hourUntil = Meta("hour_until") ?? fromMs;
        var buckets = new SortedDictionary<long, (long N, double Min, double Max, double Sum)>();

        if (resolution == HistoryResolution.Hour)
        {
            AddRollups(buckets, metric.Id, HourMs, HourMs, fromMs, Math.Min(toMs, hourUntil));
            AddRollups(buckets, metric.Id, MinuteMs, HourMs, Math.Max(fromMs, hourUntil), Math.Min(toMs, minuteUntil));
        }
        else
        {
            AddRollups(buckets, metric.Id, MinuteMs, MinuteMs, fromMs, Math.Min(toMs, minuteUntil));
        }

        foreach (var point in ReadRaw(id, DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(fromMs, minuteUntil)), to))
        {
            if (point.State != MetricState.Fresh || point.Value is not { } value) continue;
            Add(buckets, point.At.ToUnixTimeMilliseconds() / width * width, 1, Number(value), Number(value), Number(value));
        }

        var points = buckets.Select(b => new HistorySeriesPoint(DateTimeOffset.FromUnixTimeMilliseconds(b.Key), b.Value.Min, b.Value.Max, b.Value.Sum / b.Value.N)).ToArray();
        return new HistorySeries(resolution, points);
    }

    private void AddRollups(SortedDictionary<long, (long N, double Min, double Max, double Sum)> buckets, long metric, long sourceWidth, long targetWidth, long from, long to)
    {
        if (from >= to) return;
        using var statement = _db.Prepare("SELECT start, n, min, max, sum FROM rollup WHERE bucket=?1 AND metric=?2 AND start>=?3 AND start<?4");
        statement.Bind(1, sourceWidth);
        statement.Bind(2, metric);
        statement.Bind(3, from);
        statement.Bind(4, to);
        while (statement.Step())
            Add(buckets, statement.Int64(0) / targetWidth * targetWidth, statement.Int64(1), statement.Double(2), statement.Double(3), statement.Double(4));
    }

    private static void Add(SortedDictionary<long, (long N, double Min, double Max, double Sum)> buckets, long key, long n, double min, double max, double sum)
    {
        buckets[key] = buckets.TryGetValue(key, out var current)
            ? (current.N + n, Math.Min(current.Min, min), Math.Max(current.Max, max), current.Sum + sum)
            : (n, min, max, sum);
    }

    private static double Number(MetricValue value) => value.Kind == MetricValueKind.Double ? value.AsDouble() : value.AsInt64();
}
