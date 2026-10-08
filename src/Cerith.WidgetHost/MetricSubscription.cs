#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.WidgetHost;

internal enum MetricConnectionState { Connecting, Waiting, Connected, Unavailable }

// State and consumer registration stay on the caller's context (the WinUI thread in production).
internal sealed class MetricSubscription(MetricId metricId, MetricConnectionAttempt connectionAttempt, string pipeName)
{
    private int _running;

    public MetricId MetricId { get; } = metricId ?? throw new ArgumentNullException(nameof(metricId));
    public MetricDescriptor? Descriptor { get; private set; }
    public MetricSnapshot? Snapshot { get; private set; }
    public MetricConnectionState ConnectionState { get; private set; } = MetricConnectionState.Connecting;
    public event Action<MetricSubscription>? Updated;

    public void DetachConsumers() => Updated = null;

    public static MetricSubscription[] ForDistinctMetrics(IEnumerable<MetricId> ids,
        MetricConnectionAttempt connectionAttempt, string pipeName) =>
        ids.Distinct().Select(id => new MetricSubscription(id, connectionAttempt, pipeName)).ToArray();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("Metric subscription is already running.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connected = false;
                try
                {
                    await using var client = new NamedPipeMetricClient(pipeName);
                    await connectionAttempt.ConnectAsync(async token =>
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await client.ConnectAsync(MetricId, timeout.Token);
                    }, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    connected = true;
                    Descriptor = client.Descriptor;
                    Snapshot = null;
                    ConnectionState = MetricConnectionState.Waiting;
                    Notify();

                    await foreach (var snapshot in client.ReadSnapshotsAsync(cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Descriptor = client.Descriptor;
                        Snapshot = snapshot;
                        ConnectionState = MetricConnectionState.Connected;
                        Notify();
                    }
                    connectionAttempt.MarkUnavailable();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    if (connected) connectionAttempt.MarkUnavailable();
                    Debug.WriteLine($"Metric subscription interrupted for {MetricId}; reconnecting.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                Snapshot = null;
                ConnectionState = MetricConnectionState.Unavailable;
                Notify();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            Snapshot = null;
            Descriptor = null;
            ConnectionState = MetricConnectionState.Unavailable;
            Volatile.Write(ref _running, 0);
        }
    }

    private void Notify()
    {
        if (Updated is not { } updated) return;
        foreach (Action<MetricSubscription> consumer in updated.GetInvocationList())
        {
            try { consumer(this); }
            catch (Exception) { Debug.WriteLine($"Metric component update failed for {MetricId}."); }
        }
    }
}

internal sealed class MetricConnectionAttempt
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _attempt;
    private bool _available;
    private long _generation;

    public async Task ConnectAsync(Func<CancellationToken, Task> connect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<bool>? attempt;
        bool owner = false;
        long generation;
        lock (_gate)
        {
            generation = _generation;
            attempt = _available ? null : _attempt;
            if (!_available && attempt is null)
            {
                attempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _attempt = attempt;
                owner = true;
            }
        }

        if (attempt is not null && !owner)
        {
            if (!await attempt.Task.WaitAsync(cancellationToken))
                throw new IOException("TelemetryService connection attempt failed.");
            lock (_gate)
            {
                if (!_available || generation != _generation)
                    throw new IOException("TelemetryService availability changed.");
            }
        }

        bool succeeded = false;
        bool callerCancelled = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await connect(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            succeeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
            throw;
        }
        finally
        {
            if (owner)
            {
                bool release;
                lock (_gate)
                {
                    release = succeeded && generation == _generation;
                    if (ReferenceEquals(_attempt, attempt))
                    {
                        _attempt = null;
                        _available = release;
                    }
                }
                attempt!.TrySetResult(release);
            }
            else if (!succeeded && !callerCancelled)
            {
                lock (_gate)
                {
                    if (generation == _generation)
                    {
                        _available = false;
                        _generation++;
                    }
                }
            }
        }
    }

    public void MarkUnavailable()
    {
        lock (_gate)
        {
            _available = false;
            _generation++;
        }
    }
}
