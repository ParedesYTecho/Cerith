#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cerith.Core;

public sealed class MetricBus : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _subscriptions = new();
    private readonly int _subscriberCapacity;
    private bool _disposed;

    public MetricBus(int subscriberCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberCapacity);
        _subscriberCapacity = subscriberCapacity;
    }

    public async IAsyncEnumerable<MetricSnapshot> Subscribe(
        MetricId metricId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        Subscription subscription;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscription = new Subscription(metricId, _subscriberCapacity);
            _subscriptions.Add(subscription);
        }

        using var registration = cancellationToken.Register(() => Unsubscribe(subscription));
        try
        {
            await foreach (var snapshot in subscription.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return snapshot;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Unsubscribe(subscription);
        }
    }

    public async ValueTask PublishAsync(
        MetricSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Subscription[] targets;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            targets = _subscriptions.Where(subscription => subscription.MetricId == snapshot.MetricId).ToArray();
        }

        foreach (var subscription in targets)
        {
            try
            {
                await subscription.Channel.Writer.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // A subscription may close after the target snapshot is taken.
            }
        }
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Channel.Writer.TryComplete();
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }

        subscription.Channel.Writer.TryComplete();
    }

    private sealed class Subscription(MetricId metricId, int capacity)
    {
        public MetricId MetricId { get; } = metricId;

        public Channel<MetricSnapshot> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<MetricSnapshot>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    }
}
