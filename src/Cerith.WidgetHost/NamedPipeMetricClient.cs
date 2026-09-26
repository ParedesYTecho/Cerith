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
            await MetricIpcProtocol.ReadAcceptedAsync(pipe, cancellationToken).ConfigureAwait(false);
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
            MetricSnapshot snapshot;
            try { snapshot = await MetricIpcProtocol.ReadSnapshotAsync(pipe, cancellationToken).ConfigureAwait(false); }
            catch (EndOfStreamException) { yield break; }
            yield return snapshot;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        _pipe = null;
    }
}
