#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.Core.History;

namespace Cerith.TelemetryService;

/// <summary>
/// Records every snapshot published on the bus. The bus tap only enqueues; a bounded queue drops and counts on overflow,
/// so a slow disk can never slow or block publishers. A crash loses at most the unflushed batch.
/// </summary>
internal sealed class HistoryRecorder : IDisposable
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);

    private readonly HistoryStore _store;
    private readonly HistoryRetention _retention;
    private readonly TimeSpan _flushInterval;
    private readonly Channel<MetricSnapshot> _queue;
    private readonly IDisposable _tap;
    private long _dropped;
    private long _rejected;

    internal HistoryRecorder(MetricBus bus, HistoryStore store, HistoryRetention retention, TimeSpan flushInterval, int capacity)
    {
        _store = store;
        _retention = retention;
        _flushInterval = flushInterval;
        _queue = Channel.CreateBounded<MetricSnapshot>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _tap = bus.Tap(snapshot =>
        {
            if (!_queue.Writer.TryWrite(snapshot)) Interlocked.Increment(ref _dropped);
        });
    }

    /// <summary>Snapshots lost because the queue was full or a batch failed to write.</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Snapshots the store refused (non-finite, oversized, or a value kind that changed).</summary>
    internal long Rejected => Interlocked.Read(ref _rejected);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextMaintenance = DateTimeOffset.UtcNow;
        var reportedDropped = 0L;
        try
        {
            while (true)
            {
                if (DateTimeOffset.UtcNow >= nextMaintenance)
                {
                    TryMaintain();
                    nextMaintenance = DateTimeOffset.UtcNow + MaintenanceInterval;
                }

                await Task.Delay(_flushInterval, cancellationToken).ConfigureAwait(false);
                Flush();
                var dropped = Dropped;
                if (dropped != reportedDropped)
                {
                    Console.Error.WriteLine($"History: {dropped} snapshots dropped so far.");
                    reportedDropped = dropped;
                }
            }
        }
        finally
        {
            Flush();
        }
    }

    internal void Flush()
    {
        var batch = new List<MetricSnapshot>();
        while (_queue.Reader.TryRead(out var snapshot)) batch.Add(snapshot);
        if (batch.Count == 0) return;

        try
        {
            var result = _store.Append(batch);
            if (result.Rejected > 0) Interlocked.Add(ref _rejected, result.Rejected);
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException)
        {
            Interlocked.Add(ref _dropped, batch.Count);
            Console.Error.WriteLine("History: batch write failed; batch dropped.");
        }
    }

    private void TryMaintain()
    {
        try { _store.Maintain(DateTimeOffset.UtcNow, _retention); }
        catch (System.IO.IOException)
        {
            Console.Error.WriteLine("History: maintenance failed; will retry.");
        }
    }

    public void Dispose()
    {
        _tap.Dispose();
        _queue.Writer.TryComplete();
        _store.Dispose();
    }
}
