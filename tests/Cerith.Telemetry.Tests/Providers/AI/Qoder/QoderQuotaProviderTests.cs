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
        AssertSnapshot(organizationReader.Current, 8d, ObservedAt);
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
        QoderQuotaAcquisition acquisition, QoderOwnerKey owner) =>
        new(registry, bus, _ => Task.FromResult(acquisition),
            (_, _) => Task.FromResult(new QoderOwnerVerification(QoderOwnerVerificationState.Verified, owner)),
            TimeProvider.System);

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
}
