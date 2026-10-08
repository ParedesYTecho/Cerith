#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.WidgetHost;

public sealed class NamedPipeMetricClient(string pipeName) : IAsyncDisposable
{
    private readonly string _pipeName = pipeName;
    private NamedPipeClientStream? _pipe;
    private MetricId? _metricId;

    public MetricDescriptor? Descriptor { get; private set; }

    public async Task ConnectAsync(MetricId metricId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        if (_pipe is not null) throw new InvalidOperationException("Client is already connected.");
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await MetricIpcProtocol.WriteSubscribeAsync(pipe, metricId, cancellationToken).ConfigureAwait(false);
            var descriptor = await MetricIpcProtocol.ReadAcceptedAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (descriptor is not null && descriptor.Id != metricId)
                throw new InvalidDataException("Accepted descriptor does not match the requested MetricId.");
            _metricId = metricId;
            Descriptor = descriptor;
            _pipe = pipe;
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async IAsyncEnumerable<MetricSnapshot> ReadSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Client is not connected.");
        while (true)
        {
            (MetricSnapshot Snapshot, MetricDescriptor? Descriptor) frame;
            try { frame = await MetricIpcProtocol.ReadSnapshotFrameAsync(pipe, cancellationToken).ConfigureAwait(false); }
            catch (EndOfStreamException) { yield break; }
            if (frame.Snapshot.MetricId != _metricId)
                throw new InvalidDataException("Snapshot MetricId does not match the subscription.");
            if (frame.Descriptor is not null)
            {
                if (Descriptor is not null)
                    throw new InvalidDataException("Metric descriptor was sent more than once.");
                Descriptor = frame.Descriptor;
            }
            if (Descriptor is not null && frame.Snapshot.Value is { } value && value.Kind != Descriptor.ValueKind)
                throw new InvalidDataException("Snapshot value kind does not match metric descriptor.");
            yield return frame.Snapshot;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        _pipe = null;
        _metricId = null;
        Descriptor = null;
    }
}
