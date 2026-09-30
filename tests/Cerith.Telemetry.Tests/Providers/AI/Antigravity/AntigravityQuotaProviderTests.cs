#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Antigravity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Antigravity;

[TestClass]
public sealed class AntigravityQuotaProviderTests
{
    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string MetricId = "ai.antigravity.quota.3p-weekly.remaining";

    [TestMethod]
    [DataRow("account")]
    [DataRow("process")]
    [DataRow("session-generation")]
    public async Task PendingPublicationIsCanceledWhenOwnerIdentityChanges(string change)
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        var oldOwner = Owner();
        var newOwner = change switch
        {
            "account" => Owner(email: "synthetic-account-B"),
            "process" => Owner(processId: 77, start: "synthetic-start-B"),
            _ => Owner(sessionGeneration: "synthetic-session-generation-B")
        };
        var acquisitions = new Queue<AntigravityQuotaAcquisition>([
            Acquisition(oldOwner, time.GetUtcNow(), 0.25, time.GetUtcNow().AddDays(2)),
            Acquisition(newOwner, time.GetUtcNow(), 0.75, time.GetUtcNow().AddDays(2))
        ]);
        using var provider = Provider(bus, time, _ => Task.FromResult(acquisitions.Dequeue()));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blockedOldPublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedOldPublication.IsCompleted, "The capacity-one subscriber should backpressure the second publication.");

        time.Advance(AntigravityQuotaProvider.PollInterval);
        await provider.PollOnceAsync();
        await blockedOldPublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble(), 0.000001,
            "A snapshot already buffered before the transition cannot be retracted.");
        var next = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(75d, reader.Current.Value!.Value.AsDouble(), 0.000001,
            "No blocked publication from the previous ownership may be released after the transition.");
    }

    [TestMethod]
    public async Task ResetCrossingCancelsBlockedValuePublicationAndInvalidatesItsBucket()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        using var provider = Provider(bus, time, _ => Task.FromResult(
            Acquisition(Owner(), time.GetUtcNow(), 0.4, time.GetUtcNow().AddSeconds(10))));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blockedValuePublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedValuePublication.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(10));
        await provider.ExpireValidityOnceAsync();
        await blockedValuePublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Stale, reader.Current.State);
        var next = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task MonotonicCacheExpirySurvivesBackwardWallClockAdjustmentAndCancelsPendingValue()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        using var provider = Provider(bus, time, _ => Task.FromResult(
            Acquisition(Owner(), time.GetUtcNow(), 0.5, time.GetUtcNow().AddDays(2))));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blockedValuePublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedValuePublication.IsCompleted);

        time.ShiftUtcOnly(TimeSpan.FromDays(-1));
        time.Advance(AntigravityQuotaProvider.MaximumCacheAge);
        await provider.ExpireValidityOnceAsync();
        await blockedValuePublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        var next = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("cache_expired", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task RetryAfterBeyondCacheLifetimeDoesNotRetryEarlyAndFailureIsImmediatelyStale()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        var owner = Owner();
        var acquisitionCount = 0;
        var sequence = new Queue<AntigravityQuotaAcquisition>([
            Acquisition(owner, time.GetUtcNow(), 0.6, time.GetUtcNow().AddDays(2)),
            AntigravityQuotaAcquisition.Failure("rate_limited", MetricState.Unavailable, owner,
                TimeSpan.FromMinutes(20)),
            Acquisition(owner, time.GetUtcNow(), 0.7, time.GetUtcNow().AddDays(2))
        ]);
        using var sequencedProvider = Provider(bus, time, _ =>
        {
            acquisitionCount++;
            return Task.FromResult(sequence.Dequeue());
        });

        await sequencedProvider.PollOnceAsync();
        time.Advance(AntigravityQuotaProvider.PollInterval);
        await sequencedProvider.PollOnceAsync();
        var stale = await PublishOneAsync(sequencedProvider, bus, MetricId);
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual(60d, stale.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(InitialUtc, stale.ObservedAt);

        time.Advance(TimeSpan.FromMinutes(15));
        await sequencedProvider.ExpireValidityOnceAsync();
        await sequencedProvider.PollOnceAsync();
        Assert.AreEqual(2, acquisitionCount, "The expired cache must not shorten the server's 20-minute retry delay.");

        time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));
        await sequencedProvider.PollOnceAsync();
        Assert.AreEqual(2, acquisitionCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await sequencedProvider.PollOnceAsync();
        Assert.AreEqual(3, acquisitionCount);
    }

    [TestMethod]
    public async Task AuthRejectionAndMissingOwnershipClearPreviouslyValidCache()
    {
        foreach (var failure in new[] {
            AntigravityQuotaAcquisition.Failure("auth_required", MetricState.Error, Owner()),
            AntigravityQuotaAcquisition.Failure("identity_unavailable", MetricState.Unavailable)
        })
        {
            var time = new ManualTimeProvider(InitialUtc);
            using var bus = new MetricBus(1);
            var sequence = new Queue<AntigravityQuotaAcquisition>([
                Acquisition(Owner(), InitialUtc, 0.6, InitialUtc.AddDays(2)), failure
            ]);
            using var provider = Provider(bus, time, _ => Task.FromResult(sequence.Dequeue()));
            await provider.PollOnceAsync();
            var first = await PublishOneAsync(provider, bus, MetricId);
            Assert.AreEqual(MetricState.Stale, first.State);
            Assert.AreEqual("source_freshness_unknown", first.ErrorCode);
            time.Advance(AntigravityQuotaProvider.PollInterval);
            await provider.PollOnceAsync();
            var rejected = await PublishOneAsync(provider, bus, MetricId);
            Assert.IsNull(rejected.Value);
            Assert.AreEqual(failure.FailureState, rejected.State);
            Assert.AreEqual(failure.ErrorCode, rejected.ErrorCode);
        }
    }

    [TestMethod]
    public async Task OwnerMonitorRejectsLateOldOwnerAcquisition()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        var completion = new TaskCompletionSource<AntigravityQuotaAcquisition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var provider = new AntigravityQuotaProvider(new MetricRegistry(), bus,
            _ => ++count == 1 ? Task.FromResult(Acquisition(Owner(), InitialUtc, 0.6, InitialUtc.AddDays(2)))
                : completion.Task,
            static (_, _) => Task.FromResult(new AntigravityOwnerVerification(
                AntigravityOwnerVerificationState.Changed, null)), time);
        await provider.PollOnceAsync();
        time.Advance(AntigravityQuotaProvider.PollInterval);
        var pending = provider.PollOnceAsync();
        Assert.IsFalse(pending.IsCompleted);
        await provider.VerifyOwnerOnceAsync();
        completion.SetResult(Acquisition(Owner(), InitialUtc, 0.9, InitialUtc.AddDays(2)));
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await PublishOneAsync(provider, bus, MetricId);
        Assert.IsNull(result.Value);
        Assert.AreEqual("identity_unavailable", result.ErrorCode);
    }

    [TestMethod]
    public async Task ShutdownPublicationIsBoundedWhenSubscriberBackpressurePersists()
    {
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        using var provider = Provider(bus, TimeProvider.System, _ => Task.FromResult(
            Acquisition(Owner(), DateTimeOffset.UtcNow, 0.3, DateTimeOffset.UtcNow.AddDays(1))));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blocked = provider.PublishCurrentAsync();
        Assert.IsFalse(blocked.IsCompleted);

        using var stop = new CancellationTokenSource();
        var run = provider.RunAsync(stop.Token);
        var timer = Stopwatch.StartNew();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(2));
        await blocked.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task WorkerFaultStillPublishesStoppedAndReachesSupervisor()
    {
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        var stopped = reader.MoveNextAsync().AsTask();
        using var provider = Provider(bus, new ThrowingTimerTimeProvider(), _ => Task.FromResult(
            Acquisition(Owner(), DateTimeOffset.UtcNow, 0.3, DateTimeOffset.UtcNow.AddDays(1))));

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => provider.RunAsync(CancellationToken.None));
        Assert.AreEqual("synthetic timer creation failure", failure.Message);
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
    }

    private static async Task<MetricSnapshot> PublishOneAsync(AntigravityQuotaProvider provider, MetricBus bus,
        string metricId)
    {
        await using var reader = bus.Subscribe(new MetricId(metricId)).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(1)));
        return reader.Current;
    }

    private static AntigravityQuotaProvider Provider(MetricBus bus, TimeProvider time,
        Func<CancellationToken, Task<AntigravityQuotaAcquisition>> acquire) =>
        new(new MetricRegistry(), bus, acquire,
            static (owner, _) => Task.FromResult(new AntigravityOwnerVerification(
                AntigravityOwnerVerificationState.Verified, owner)), time);

    private static AntigravityOwnerKey Owner(string email = "synthetic-account-A", int processId = 42,
        string start = "synthetic-start-A", string sessionGeneration = "synthetic-session-generation-A") =>
        new(email, processId, start, sessionGeneration);

    private static AntigravityQuotaAcquisition Acquisition(AntigravityOwnerKey owner, DateTimeOffset observedAt,
        double fraction, DateTimeOffset resetAt)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            response = new { groups = new[] { new { buckets = new[] {
                new { bucketId = "3p-weekly", remainingFraction = fraction, resetTime = resetAt.ToString("O") }
            } } } }
        });
        return new AntigravityQuotaAcquisition(owner, AntigravityQuotaParser.Parse(payload, observedAt),
            MetricState.Error, null);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }

        public void ShiftUtcOnly(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class ThrowingTimerTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("synthetic timer creation failure");
    }
}
