#nullable enable

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class NamedPipeMetricTests
{
    [TestMethod]
    public async Task SyntheticSnapshotTraversesPipeAndReconnectsWithoutReplay()
    {
        using var bus = new MetricBus(1);
        var provider = new SyntheticProvider(new MetricRegistry(), bus);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus).RunAsync(stop.Token);

        await provider.PublishNextAsync(stop.Token);
        await using (var first = new NamedPipeMetricClient(name))
        {
            await first.ConnectAsync(provider.Descriptor.Id, stop.Token);
            await using var reader = first.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await provider.PublishNextAsync(stop.Token);
            Assert.IsTrue(await pending.WaitAsync(stop.Token));
            var snapshot = reader.Current;
            Assert.AreEqual(provider.Descriptor.Id, snapshot.MetricId);
            Assert.AreEqual(1L, snapshot.Value!.Value.AsInt64());
            Assert.AreEqual(MetricValueKind.Int64, snapshot.Value.Value.Kind);
            Assert.AreEqual(MetricState.Fresh, snapshot.State);
            Assert.AreEqual(MetricConfidence.Unknown, snapshot.Confidence);
            Assert.AreEqual("synthetic", snapshot.Source);
            Assert.IsNull(snapshot.ErrorCode);
        }

        await using (var second = new NamedPipeMetricClient(name))
        {
            await second.ConnectAsync(provider.Descriptor.Id, stop.Token);
            await using var reader = second.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await provider.PublishNextAsync(stop.Token);
            Assert.IsTrue(await pending.WaitAsync(stop.Token));
            Assert.AreEqual(2L, reader.Current.Value!.Value.AsInt64());
        }

        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ExactIdAndDisconnectRemoveSubscription()
    {
        using var bus = new MetricBus(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus).RunAsync(stop.Token);
        var id = new MetricId("Example.Id");
        await using (var client = new NamedPipeMetricClient(name))
        {
            await client.ConnectAsync(id, stop.Token);
            await bus.PublishAsync(Snapshot(new MetricId("example.id")), stop.Token);
            await using var reader = client.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await bus.PublishAsync(Snapshot(id), stop.Token);
            Assert.IsTrue(await pending.WaitAsync(stop.Token));
            Assert.AreEqual(id, reader.Current.MetricId);
        }
        await using (var next = new NamedPipeMetricClient(name))
        {
            await next.ConnectAsync(new MetricId("another.id"), stop.Token);
            await bus.PublishAsync(Snapshot(id), stop.Token);
            await bus.PublishAsync(Snapshot(id), stop.Token);
        }
        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ServerAndClientCancellationTerminate()
    {
        using var bus = new MetricBus(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus).RunAsync(stop.Token);
        await using (var client = new NamedPipeMetricClient(name))
        {
            await client.ConnectAsync(new MetricId("idle"), stop.Token);
            using var readStop = new CancellationTokenSource();
            await using var reader = client.ReadSnapshotsAsync(readStop.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            readStop.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await pending);
        }
        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        using var waitingStop = new CancellationTokenSource();
        var waiting = new NamedPipeMetricServer(Name(), bus).RunAsync(waitingStop.Token);
        waitingStop.Cancel();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task EveryCurrentValueKindRoundTrips()
    {
        MetricValue[] values = [MetricValue.From(double.Epsilon), MetricValue.From(long.MaxValue),
            MetricValue.From(decimal.MaxValue), MetricValue.From(true), MetricValue.From("\u00e9\n"),
            MetricValue.From(new DateTimeOffset(2026, 9, 26, 12, 34, 56, TimeSpan.FromHours(2))),
            MetricValue.From(TimeSpan.FromTicks(-123456789))];
        foreach (var value in values)
        {
            var original = new MetricSnapshot(new MetricId("value.Test"), value,
                new DateTimeOffset(2026, 9, 26, 12, 34, 56, TimeSpan.FromHours(2)),
                MetricState.Stale, MetricConfidence.Estimated, "source", "E_1");
            using var stream = new MemoryStream();
            await MetricIpcProtocol.WriteSnapshotAsync(stream, original);
            stream.Position = 0;
            var result = await MetricIpcProtocol.ReadSnapshotAsync(stream);
            Assert.AreEqual(original.MetricId, result.MetricId);
            Assert.AreEqual(original.Value, result.Value);
            Assert.IsTrue(original.ObservedAt.EqualsExact(result.ObservedAt));
            Assert.AreEqual(original.State, result.State);
            Assert.AreEqual(original.Confidence, result.Confidence);
            Assert.AreEqual(original.Source, result.Source);
            Assert.AreEqual(original.ErrorCode, result.ErrorCode);
        }
        using var missing = new MemoryStream();
        await MetricIpcProtocol.WriteSnapshotAsync(missing, new MetricSnapshot(new MetricId("no.value"), null,
            DateTimeOffset.UtcNow, MetricState.Unlimited, MetricConfidence.Unknown));
        missing.Position = 0;
        var unlimited = await MetricIpcProtocol.ReadSnapshotAsync(missing);
        Assert.IsNull(unlimited.Value);
        Assert.AreEqual(MetricState.Unlimited, unlimited.State);
    }

    [TestMethod]
    public async Task InvalidFramesAreRejectedBeforeUnboundedReads()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadAcceptedAsync(FrameLength(-1)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadAcceptedAsync(FrameLength(MetricIpcProtocol.MaxFrameSize + 1)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadAcceptedAsync(FrameLength(8)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadAcceptedAsync(new MemoryStream([0x00, 0x01])));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadAcceptedAsync(Frame("garbage")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadAcceptedAsync(Frame("{\"Version\":2,\"Kind\":\"accepted\"}")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadAcceptedAsync(Frame("{\"Version\":1,\"Kind\":\"snapshot\"}")));
    }

    [TestMethod]
    public async Task PipeIsLocalCurrentUserAndRejectsRemoteClients()
    {
        using var bus = new MetricBus(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus).RunAsync(stop.Token);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(stop.Token);
        Assert.IsTrue(GetNamedPipeInfo(client.SafePipeHandle, out var flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        Assert.AreEqual(0x8u, flags & 0x8u);
        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        var controlName = Name();
        using var controlServer = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var controlClient = new NamedPipeClientStream(".", controlName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await controlClient.ConnectAsync();
        Assert.IsTrue(GetNamedPipeInfo(controlClient.SafePipeHandle, out var controlFlags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        Assert.AreEqual(0u, controlFlags & 0x8u);
    }

    [TestMethod]
    public async Task ClientConnectCanBeCancelledWhileWaitingForServer()
    {
        await using var client = new NamedPipeMetricClient(Name());
        using var stop = new CancellationTokenSource();
        var pending = client.ConnectAsync(new MetricId("idle"), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    private static MetricSnapshot Snapshot(MetricId id) => new(id, MetricValue.From(1L),
        DateTimeOffset.UtcNow, MetricState.Fresh, MetricConfidence.Unknown);

    private static string Name() => $"cerith-test-{Guid.NewGuid():N}";

    private static MemoryStream FrameLength(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        return new MemoryStream(bytes);
    }

    private static MemoryStream Frame(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        stream.Write(header);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeInfo(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint flags,
        IntPtr outBufferSize, IntPtr inBufferSize, IntPtr maxInstances);
}
