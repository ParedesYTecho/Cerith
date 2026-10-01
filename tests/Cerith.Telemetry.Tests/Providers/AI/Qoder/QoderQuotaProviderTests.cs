#nullable enable

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Qoder;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Qoder;

[TestClass]
public sealed class QoderQuotaProviderTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string PersonalUsedId = "ai.qoder.credits.personal.used";
    private const string AddonUsedId = "ai.qoder.credits.addon.used";
    private const string OverallUsedId = "ai.qoder.quota.overall.used_percent";
    private const string QuotaExceededId = "ai.qoder.quota.exceeded";
    private const string OrganizationUsedId = "ai.qoder.credits.organization.used";

    [TestMethod]
    public async Task PollRegistersTypedMetricsAndPublishesSeparateCreditPools()
    {
        const string json = """
            {
              "userId": "synthetic-user-1",
              "totalUsagePercentage": 31.25,
              "userQuota": { "used": 12.5, "total": 100, "remaining": 87.5, "percentage": 12.5, "unit": "credits" },
              "orgResourcePackage": { "used": 8, "cap": 40, "remaining": 32, "percentage": 20, "available": true, "unit": "credits" }
            }
            """;
        var owner = Owner("synthetic-user-1", "synthetic-org-1");
        var acquisition = Acquisition(json, owner);
        var registry = new MetricRegistry();
        using var bus = new MetricBus(2);
        await using var personalReader = bus.Subscribe(new MetricId(PersonalUsedId)).GetAsyncEnumerator();
        await using var organizationReader = bus.Subscribe(new MetricId(OrganizationUsedId)).GetAsyncEnumerator();
        using var provider = CreateProvider(registry, bus, acquisition, owner);
        var personalNext = personalReader.MoveNextAsync().AsTask();
        var organizationNext = organizationReader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();

        Assert.IsTrue(await personalNext.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await organizationNext.WaitAsync(TimeSpan.FromSeconds(1)));
        AssertSnapshot(personalReader.Current, 12.5d, ObservedAt);
        Assert.AreEqual(MetricState.Unavailable, organizationReader.Current.State);
        Assert.AreEqual("scope_unverified", organizationReader.Current.ErrorCode);
        Assert.IsNull(organizationReader.Current.Value);
        Assert.AreEqual(16, registry.Count);
        Assert.IsTrue(registry.TryGet(new MetricId(PersonalUsedId), out var personalDescriptor));
        Assert.AreEqual(MetricValueKind.Double, personalDescriptor!.ValueKind);
        Assert.AreEqual("credits", personalDescriptor.Unit);
        Assert.IsTrue(registry.TryGet(new MetricId(OrganizationUsedId), out var organizationDescriptor));
        Assert.AreEqual(MetricValueKind.Double, organizationDescriptor!.ValueKind);
        Assert.AreEqual("credits", organizationDescriptor.Unit);
    }

    [TestMethod]
    public async Task MissingQuotaFieldsPublishUnavailableWithoutValues()
    {
        const string json = """
            { "userId": "synthetic-user-1", "userQuota": { "unit": "credits" } }
            """;
        var owner = Owner("synthetic-user-1", null);
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(PersonalUsedId)).GetAsyncEnumerator();
        using var provider = CreateProvider(registry, bus, Acquisition(json, owner), owner);
        var next = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();

        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual(MetricConfidence.Unknown, reader.Current.Confidence);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(ObservedAt, reader.Current.ObservedAt);
    }

    [TestMethod]
    public async Task OwnerTransitionCancelsBlockedOldOwnerPublication()
    {
        const string json = """
            { "userId": "synthetic-user-1", "userQuota": { "used": 5, "unit": "credits" } }
            """;
        var oldOwner = Owner("synthetic-user-1", null);
        var newOwner = Owner("synthetic-user-2", null) with { ProcessId = oldOwner.ProcessId + 1 };
        var changed = false;
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(PersonalUsedId)).GetAsyncEnumerator();
        var provider = new QoderQuotaProvider(registry, bus, _ => Task.FromResult(Acquisition(json, oldOwner)),
            (_, _) => Task.FromResult(changed
                ? new QoderOwnerVerification(QoderOwnerVerificationState.Changed, newOwner)
                : new QoderOwnerVerification(QoderOwnerVerificationState.Verified, oldOwner)), TimeProvider.System);
        using (provider)
        {
            var first = reader.MoveNextAsync().AsTask();
            await provider.PollOnceAsync();
            Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(1)));

            await provider.PublishCurrentAsync();
            var blockedOldPublication = provider.PublishCurrentAsync();
            Assert.IsFalse(blockedOldPublication.IsCompleted);
            changed = true;

            var transition = provider.VerifyOwnerOnceAsync();
            await blockedOldPublication.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(5d, reader.Current.Value!.Value.AsDouble(), 0.000001,
                "A value already buffered before ownership changed cannot be retracted.");

            var next = reader.MoveNextAsync().AsTask();
            await transition.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
            Assert.IsNull(reader.Current.Value);
        }
    }

    [TestMethod]
    public async Task PlanExpiryInvalidatesPersonalAndAggregateButKeepsIndependentAddonAndObservedAt()
    {
        var time = new ManualTimeProvider(ObservedAt);
        var owner = Owner("synthetic-user-1", null);
        var expiry = ObservedAt.AddSeconds(10).ToUnixTimeMilliseconds();
        var json = $$"""
            {
              "userId": "synthetic-user-1",
              "expiresAt": {{expiry}},
              "totalUsagePercentage": 42,
              "isQuotaExceeded": false,
              "userQuota": { "used": 5, "total": 10, "remaining": 5, "unit": "credits" },
              "addOnQuota": { "used": 2, "total": 20, "remaining": 18, "unit": "credits" }
            }
            """;
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        await using var personalReader = bus.Subscribe(new MetricId(PersonalUsedId)).GetAsyncEnumerator();
        await using var addonReader = bus.Subscribe(new MetricId(AddonUsedId)).GetAsyncEnumerator();
        await using var overallReader = bus.Subscribe(new MetricId(OverallUsedId)).GetAsyncEnumerator();
        await using var exceededReader = bus.Subscribe(new MetricId(QuotaExceededId)).GetAsyncEnumerator();
        using var provider = new QoderQuotaProvider(registry, bus,
            _ => Task.FromResult(Acquisition(json, owner)),
            (_, _) => Task.FromResult(new QoderOwnerVerification(QoderOwnerVerificationState.Verified, owner)), time);
        var personalInitial = personalReader.MoveNextAsync().AsTask();
        var addonInitial = addonReader.MoveNextAsync().AsTask();
        var overallInitial = overallReader.MoveNextAsync().AsTask();
        var exceededInitial = exceededReader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        Assert.IsTrue(await personalInitial.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await addonInitial.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await overallInitial.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await exceededInitial.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Fresh, overallReader.Current.State);
        Assert.AreEqual(MetricState.Fresh, exceededReader.Current.State);
        time.Advance(TimeSpan.FromSeconds(11));

        var personalExpired = personalReader.MoveNextAsync().AsTask();
        var addonAfterExpiry = addonReader.MoveNextAsync().AsTask();
        var overallExpired = overallReader.MoveNextAsync().AsTask();
        var exceededExpired = exceededReader.MoveNextAsync().AsTask();
        await provider.ExpireValidityOnceAsync();

        Assert.IsTrue(await personalExpired.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, personalReader.Current.State);
        Assert.AreEqual("quota_expired", personalReader.Current.ErrorCode);
        Assert.IsNull(personalReader.Current.Value);
        Assert.AreEqual(ObservedAt, personalReader.Current.ObservedAt);
        Assert.IsTrue(await addonAfterExpiry.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Fresh, addonReader.Current.State);
        Assert.AreEqual(2d, addonReader.Current.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(ObservedAt, addonReader.Current.ObservedAt);
        Assert.IsTrue(await overallExpired.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, overallReader.Current.State);
        Assert.AreEqual("quota_expired", overallReader.Current.ErrorCode);
        Assert.IsNull(overallReader.Current.Value);
        Assert.AreEqual(ObservedAt, overallReader.Current.ObservedAt);
        Assert.IsTrue(await exceededExpired.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, exceededReader.Current.State);
        Assert.AreEqual("quota_expired", exceededReader.Current.ErrorCode);
        Assert.IsNull(exceededReader.Current.Value);
        Assert.AreEqual(ObservedAt, exceededReader.Current.ObservedAt);
    }

    [TestMethod]
    public async Task HardExpiryInvalidatesOldOwnerWhileAcquisitionIsStillPending()
    {
        var time = new ManualTimeProvider(ObservedAt);
        var owner = Owner("synthetic-user-1", null);
        const string json = """
            { "userId": "synthetic-user-1", "userQuota": { "used": 5, "unit": "credits" } }
            """;
        var pending = new TaskCompletionSource<QoderQuotaAcquisition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitionNumber = 0;
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(PersonalUsedId)).GetAsyncEnumerator();
        using var provider = new QoderQuotaProvider(registry, bus,
            _ => Interlocked.Increment(ref acquisitionNumber) == 1
                ? Task.FromResult(Acquisition(json, owner))
                : pending.Task,
            (_, _) => Task.FromResult(new QoderOwnerVerification(QoderOwnerVerificationState.Verified, owner)), time);
        var initial = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        Assert.IsTrue(await initial.WaitAsync(TimeSpan.FromSeconds(1)));
        var slowPoll = provider.PollOnceAsync();
        Assert.IsFalse(slowPoll.IsCompleted);

        time.Advance(QoderQuotaProvider.HardCacheAge);
        var expired = reader.MoveNextAsync().AsTask();
        await provider.ExpireValidityOnceAsync();

        Assert.IsTrue(await expired.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("cache_expired", reader.Current.ErrorCode);
        Assert.AreEqual(ObservedAt, reader.Current.ObservedAt);
        pending.SetResult(Acquisition(json, owner));
        await slowPoll.WaitAsync(TimeSpan.FromSeconds(1));
        var currentAfterSlowPoll = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await currentAfterSlowPoll.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("cache_expired", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task FreshnessObserverCancelsPublicationBlockedByMetricBusBackpressure()
    {
        var time = new ManualTimeProvider(ObservedAt);
        var owner = Owner("synthetic-user-1", null);
        const string json = """
            { "userId": "synthetic-user-1", "userQuota": { "used": 5, "unit": "credits" } }
            """;
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(PersonalUsedId)).GetAsyncEnumerator();
        using var provider = CreateProvider(registry, bus, Acquisition(json, owner), owner, time);
        var initial = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        Assert.IsTrue(await initial.WaitAsync(TimeSpan.FromSeconds(1)));
        await provider.PublishCurrentAsync();
        var blockedPublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedPublication.IsCompleted);
        time.Advance(QoderQuotaProvider.FreshnessGrace);

        await provider.ExpireValidityOnceAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "A value already buffered before the freshness deadline cannot be retracted.");
        var stale = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await stale.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Stale, reader.Current.State);
        Assert.AreEqual(5d, reader.Current.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(ObservedAt, reader.Current.ObservedAt);
    }

    [TestMethod]
    public async Task UnexpectedWorkerFaultStillPublishesStoppedAndPropagates()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(PersonalUsedId)).GetAsyncEnumerator();
        using var provider = new QoderQuotaProvider(registry, bus,
            _ => Task.FromException<QoderQuotaAcquisition>(new InvalidOperationException("synthetic_worker_fault")),
            (_, _) => Task.FromResult(new QoderOwnerVerification(QoderOwnerVerificationState.Unverifiable, null)),
            TimeProvider.System);
        var stopped = reader.MoveNextAsync().AsTask();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            provider.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        Assert.AreEqual(MetricConfidence.Unknown, reader.Current.Confidence);
        Assert.IsNull(reader.Current.Value);
    }

    private static QoderQuotaProvider CreateProvider(MetricRegistry registry, MetricBus bus,
        QoderQuotaAcquisition acquisition, QoderOwnerKey owner, TimeProvider? timeProvider = null) =>
        new(registry, bus, _ => Task.FromResult(acquisition),
            (_, _) => Task.FromResult(new QoderOwnerVerification(QoderOwnerVerificationState.Verified, owner)),
            timeProvider ?? TimeProvider.System);

    private static QoderQuotaAcquisition Acquisition(string json, QoderOwnerKey owner) =>
        new(owner, QoderQuotaParser.Parse(Encoding.UTF8.GetBytes(json),
            new QoderSessionIdentity(owner.AccountId, owner.OrganizationId), ObservedAt), MetricState.Unavailable, null);

    private static QoderOwnerKey Owner(string accountId, string? organizationId) =>
        new(accountId, organizationId, 5172, 638950000000000000, 1, 638950000000000000,
            "synthetic-pipe-fingerprint");

    private static void AssertSnapshot(MetricSnapshot snapshot, double expected, DateTimeOffset observedAt)
    {
        Assert.AreEqual(MetricState.Fresh, snapshot.State);
        Assert.AreEqual(MetricConfidence.Measured, snapshot.Confidence);
        Assert.AreEqual(expected, snapshot.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(observedAt, snapshot.ObservedAt);
        Assert.AreEqual(QoderQuotaProvider.Source, snapshot.Source);
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
    }
}
