#nullable enable

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
    public async Task ConcurrentClientsKeepExactSubscriptionsAndShutDownTogether()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);
        var idA = new MetricId("metric.a");
        var idB = new MetricId("metric.b");
        await using var a = new NamedPipeMetricClient(name);
        await using var b = new NamedPipeMetricClient(name);
        await a.ConnectAsync(idA, stop.Token);
        await b.ConnectAsync(idB, stop.Token);
        await using (var invalid = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await invalid.ConnectAsync(stop.Token);
            using var frame = Frame("{\"Version\":2,\"Kind\":\"subscribe\",\"MetricId\":\"x\"}");
            await invalid.WriteAsync(frame.ToArray(), stop.Token);
        }
        await using var readerA = a.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
        await using var readerB = b.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
        var nextA = readerA.MoveNextAsync().AsTask();
        var nextB = readerB.MoveNextAsync().AsTask();

        await bus.PublishAsync(Snapshot(idA), stop.Token);
        Assert.IsTrue(await nextA.WaitAsync(stop.Token));
        Assert.AreEqual(idA, readerA.Current.MetricId);
        await Task.Delay(100, stop.Token);
        Assert.IsFalse(nextB.IsCompleted);

        nextA = readerA.MoveNextAsync().AsTask();
        await bus.PublishAsync(Snapshot(idB), stop.Token);
        Assert.IsTrue(await nextB.WaitAsync(stop.Token));
        Assert.AreEqual(idB, readerB.Current.MetricId);
        await Task.Delay(100, stop.Token);
        Assert.IsFalse(nextA.IsCompleted);

        await a.DisposeAsync();
        try { await nextA.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { }
        catch (OperationCanceledException) { }

        nextB = readerB.MoveNextAsync().AsTask();
        await bus.PublishAsync(Snapshot(idB), stop.Token);
        Assert.IsTrue(await nextB.WaitAsync(stop.Token));
        Assert.AreEqual(idB, readerB.Current.MetricId);

        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        nextB = readerB.MoveNextAsync().AsTask();
        try { Assert.IsFalse(await nextB.WaitAsync(TimeSpan.FromSeconds(5))); }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task SyntheticSnapshotTraversesPipeAndReconnectsWithoutReplay()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        var provider = new SyntheticProvider(registry, bus);
        Assert.IsTrue(provider.TryRegister());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);

        await provider.PublishNextAsync(stop.Token);
        await using (var first = new NamedPipeMetricClient(name))
        {
            await first.ConnectAsync(provider.Descriptor.Id, stop.Token);
            Assert.AreEqual(provider.Descriptor.DisplayName, first.Descriptor?.DisplayName);
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
    public async Task SubscriptionSurvivesLateDescriptorRegistrationAndBindsOnValuelessSnapshot()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var id = new MetricId("late.metric");
        var pipeName = Name();
        var server = new NamedPipeMetricServer(pipeName, bus, registry);
        var serverTask = server.RunAsync(stop.Token);
        await using var client = new NamedPipeMetricClient(pipeName);
        try
        {
            await client.ConnectAsync(id, stop.Token);
            Assert.IsNull(client.Descriptor);

            var descriptor = new MetricDescriptor(id, "Late metric", "count", MetricValueKind.Int64);
            Assert.IsTrue(registry.TryRegister(descriptor));
            await using var reader = client.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await bus.PublishAsync(new MetricSnapshot(id, null, DateTimeOffset.UtcNow, MetricState.Unavailable,
                MetricConfidence.Unknown), stop.Token);
            Assert.IsTrue(await pending.WaitAsync(stop.Token));
            Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
            Assert.IsNull(reader.Current.Value);
            Assert.AreEqual(id, client.Descriptor?.Id);
            Assert.AreEqual("Late metric", client.Descriptor?.DisplayName);
            Assert.IsNull(client.Descriptor?.Unit);
            Assert.IsNull(client.Descriptor?.Description);

            pending = reader.MoveNextAsync().AsTask();
            await bus.PublishAsync(new MetricSnapshot(id, MetricValue.From(4L), DateTimeOffset.UtcNow,
                MetricState.Fresh, MetricConfidence.Measured), stop.Token);
            Assert.IsTrue(await pending.WaitAsync(stop.Token));
            Assert.AreEqual(4L, reader.Current.Value!.Value.AsInt64());
            Assert.AreEqual(id, client.Descriptor?.Id);
        }
        finally
        {
            stop.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task ReconnectToDescriptorFreeServerUsesFallbackUntilLateMetadataArrives()
    {
        var id = new MetricId("reconnect.metric");
        var pipeName = Name();
        var previousDescriptor = new MetricDescriptor(id, "Previous metric", "percentage",
            MetricValueKind.Double, unit: "%");
        using (var firstBus = new MetricBus(1))
        using (var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var firstRegistry = new MetricRegistry();
            Assert.IsTrue(firstRegistry.TryRegister(previousDescriptor));
            var firstServer = new NamedPipeMetricServer(pipeName, firstBus, firstRegistry).RunAsync(firstStop.Token);
            await using (var first = new NamedPipeMetricClient(pipeName))
            {
                await first.ConnectAsync(id, firstStop.Token);
                Assert.AreEqual(previousDescriptor.DisplayName, first.Descriptor?.DisplayName);
            }
            firstStop.Cancel();
            await firstServer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using var bus = new MetricBus(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var registry = new MetricRegistry();
        var server = new NamedPipeMetricServer(pipeName, bus, registry).RunAsync(stop.Token);
        try
        {
            await using var client = new NamedPipeMetricClient(pipeName);
            await client.ConnectAsync(id, stop.Token);
            Assert.IsNull(client.Descriptor);

            var spec = new MetricPresentationSpec(id, "0");
            var fallback = MetricPresentation.Create(spec, client.Descriptor,
                new MetricSnapshot(id, MetricValue.From(42L), DateTimeOffset.UtcNow,
                    MetricState.Fresh, MetricConfidence.Measured));
            Assert.AreEqual("42", fallback.ValueText);

            var lateDescriptor = new MetricDescriptor(id, "Current metric", "count", MetricValueKind.Int64,
                unit: "items");
            Assert.IsTrue(registry.TryRegister(lateDescriptor));
            await using var reader = client.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await bus.PublishAsync(new MetricSnapshot(id, null, DateTimeOffset.UtcNow, MetricState.Unavailable,
                MetricConfidence.Unknown), stop.Token);
            Assert.IsTrue(await pending.WaitAsync(stop.Token));
            Assert.IsNull(reader.Current.Value);
            Assert.AreEqual(lateDescriptor.DisplayName, client.Descriptor?.DisplayName);

            pending = reader.MoveNextAsync().AsTask();
            await bus.PublishAsync(new MetricSnapshot(id, MetricValue.From(42L), DateTimeOffset.UtcNow,
                MetricState.Fresh, MetricConfidence.Measured), stop.Token);
            Assert.IsTrue(await pending.WaitAsync(stop.Token));
            var rebound = MetricPresentation.Create(spec, client.Descriptor, reader.Current);
            Assert.AreEqual("42 items", rebound.ValueText);
        }
        finally
        {
            stop.Cancel();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task ClientRejectsDescriptorForWrongIdInAcceptedFrame()
    {
        var id = new MetricId("requested.metric");
        var descriptor = new MetricDescriptor(new MetricId("other.metric"), "Other", "count",
            MetricValueKind.Int64);
        var name = Name();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(stop.Token);
            Assert.AreEqual(id, await MetricIpcProtocol.ReadSubscribeAsync(server, stop.Token));
            await MetricIpcProtocol.WriteAcceptedAsync(server, descriptor, stop.Token);
        }, stop.Token);
        await using var client = new NamedPipeMetricClient(name);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.ConnectAsync(id, stop.Token));
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ClientRejectsDescriptorRepeatedAfterAcceptance()
    {
        var id = new MetricId("repeated.metric");
        var descriptor = new MetricDescriptor(id, "Repeated", "count", MetricValueKind.Int64);
        var name = Name();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(stop.Token);
            Assert.AreEqual(id, await MetricIpcProtocol.ReadSubscribeAsync(server, stop.Token));
            await MetricIpcProtocol.WriteAcceptedAsync(server, descriptor, stop.Token);
            await MetricIpcProtocol.WriteSnapshotAsync(server,
                new MetricSnapshot(id, MetricValue.From(1L), DateTimeOffset.UtcNow,
                    MetricState.Fresh, MetricConfidence.Measured), descriptor, stop.Token);
        }, stop.Token);
        await using var client = new NamedPipeMetricClient(name);
        await client.ConnectAsync(id, stop.Token);
        await using var reader = client.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task QuietSubscriptionCanBeCanceledAfterFormerReceiveDeadline()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var readStop = new CancellationTokenSource();
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);
        await using var client = new NamedPipeMetricClient(name);
        await client.ConnectAsync(new MetricId("quiet"), stop.Token);
        await using var reader = client.ReadSnapshotsAsync(readStop.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(3500), stop.Token);
        Assert.IsFalse(pending.IsCompleted);

        readStop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => pending);
        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClientRejectsValueKindMismatchAfterAcceptedOrLateDescriptor(bool lateDescriptor)
    {
        var id = new MetricId("typed.metric");
        var descriptor = new MetricDescriptor(id, "Typed", "percentage", MetricValueKind.Double);
        var name = Name();
        await using var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () =>
        {
            await peer.WaitForConnectionAsync(stop.Token);
            Assert.AreEqual(id, await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token));
            await MetricIpcProtocol.WriteAcceptedAsync(peer, lateDescriptor ? null : descriptor, stop.Token);
            if (lateDescriptor)
                await MetricIpcProtocol.WriteSnapshotAsync(peer, new MetricSnapshot(id, MetricValue.From(1d),
                    DateTimeOffset.UtcNow, MetricState.Fresh, MetricConfidence.Measured), descriptor, stop.Token);
            await MetricIpcProtocol.WriteSnapshotAsync(peer, new MetricSnapshot(id, MetricValue.From(2L),
                DateTimeOffset.UtcNow, MetricState.Fresh, MetricConfidence.Measured), stop.Token);
        }, stop.Token);
        await using var client = new NamedPipeMetricClient(name);
        await client.ConnectAsync(id, stop.Token);
        await using var reader = client.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
        if (lateDescriptor) Assert.IsTrue(await reader.MoveNextAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());
        await serving.WaitAsync(stop.Token);
    }

    [TestMethod]
    public async Task QuietSubscriptionEndsOnPipeClosureAfterFormerReceiveDeadline()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var readStop = new CancellationTokenSource();
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);
        await using var client = new NamedPipeMetricClient(name);
        await client.ConnectAsync(new MetricId("quiet"), stop.Token);
        await using var reader = client.ReadSnapshotsAsync(readStop.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(3500), stop.Token);
        Assert.IsFalse(pending.IsCompleted);

        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ExactIdAndDisconnectRemoveSubscription()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);
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
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);
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
        var waiting = new NamedPipeMetricServer(Name(), bus, registry).RunAsync(waitingStop.Token);
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
    public async Task DescriptorRoundTripsWithBothOptionalFieldsAndRejectsMismatchedIds()
    {
        var descriptor = new MetricDescriptor(new MetricId("metric.described"), "Described metric", "count",
            MetricValueKind.Int64, "Optional description", "tokens");
        using var accepted = new MemoryStream();
        await MetricIpcProtocol.WriteAcceptedAsync(accepted, descriptor);
        accepted.Position = 0;
        var acceptedDescriptor = await MetricIpcProtocol.ReadAcceptedAsync(accepted);
        Assert.AreEqual(descriptor.Id, acceptedDescriptor?.Id);
        Assert.AreEqual(descriptor.DisplayName, acceptedDescriptor?.DisplayName);
        Assert.AreEqual(descriptor.SemanticType, acceptedDescriptor?.SemanticType);
        Assert.AreEqual(descriptor.Unit, acceptedDescriptor?.Unit);
        Assert.AreEqual(descriptor.ValueKind, acceptedDescriptor?.ValueKind);
        Assert.AreEqual(descriptor.Description, acceptedDescriptor?.Description);

        var snapshot = new MetricSnapshot(descriptor.Id, MetricValue.From(12L), DateTimeOffset.UtcNow,
            MetricState.Fresh, MetricConfidence.Measured);
        using var stream = new MemoryStream();
        await MetricIpcProtocol.WriteSnapshotAsync(stream, snapshot, descriptor);
        stream.Position = 0;
        var frame = await MetricIpcProtocol.ReadSnapshotFrameAsync(stream);
        Assert.AreEqual(snapshot.MetricId, frame.Snapshot.MetricId);
        Assert.AreEqual(descriptor.Id, frame.Descriptor?.Id);
        Assert.AreEqual(descriptor.ValueKind, frame.Descriptor?.ValueKind);
        Assert.AreEqual(descriptor.Description, frame.Descriptor?.Description);

        var mismatched = new MetricDescriptor(new MetricId("metric.other"), "Other", "count",
            MetricValueKind.String);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            MetricIpcProtocol.WriteSnapshotAsync(new MemoryStream(), snapshot, mismatched));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            MetricIpcProtocol.ReadAcceptedAsync(Frame("{\"Version\":1,\"Kind\":\"accepted\",\"Descriptor\":{}}")));
        Assert.IsNull(await MetricIpcProtocol.ReadAcceptedAsync(Frame("{\"Version\":1,\"Kind\":\"accepted\"}")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MetricIpcProtocol.ReadSnapshotFrameAsync(Frame(
            "{\"Version\":1,\"Kind\":\"snapshot\",\"MetricId\":\"metric.described\",\"ValueKind\":2,\"Value\":\"12\",\"ObservedAt\":\"2026-10-07T00:00:00+00:00\",\"State\":0,\"Confidence\":1,\"Descriptor\":{\"Id\":{\"Value\":\"metric.other\"},\"DisplayName\":\"Other\",\"SemanticType\":\"count\",\"Unit\":\"tokens\",\"ValueKind\":2}}")));
    }

    [TestMethod]
    public async Task HardwareGamingAndCodexDescriptorsTraverseTheSameNamedPipeContract()
    {
        MetricDescriptor[] descriptors =
        [
            new(new MetricId("cpu.total.usage"), "CPU Total Usage", "percentage", MetricValueKind.Double, unit: "%"),
            new(new MetricId("game.fps.display"), "Displayed FPS", "rate", MetricValueKind.Double, unit: "FPS"),
            new(new MetricId("ai.codex.quota.primary.remaining"), "Codex Primary Quota Remaining", "quota_remaining", MetricValueKind.Double, unit: "%"),
            new(new MetricId("ai.codex.quota.secondary.remaining"), "Codex Secondary Quota Remaining", "quota_remaining", MetricValueKind.Double, unit: "%"),
            new(new MetricId("ai.codex.quota.primary.reset"), "Codex Primary Reset At", "timestamp", MetricValueKind.DateTimeOffset),
            new(new MetricId("ai.codex.quota.secondary.reset"), "Codex Secondary Reset At", "timestamp", MetricValueKind.DateTimeOffset),
            new(new MetricId("ai.codex.quota.primary.duration"), "Codex Primary Window Duration", "duration", MetricValueKind.TimeSpan),
            new(new MetricId("ai.codex.quota.secondary.duration"), "Codex Secondary Window Duration", "duration", MetricValueKind.TimeSpan)
        ];
        var ids = descriptors.Select(descriptor => descriptor.Id.Value).ToArray();
        MetricValue[] values =
        [
            MetricValue.From(44.25),
            MetricValue.From(120.5),
            MetricValue.From(62.75),
            MetricValue.From(87.5),
            MetricValue.From(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            MetricValue.From(new DateTimeOffset(2030, 1, 8, 0, 0, 0, TimeSpan.Zero)),
            MetricValue.From(TimeSpan.FromHours(5)),
            MetricValue.From(TimeSpan.FromDays(7))
        ];
        var registry = new MetricRegistry();
        foreach (var descriptor in descriptors) Assert.IsTrue(registry.TryRegister(descriptor));
        Assert.AreEqual(descriptors.Length, registry.Count);

        using var bus = new MetricBus(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipeName = Name();
        var server = new NamedPipeMetricServer(pipeName, bus, registry);
        var serverTask = server.RunAsync(stop.Token);
        var clients = new NamedPipeMetricClient?[ids.Length];
        var readers = new System.Collections.Generic.IAsyncEnumerator<MetricSnapshot>?[ids.Length];
        var pending = new Task<bool>[ids.Length];
        try
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var client = new NamedPipeMetricClient(pipeName);
                clients[i] = client;
                await client.ConnectAsync(descriptors[i].Id, stop.Token);
                Assert.AreEqual(descriptors[i].Id, client.Descriptor?.Id);
                Assert.AreEqual(descriptors[i].DisplayName, client.Descriptor?.DisplayName);
                Assert.AreEqual(descriptors[i].SemanticType, client.Descriptor?.SemanticType);
                Assert.AreEqual(descriptors[i].Unit, client.Descriptor?.Unit);
                Assert.AreEqual(descriptors[i].ValueKind, client.Descriptor?.ValueKind);
                var reader = client.ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
                readers[i] = reader;
                pending[i] = reader.MoveNextAsync().AsTask();
            }

            for (var i = 0; i < ids.Length; i++)
                await bus.PublishAsync(new MetricSnapshot(new MetricId(ids[i]), values[i],
                    new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), MetricState.Fresh,
                    MetricConfidence.Measured, "Codex.Usage.ReadOnly.v1"), stop.Token);

            Assert.IsTrue((await Task.WhenAll(pending)).All(result => result));
            for (var i = 0; i < ids.Length; i++)
            {
                var snapshot = readers[i]!.Current;
                var value = snapshot.Value!.Value;
                Assert.AreEqual(ids[i], snapshot.MetricId.Value);
                Assert.AreEqual(descriptors[i].ValueKind, value.Kind);
                Assert.AreEqual(values[i], value);
                Assert.AreEqual(MetricState.Fresh, snapshot.State);
                Assert.AreEqual(MetricConfidence.Measured, snapshot.Confidence);
                Assert.AreEqual("Codex.Usage.ReadOnly.v1", snapshot.Source);
            }
        }
        finally
        {
            stop.Cancel();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            for (var i = 0; i < ids.Length; i++)
            {
                if (readers[i] is { } reader) await reader.DisposeAsync();
                if (clients[i] is { } client) await client.DisposeAsync();
            }
        }
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
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);
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
        await Task.Delay(250);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    [TestMethod]
    public async Task BadSubscribeDoesNotStopServer()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = Name();
        var server = new NamedPipeMetricServer(name, bus, registry).RunAsync(stop.Token);
        await using (var invalid = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await invalid.ConnectAsync(stop.Token);
            using var frame = Frame("{\"Version\":2,\"Kind\":\"subscribe\",\"MetricId\":\"x\"}");
            await invalid.WriteAsync(frame.ToArray(), stop.Token);
        }
        await using (var valid = new NamedPipeMetricClient(name))
        {
            await valid.ConnectAsync(new MetricId("x"), stop.Token);
        }
        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClientWaitsForPipeAvailabilityAndDoesNotShortenAcceptance(bool busy)
    {
        var name = Name();
        var id = new MetricId("delayed.metric");
        var descriptor = new MetricDescriptor(id, "Delayed", "count", MetricValueKind.Int64);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var occupied = busy ? new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly) : null;
        await using var occupant = busy ? new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly) : null;
        if (busy)
        {
            var accepted = occupied!.WaitForConnectionAsync(stop.Token);
            await occupant!.ConnectAsync(stop.Token);
            await accepted;
        }
        await using var client = new NamedPipeMetricClient(name);
        var connecting = client.ConnectAsync(id, stop.Token);
        await Task.Delay(350, stop.Token);
        Assert.IsFalse(connecting.IsCompleted);
        if (busy)
        {
            await occupant!.DisposeAsync();
            await occupied!.DisposeAsync();
        }
        await using var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await peer.WaitForConnectionAsync(stop.Token);
        Assert.AreEqual(id, await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token));
        await Task.Delay(250, stop.Token);
        Assert.IsFalse(connecting.IsCompleted);
        await MetricIpcProtocol.WriteAcceptedAsync(peer, descriptor, stop.Token);
        await connecting.WaitAsync(stop.Token);
        Assert.AreEqual(descriptor.Id, client.Descriptor?.Id);
        Assert.AreEqual(descriptor.DisplayName, client.Descriptor?.DisplayName);
        Assert.AreEqual(descriptor.Unit, client.Descriptor?.Unit);
        Assert.AreEqual(descriptor.ValueKind, client.Descriptor?.ValueKind);
    }

    [TestMethod]
    public async Task ClientCancellationDuringStalledAcceptanceAllowsRetry()
    {
        var name = Name();
        var id = new MetricId("stalled.metric");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeMetricClient(name);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var connecting = client.ConnectAsync(id, deadline.Token);
        await peer.WaitForConnectionAsync(stop.Token);
        Assert.AreEqual(id, await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token));
        deadline.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await connecting);
        Assert.IsNull(client.Descriptor);
        peer.Disconnect();
        connecting = client.ConnectAsync(id, stop.Token);
        await peer.WaitForConnectionAsync(stop.Token);
        Assert.AreEqual(id, await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token));
        await MetricIpcProtocol.WriteAcceptedAsync(peer, stop.Token);
        await connecting.WaitAsync(stop.Token);
        Assert.IsNull(client.Descriptor);
    }
    [TestMethod]
    public async Task ClientCancellationOnBusyPipeAllowsRetry()
    {
        var name = Name();
        var id = new MetricId("busy.metric");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var occupant = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = peer.WaitForConnectionAsync(stop.Token);
        await occupant.ConnectAsync(stop.Token);
        await accepted;
        await using var client = new NamedPipeMetricClient(name);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var connecting = client.ConnectAsync(id, cancelled.Token);
        await Task.Delay(250, stop.Token);
        Assert.IsFalse(connecting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await connecting);
        await occupant.DisposeAsync();
        peer.Disconnect();
        connecting = client.ConnectAsync(id, stop.Token);
        await peer.WaitForConnectionAsync(stop.Token);
        Assert.AreEqual(id, await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token));
        await MetricIpcProtocol.WriteAcceptedAsync(peer, stop.Token);
        await connecting.WaitAsync(stop.Token);
    }

    [TestMethod]
    public async Task CompetingClientsRecoverWhenSinglePipeInstanceIsReleased()
    {
        var name = Name();
        var idA = new MetricId("busy.a");
        var idB = new MetricId("busy.b");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var a = new NamedPipeMetricClient(name);
        await using var b = new NamedPipeMetricClient(name);
        var connectA = a.ConnectAsync(idA, stop.Token);
        var connectB = b.ConnectAsync(idB, stop.Token);
        await peer.WaitForConnectionAsync(stop.Token);
        var first = await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token);
        Assert.IsTrue(first == idA || first == idB);
        await MetricIpcProtocol.WriteAcceptedAsync(peer, stop.Token);
        await (first == idA ? connectA : connectB).WaitAsync(stop.Token);
        Assert.IsFalse((first == idA ? connectB : connectA).IsCompleted);
        await (first == idA ? a : b).DisposeAsync();
        peer.Disconnect();
        await peer.WaitForConnectionAsync(stop.Token);
        Assert.AreEqual(first == idA ? idB : idA, await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token));
        await MetricIpcProtocol.WriteAcceptedAsync(peer, stop.Token);
        await (first == idA ? connectB : connectA).WaitAsync(stop.Token);
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
