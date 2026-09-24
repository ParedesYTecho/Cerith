#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Core.Tests;

[TestClass]
public sealed class MetricBusTests
{
    private static readonly MetricId Id = new("gpu.0.temperature.core");

    [TestMethod]
    public async Task PublishesIndependentlyToSubscribersOfTheSameId()
    {
        using var bus = new MetricBus(1);
        await using var first = bus.Subscribe(Id).GetAsyncEnumerator();
        await using var second = bus.Subscribe(new MetricId(Id.Value)).GetAsyncEnumerator();
        var firstRead = first.MoveNextAsync().AsTask();
        var secondRead = second.MoveNextAsync().AsTask();
        var snapshot = Snapshot(1);

        await bus.PublishAsync(snapshot);

        Assert.IsTrue(await firstRead);
        Assert.IsTrue(await secondRead);
        Assert.AreSame(snapshot, first.Current);
        Assert.AreSame(snapshot, second.Current);
    }

    [TestMethod]
    public async Task RoutesOnlyToTheExactIdIncludingCasing()
    {
        using var bus = new MetricBus(1);
        await using var lower = bus.Subscribe(Id).GetAsyncEnumerator();
        await using var upper = bus.Subscribe(new MetricId("GPU.0.temperature.core")).GetAsyncEnumerator();
        var lowerRead = lower.MoveNextAsync().AsTask();
        var upperRead = upper.MoveNextAsync().AsTask();
        var lowerSnapshot = Snapshot(1);
        var upperSnapshot = Snapshot(2, "GPU.0.temperature.core");

        await bus.PublishAsync(upperSnapshot);
        Assert.IsFalse(lowerRead.IsCompleted);
        Assert.IsTrue(await upperRead);
        Assert.AreSame(upperSnapshot, upper.Current);

        await bus.PublishAsync(lowerSnapshot);
        Assert.IsTrue(await lowerRead);
        Assert.AreSame(lowerSnapshot, lower.Current);
    }

    [TestMethod]
    public async Task SubscriberDoesNotReceiveAnotherMetric()
    {
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        await bus.PublishAsync(Snapshot(1, "cpu.total.usage"));
        Assert.IsFalse(read.IsCompleted);
        var matching = Snapshot(2);
        await bus.PublishAsync(matching);

        Assert.IsTrue(await read);
        Assert.AreSame(matching, reader.Current);
    }

    [TestMethod]
    public async Task SequentialPublicationsKeepTheirOrder()
    {
        using var bus = new MetricBus(2);
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        var first = Snapshot(1);
        var second = Snapshot(2);
        var third = Snapshot(3);

        await bus.PublishAsync(first);
        Assert.IsTrue(await firstRead);
        await bus.PublishAsync(second);
        await bus.PublishAsync(third);

        Assert.AreSame(first, reader.Current);
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreSame(second, reader.Current);
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreSame(third, reader.Current);
    }

    [TestMethod]
    public async Task PublicationsBeforeActivationAreNotReplayed()
    {
        using var bus = new MetricBus(1);
        var old = Snapshot(1);
        await bus.PublishAsync(old);
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        Assert.IsFalse(read.IsCompleted);
        var current = Snapshot(2);
        await bus.PublishAsync(current);

        Assert.IsTrue(await read);
        Assert.AreSame(current, reader.Current);
    }

    [TestMethod]
    public void RejectsNonPositiveCapacity()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MetricBus(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MetricBus(-1));
    }

    [TestMethod]
    public async Task FullSubscriberBufferBackpressuresUntilItIsRead()
    {
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(Snapshot(1));
        Assert.IsTrue(await firstRead);
        await bus.PublishAsync(Snapshot(2));

        var thirdPublish = bus.PublishAsync(Snapshot(3)).AsTask();
        Assert.IsFalse(thirdPublish.IsCompleted);
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(2L, reader.Current.Value!.Value.AsInt64());
        await thirdPublish;
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(3L, reader.Current.Value!.Value.AsInt64());
    }

    [TestMethod]
    public async Task CancellingOneSubscriberDoesNotAffectAnother()
    {
        using var bus = new MetricBus(1);
        using var cancellation = new CancellationTokenSource();
        await using var cancelled = bus.Subscribe(Id, cancellation.Token).GetAsyncEnumerator();
        await using var remaining = bus.Subscribe(Id).GetAsyncEnumerator();
        var cancelledRead = cancelled.MoveNextAsync().AsTask();
        var remainingRead = remaining.MoveNextAsync().AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledRead);
        var snapshot = Snapshot(1);
        await bus.PublishAsync(snapshot);

        Assert.IsTrue(await remainingRead);
        Assert.AreSame(snapshot, remaining.Current);
    }

    [TestMethod]
    public async Task BlockedPublisherCanBeCancelled()
    {
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(Snapshot(1));
        Assert.IsTrue(await firstRead);
        await bus.PublishAsync(Snapshot(2));
        using var cancellation = new CancellationTokenSource();

        var blocked = bus.PublishAsync(Snapshot(3), cancellation.Token).AsTask();
        Assert.IsFalse(blocked.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await blocked);

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(2L, reader.Current.Value!.Value.AsInt64());
    }

    [TestMethod]
    public async Task CancellingBufferedSubscriberReleasesBlockedPublisher()
    {
        using var bus = new MetricBus(1);
        using var cancellation = new CancellationTokenSource();
        await using var reader = bus.Subscribe(Id, cancellation.Token).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(Snapshot(1));
        Assert.IsTrue(await firstRead);
        await bus.PublishAsync(Snapshot(2));
        var blocked = bus.PublishAsync(Snapshot(3)).AsTask();
        Assert.IsFalse(blocked.IsCompleted);

        cancellation.Cancel();

        await blocked;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
    }

    [TestMethod]
    public async Task DisposingEnumeratorUnsubscribes()
    {
        using var bus = new MetricBus(1);
        var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(Snapshot(1));
        Assert.IsTrue(await firstRead);
        await reader.DisposeAsync();

        Assert.IsTrue(bus.PublishAsync(Snapshot(2)).IsCompletedSuccessfully);
        Assert.IsTrue(bus.PublishAsync(Snapshot(3)).IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task DisposeCompletesReadersAndIsIdempotent()
    {
        var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();

        bus.Dispose();
        bus.Dispose();

        Assert.IsFalse(await pending);
    }

    [TestMethod]
    public async Task DisposeReleasesBlockedPublisher()
    {
        var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(Snapshot(1));
        Assert.IsTrue(await firstRead);
        await bus.PublishAsync(Snapshot(2));
        var blocked = bus.PublishAsync(Snapshot(3)).AsTask();
        Assert.IsFalse(blocked.IsCompleted);

        bus.Dispose();

        await blocked;
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(2L, reader.Current.Value!.Value.AsInt64());
        Assert.IsFalse(await reader.MoveNextAsync());
    }

    [TestMethod]
    public async Task OperationsAfterDisposeFailWhenStarted()
    {
        var bus = new MetricBus(1);
        bus.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await bus.PublishAsync(Snapshot(1)));
        await using var reader = bus.Subscribe(Id).GetAsyncEnumerator();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await reader.MoveNextAsync());
    }

    private static MetricSnapshot Snapshot(long value, string? id = null) => new(
        id is null ? Id : new MetricId(id),
        MetricValue.From(value),
        new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
        MetricState.Fresh,
        MetricConfidence.Measured);
}
