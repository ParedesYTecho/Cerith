#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class SyntheticProviderTests
{
    [TestMethod]
    public void DescribesOnlyTheSyntheticCounter()
    {
        using var bus = new MetricBus(1);
        var provider = new SyntheticProvider(new MetricRegistry(), bus);

        Assert.AreEqual("synthetic.counter", provider.Descriptor.Id.Value);
        Assert.AreEqual("Synthetic Counter", provider.Descriptor.DisplayName);
        Assert.AreEqual("count", provider.Descriptor.SemanticType);
        Assert.AreEqual(MetricValueKind.Int64, provider.Descriptor.ValueKind);
        Assert.IsNull(provider.Descriptor.Unit);
    }

    [TestMethod]
    public void RegistersOnceAndKeepsTheFirstDescriptorForItsId()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        var first = new SyntheticProvider(registry, bus);
        var duplicate = new SyntheticProvider(registry, bus);

        Assert.IsTrue(first.TryRegister());
        Assert.IsFalse(duplicate.TryRegister());
        Assert.IsTrue(registry.TryGet(first.Descriptor.Id, out var registered));
        Assert.AreSame(first.Descriptor, registered);
        Assert.AreNotSame(duplicate.Descriptor, registered);
    }

    [TestMethod]
    public async Task PublishesConsecutiveInt64ValuesInOrder()
    {
        using var bus = new MetricBus(3);
        var provider = new SyntheticProvider(new MetricRegistry(), bus);
        await using var reader = bus.Subscribe(provider.Descriptor.Id).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();

        await provider.PublishNextAsync();
        await provider.PublishNextAsync();
        await provider.PublishNextAsync();

        Assert.IsTrue(await firstRead);
        Assert.AreEqual(0L, reader.Current.Value!.Value.AsInt64());
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(1L, reader.Current.Value!.Value.AsInt64());
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(2L, reader.Current.Value!.Value.AsInt64());
    }

    [TestMethod]
    public async Task PublishesFreshSyntheticSnapshotWithUnknownConfidence()
    {
        var observedAt = new DateTimeOffset(2026, 9, 25, 12, 30, 0, TimeSpan.Zero);
        using var bus = new MetricBus(1);
        var provider = new SyntheticProvider(
            new MetricRegistry(),
            bus,
            new FixedTimeProvider(observedAt));
        await using var reader = bus.Subscribe(provider.Descriptor.Id).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        await provider.PublishNextAsync();

        Assert.IsTrue(await read);
        var snapshot = reader.Current;
        Assert.AreEqual(provider.Descriptor.Id, snapshot.MetricId);
        Assert.AreEqual(MetricValueKind.Int64, snapshot.Value!.Value.Kind);
        Assert.AreEqual(MetricState.Fresh, snapshot.State);
        Assert.AreEqual(MetricConfidence.Unknown, snapshot.Confidence);
        Assert.AreEqual(observedAt, snapshot.ObservedAt);
        Assert.AreEqual("synthetic", snapshot.Source);
        Assert.IsNull(snapshot.ErrorCode);
    }

    [TestMethod]
    public async Task DoesNotReplayPublicationsMadeBeforeSubscription()
    {
        using var bus = new MetricBus(1);
        var provider = new SyntheticProvider(new MetricRegistry(), bus);

        await provider.PublishNextAsync();

        await using var reader = bus.Subscribe(provider.Descriptor.Id).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();
        Assert.IsFalse(read.IsCompleted);

        await provider.PublishNextAsync();

        Assert.IsTrue(await read);
        Assert.AreEqual(1L, reader.Current.Value!.Value.AsInt64());
    }

    [TestMethod]
    public async Task CancelledPublicationDoesNotAdvanceTheSequence()
    {
        using var bus = new MetricBus(1);
        var provider = new SyntheticProvider(new MetricRegistry(), bus);
        await using var reader = bus.Subscribe(provider.Descriptor.Id).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await provider.PublishNextAsync();
        Assert.IsTrue(await firstRead);
        Assert.AreEqual(0L, reader.Current.Value!.Value.AsInt64());

        await provider.PublishNextAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelledPublish = provider.PublishNextAsync(cancellation.Token).AsTask();
        Assert.IsFalse(cancelledPublish.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledPublish);

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(1L, reader.Current.Value!.Value.AsInt64());
        var nextRead = reader.MoveNextAsync().AsTask();
        await provider.PublishNextAsync();
        Assert.IsTrue(await nextRead);
        Assert.AreEqual(2L, reader.Current.Value!.Value.AsInt64());
    }

    [TestMethod]
    public void RejectsNullRegistryAndBus()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);

        Assert.ThrowsExactly<ArgumentNullException>(() => new SyntheticProvider(null!, bus));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SyntheticProvider(registry, null!));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
