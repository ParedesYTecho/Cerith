#nullable enable

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class MetricSubscriptionTests
{
    [TestMethod]
    public async Task AbsentServiceHasOneAttemptAndFailureReleasesFollowersForRecovery()
    {
        var gate = new MetricConnectionAttempt();
        var release = Signal();
        var calls = 0;
        Task Connect(CancellationToken token) { Interlocked.Increment(ref calls); return release.Task.WaitAsync(token); }
        var owner = gate.ConnectAsync(Connect, CancellationToken.None);
        var followers = Enumerable.Range(0, 12).Select(_ => gate.ConnectAsync(Connect, CancellationToken.None)).ToArray();
        Assert.AreEqual(1, calls);
        Assert.IsTrue(followers.All(task => !task.IsCompleted));
        release.SetException(new IOException("absent"));
        await Assert.ThrowsAsync<IOException>(() => owner);
        foreach (var follower in followers) await Assert.ThrowsAsync<IOException>(() => follower);
        Assert.AreEqual(1, calls);

        await gate.ConnectAsync(_ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        await gate.ConnectAsync(_ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task AcceptanceReleasesDistinctIdConnections()
    {
        var gate = new MetricConnectionAttempt();
        var release = Signal();
        var calls = 0;
        Task Connect(CancellationToken token) { Interlocked.Increment(ref calls); return release.Task.WaitAsync(token); }
        var owner = gate.ConnectAsync(Connect, CancellationToken.None);
        var follower = gate.ConnectAsync(Connect, CancellationToken.None);
        Assert.AreEqual(1, calls);
        release.SetResult();
        await Task.WhenAll(owner, follower).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task CancellingFollowerDoesNotCancelOwnerOrOtherFollowers()
    {
        var gate = new MetricConnectionAttempt();
        var release = Signal();
        var calls = 0;
        Task Connect(CancellationToken token) { Interlocked.Increment(ref calls); return release.Task.WaitAsync(token); }
        var owner = gate.ConnectAsync(Connect, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var cancelled = gate.ConnectAsync(Connect, stop.Token);
        var live = gate.ConnectAsync(Connect, CancellationToken.None);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        Assert.IsFalse(owner.IsCompleted);
        release.SetResult();
        await Task.WhenAll(owner, live).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task CancellingOwnerReleasesFollowerAndAllowsNextAttempt()
    {
        var gate = new MetricConnectionAttempt();
        using var stop = new CancellationTokenSource();
        var release = Signal();
        var owner = gate.ConnectAsync(token => release.Task.WaitAsync(token), stop.Token);
        var follower = gate.ConnectAsync(_ => throw new AssertFailedException("Follower attempted connection."), CancellationToken.None);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => owner);
        await Assert.ThrowsAsync<IOException>(() => follower);
        await gate.ConnectAsync(_ => Task.CompletedTask, CancellationToken.None);
    }

    [TestMethod]
    public async Task OldSuccessCannotReleaseFollowersAfterConnectionLoss()
    {
        var gate = new MetricConnectionAttempt();
        var release = Signal();
        var owner = gate.ConnectAsync(_ => release.Task, CancellationToken.None);
        var follower = gate.ConnectAsync(_ => throw new AssertFailedException("Old success released a follower."), CancellationToken.None);
        gate.MarkUnavailable();
        release.SetResult();
        await owner;
        await Assert.ThrowsAsync<IOException>(() => follower);
        var newRelease = Signal();
        var newOwner = gate.ConnectAsync(_ => newRelease.Task, CancellationToken.None);
        var newFollower = gate.ConnectAsync(_ => Task.CompletedTask, CancellationToken.None);
        Assert.IsFalse(newFollower.IsCompleted);
        newRelease.SetResult();
        await Task.WhenAll(newOwner, newFollower).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task OldIndependentFailureCannotInvalidateNewAvailability()
    {
        var gate = new MetricConnectionAttempt();
        await gate.ConnectAsync(_ => Task.CompletedTask, CancellationToken.None);
        var oldRelease = Signal();
        var old = gate.ConnectAsync(_ => oldRelease.Task, CancellationToken.None);
        gate.MarkUnavailable();
        await gate.ConnectAsync(_ => Task.CompletedTask, CancellationToken.None);
        oldRelease.SetException(new IOException("old connection"));
        await Assert.ThrowsAsync<IOException>(() => old);
        var hold = Signal();
        var current = gate.ConnectAsync(_ => hold.Task, CancellationToken.None);
        var independentlyEntered = false;
        await gate.ConnectAsync(_ => { independentlyEntered = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.IsTrue(independentlyEntered);
        hold.SetResult();
        await current;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledIndependentConnectionDoesNotInvalidateHealthyAvailability(bool cancelDuringConnect)
    {
        var gate = new MetricConnectionAttempt();
        await gate.ConnectAsync(_ => Task.CompletedTask, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var pending = Signal();
        if (!cancelDuringConnect) stop.Cancel();
        var cancelled = gate.ConnectAsync(token => pending.Task.WaitAsync(token), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        var hold = Signal();
        var current = gate.ConnectAsync(_ => hold.Task, CancellationToken.None);
        var entered = false;
        var independent = gate.ConnectAsync(_ => { entered = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.IsTrue(entered);
        hold.SetResult();
        await Task.WhenAll(current, independent).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task TwoRenderersShareOneAcceptedPipeAndDifferentIdsKeepIndependentPipes()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        using var peerA = Peer(name);
        using var peerB = Peer(name);
        var idA = new MetricId("example.percent");
        var idB = new MetricId("Example.percent");
        var descriptor = Descriptor(idA);
        var acceptedCount = 0;
        var acceptA = Accept(peerA, descriptor, stop.Token, () => Interlocked.Increment(ref acceptedCount));
        var acceptB = Accept(peerB, null, stop.Token, () => Interlocked.Increment(ref acceptedCount));
        var subscriptions = MetricSubscription.ForDistinctMetrics([idA, new(idA.Value), idB], new(), name);
        Assert.AreEqual(2, subscriptions.Length);
        var a = subscriptions.Single(item => item.MetricId == idA);
        var b = subscriptions.Single(item => item.MetricId == idB);
        var spec = new MetricPresentationSpec(idA, "0.0");
        var barSpec = new MetricRangeSpec(spec, 0, 100);
        var text = Signal();
        var bar = Signal();
        var ring = Signal();
        var independent = Signal();
        Action<MetricSubscription> renderText = state =>
        {
            if (state.Snapshot is null) return;
            Assert.AreEqual("42.0 %", MetricPresentation.Create(spec, state).ValueText);
            Assert.AreEqual("Example", state.Descriptor?.DisplayName);
            text.TrySetResult();
        };
        a.Updated += renderText;
        a.Updated += state =>
        {
            if (state.Snapshot is null) return;
            Assert.AreEqual(0.42, MetricRangePresentation.Create(barSpec, state.Descriptor, state.Snapshot).FillFraction);
            bar.TrySetResult();
        };
        a.Updated += state =>
        {
            if (state.Snapshot is null) return;
            Assert.AreEqual(0.42, MetricRangePresentation.Create(barSpec, state.Descriptor, state.Snapshot).FillFraction);
            ring.TrySetResult();
        };
        b.Updated += state => { if (state.Snapshot is not null) independent.TrySetResult(); };
        var run = Task.WhenAll(subscriptions.Select(item => item.RunAsync(stop.Token)));
        try
        {
            Assert.AreEqual(idA, await acceptA.WaitAsync(stop.Token));
            Assert.AreEqual(idB, await acceptB.WaitAsync(stop.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => a.RunAsync(stop.Token));
            await MetricIpcProtocol.WriteSnapshotAsync(peerA, Snapshot(idA, 42), stop.Token);
            await Task.WhenAll(text.Task, bar.Task, ring.Task).WaitAsync(stop.Token);
            Assert.IsFalse(independent.Task.IsCompleted);
            await MetricIpcProtocol.WriteSnapshotAsync(peerB, Snapshot(idB, 8), stop.Token);
            await independent.Task.WaitAsync(stop.Token);
            Assert.AreEqual(2, acceptedCount);
            Assert.AreEqual(42d, a.Snapshot!.Value!.Value.AsDouble());
            Assert.AreEqual(8d, b.Snapshot!.Value!.Value.AsDouble());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.IsNull(a.Snapshot);
        Assert.IsNull(a.Descriptor);
    }

    [TestMethod]
    public async Task ConsumerRemovalAndFailureDoNotReconnectOrBreakAnotherConsumer()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        using var peer = Peer(name);
        var id = new MetricId("example.percent");
        var accepted = Accept(peer, Descriptor(id), stop.Token);
        var subscription = MetricSubscription.ForDistinctMetrics([id, id], new(), name).Single();
        var removedCalls = 0;
        Action<MetricSubscription> removed = state => { if (state.Snapshot is not null) removedCalls++; };
        subscription.Updated += removed;
        subscription.Updated += _ => throw new InvalidOperationException("renderer failure");
        var first = Signal();
        var second = Signal();
        subscription.Updated += state =>
        {
            if (state.Snapshot?.Value?.AsDouble() == 1) first.TrySetResult();
            if (state.Snapshot?.Value?.AsDouble() == 2) second.TrySetResult();
        };
        var run = subscription.RunAsync(stop.Token);
        try
        {
            await accepted.WaitAsync(stop.Token);
            await MetricIpcProtocol.WriteSnapshotAsync(peer, Snapshot(id, 1), stop.Token);
            await first.Task.WaitAsync(stop.Token);
            subscription.Updated -= removed;
            await MetricIpcProtocol.WriteSnapshotAsync(peer, Snapshot(id, 2), stop.Token);
            await second.Task.WaitAsync(stop.Token);
            Assert.AreEqual(1, removedCalls);
            Assert.AreEqual(MetricConnectionState.Connected, subscription.ConnectionState);
            Assert.IsFalse(run.IsCompleted);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task CleanEofRebindsBothConsumersThroughDescriptorFreeAndLateMetadata()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = Name();
        var id = new MetricId("example.percent");
        using var firstPeer = Peer(name);
        var accepted = Accept(firstPeer, Descriptor(id), stop.Token);
        var subscription = new MetricSubscription(id, new(), name);
        var firstValue = Signal();
        var unavailable = Signal();
        var fallback = Signal();
        var late = Signal();
        var barLate = Signal();
        var spec = new MetricPresentationSpec(id, "0.0");
        subscription.Updated += state =>
        {
            if (state.ConnectionState == MetricConnectionState.Unavailable) unavailable.TrySetResult();
            if (state.Snapshot is not { } snapshot) return;
            if (snapshot.Value?.AsDouble() == 1) firstValue.TrySetResult();
            if (snapshot.Value?.AsDouble() == 2)
            {
                Assert.IsNull(state.Descriptor);
                Assert.AreEqual("2.0", MetricPresentation.Create(spec, state).ValueText);
                fallback.TrySetResult();
            }
            if (snapshot.Value?.AsDouble() == 3)
            {
                Assert.AreEqual("Late", state.Descriptor?.DisplayName);
                Assert.AreEqual("3.0 units", MetricPresentation.Create(spec, state).ValueText);
                late.TrySetResult();
            }
        };
        subscription.Updated += state =>
        {
            if (state.Snapshot?.Value?.AsDouble() != 3) return;
            Assert.AreEqual("Late", state.Descriptor?.DisplayName);
            Assert.AreEqual(0.3, MetricRangePresentation.Create(new(spec, 0, 10), state.Descriptor, state.Snapshot).FillFraction);
            barLate.TrySetResult();
        };
        var run = subscription.RunAsync(stop.Token);
        try
        {
            await accepted.WaitAsync(stop.Token);
            await MetricIpcProtocol.WriteSnapshotAsync(firstPeer, Snapshot(id, 1), stop.Token);
            await firstValue.Task.WaitAsync(stop.Token);
            firstPeer.Dispose();
            await unavailable.Task.WaitAsync(stop.Token);
            Assert.IsNull(subscription.Snapshot);
            using var secondPeer = Peer(name);
            await Accept(secondPeer, null, stop.Token).WaitAsync(stop.Token);
            var waiting = Signal();
            Action<MetricSubscription> onWaiting = state =>
            {
                if (state.ConnectionState != MetricConnectionState.Waiting) return;
                Assert.IsNull(state.Descriptor);
                Assert.IsNull(state.Snapshot);
                waiting.TrySetResult();
            };
            // Acceptance can have reached the consumer before the server's write continuation resumes.
            subscription.Updated += onWaiting;
            onWaiting(subscription);
            await waiting.Task.WaitAsync(stop.Token);
            await MetricIpcProtocol.WriteSnapshotAsync(secondPeer, Snapshot(id, 2), stop.Token);
            await fallback.Task.WaitAsync(stop.Token);
            var descriptor = new MetricDescriptor(id, "Late", "count", MetricValueKind.Double, unit: "units");
            await MetricIpcProtocol.WriteSnapshotAsync(secondPeer, Snapshot(id, 3), descriptor, stop.Token);
            await Task.WhenAll(late.Task, barLate.Task).WaitAsync(stop.Token);
            subscription.Updated -= onWaiting;
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task CancellationDuringAbsentServiceAttemptEndsAllLoops()
    {
        using var stop = new CancellationTokenSource();
        var subscriptions = MetricSubscription.ForDistinctMetrics(
            [new("a"), new("a"), new("b"), new("c")], new(), Name());
        var run = Task.WhenAll(subscriptions.Select(item => item.RunAsync(stop.Token)));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(subscriptions.All(item => item.Snapshot is null && item.Descriptor is null));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [TestMethod]
    public async Task StalledAcceptanceUsesExistingDeadlineAndReleasesFollowers()
    {
        var name = Name();
        var gate = new MetricConnectionAttempt();
        var owner = new MetricSubscription(new MetricId("deadline.owner"), gate, name);
        var follower = new MetricSubscription(new MetricId("deadline.follower"), gate, name);
        var ownerUnavailable = Signal();
        var followerUnavailable = Signal();
        owner.Updated += state => { if (state.ConnectionState == MetricConnectionState.Unavailable) ownerUnavailable.TrySetResult(); };
        follower.Updated += state => { if (state.ConnectionState == MetricConnectionState.Unavailable) followerUnavailable.TrySetResult(); };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ownerRun = owner.RunAsync(stop.Token);
        Task? followerRun = null;
        try
        {
            await Task.Delay(350, stop.Token);
            await using var peer = Peer(name);
            await peer.WaitForConnectionAsync(stop.Token);
            Assert.AreEqual(owner.MetricId, await MetricIpcProtocol.ReadSubscribeAsync(peer, stop.Token));
            followerRun = follower.RunAsync(stop.Token);
            await Task.WhenAll(ownerUnavailable.Task, followerUnavailable.Task).WaitAsync(TimeSpan.FromSeconds(7));
            Assert.IsFalse(stop.IsCancellationRequested);
            Assert.IsNull(owner.Snapshot);
            Assert.IsNull(follower.Snapshot);
        }
        finally
        {
            stop.Cancel();
            await ownerRun;
            if (followerRun is not null) await followerRun;
        }
    }
    private static string Name() => "Cerith.Binding.Test." + Guid.NewGuid().ToString("N");
    private static NamedPipeServerStream Peer(string name) => new(name, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private static MetricDescriptor Descriptor(MetricId id) => new(id, "Example", "percentage", MetricValueKind.Double, unit: "%");
    private static MetricSnapshot Snapshot(MetricId id, double value) =>
        new(id, MetricValue.From(value), DateTimeOffset.UtcNow, MetricState.Fresh, MetricConfidence.Measured);

    private static async Task<MetricId> Accept(NamedPipeServerStream pipe, MetricDescriptor? descriptor,
        CancellationToken token, Action? onAccepted = null)
    {
        await pipe.WaitForConnectionAsync(token);
        var id = await MetricIpcProtocol.ReadSubscribeAsync(pipe, token);
        await MetricIpcProtocol.WriteAcceptedAsync(pipe, descriptor, token);
        onAccepted?.Invoke();
        return id;
    }
}
