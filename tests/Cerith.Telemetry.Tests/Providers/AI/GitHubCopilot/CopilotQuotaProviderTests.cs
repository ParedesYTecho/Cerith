#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.GitHubCopilot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.GitHubCopilot;

[TestClass]
public sealed class CopilotQuotaProviderTests
{
    private const string BillingModeId = "ai.github_copilot.billing.token_based";
    private const string ChatPercentId = "ai.github_copilot.quota.chat.percent_remaining";
    private const string ChatResetId = "ai.github_copilot.quota.chat.reset_at";
    private const string ChatUnlimitedId = "ai.github_copilot.quota.chat.unlimited";
    private const string ChatHasQuotaId = "ai.github_copilot.quota.chat.has_quota";

    [TestMethod]
    public async Task PublishesOnlyFieldsWithVerifiedUnitsAndPreservesIndependentFlags()
    {
        using var context = new RegistryAndBus();
        using var provider = CreateProvider(context.Registry, context.Bus, 24680);
        var pending = new[] { BillingModeId, ChatPercentId, ChatResetId, ChatUnlimitedId, ChatHasQuotaId }
            .Select(id => ReadNextAsync(context.Bus, id)).ToArray();

        await provider.PollOnceAsync();
        var snapshots = await Task.WhenAll(pending);

        Assert.AreEqual(13, context.Registry.Count);
        Assert.IsFalse(context.Registry.GetSnapshot().Any(item =>
            item.Id.Value.EndsWith(".entitlement", StringComparison.Ordinal) ||
            item.Id.Value.EndsWith(".credits_used", StringComparison.Ordinal) ||
            item.Id.Value.EndsWith(".quota_remaining", StringComparison.Ordinal)));
        Assert.AreEqual("%", context.Registry.GetSnapshot().Single(item => item.Id.Value == ChatPercentId).Unit);
        Assert.AreEqual(MetricValueKind.DateTimeOffset,
            context.Registry.GetSnapshot().Single(item => item.Id.Value == ChatResetId).ValueKind);

        Assert.AreEqual(MetricState.Fresh, snapshots[0].State);
        Assert.IsTrue(snapshots[0].Value!.Value.AsBoolean());
        Assert.AreEqual(MetricState.Fresh, snapshots[1].State);
        Assert.AreEqual(80d, snapshots[1].Value!.Value.AsDouble());
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1893456000),
            snapshots[2].Value!.Value.AsDateTimeOffset());
        Assert.IsFalse(snapshots[3].Value!.Value.AsBoolean());
        Assert.IsFalse(snapshots[4].Value!.Value.AsBoolean());
        Assert.IsTrue(snapshots.All(item => item.Confidence == MetricConfidence.Measured));
    }

    [TestMethod]
    public async Task AccountSwitchBetweenQuotaAndAfterCheckPublishesUnavailableInsteadOfForeignValues()
    {
        using var context = new RegistryAndBus();
        var responses = new Queue<string>([
            "{\"state\":\"success\",\"tokenSource\":\"keyring\"}",
            "{\"id\":24680}",
            QuotaPayload(24680),
            "{\"id\":13579}"
        ]);
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateAcquirer(responses), TimeProvider.System);
        var pending = ReadNextAsync(context.Bus, ChatPercentId);

        await provider.PollOnceAsync();
        var snapshot = await pending;

        Assert.AreEqual(MetricState.Unavailable, snapshot.State);
        Assert.IsNull(snapshot.Value);
    }

    [TestMethod]
    public async Task ConfirmedAccountChangeClearsPreviousScopeAndNextPollUsesNewAccount()
    {
        using var context = new RegistryAndBus();
        var responses = new Queue<string>(AccountResponses(24680).Concat(AccountResponses(13579))
            .Concat(AccountResponses(13579)));
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateAcquirer(responses), TimeProvider.System);

        var firstPending = ReadNextAsync(context.Bus, ChatPercentId);
        await provider.PollOnceAsync();
        Assert.AreEqual(MetricState.Fresh, (await firstPending).State);

        var clearPending = ReadNextAsync(context.Bus, ChatPercentId);
        await provider.PollOnceAsync();
        var cleared = await clearPending;
        Assert.AreEqual(MetricState.Unavailable, cleared.State);
        Assert.IsNull(cleared.Value);

        var nextPending = ReadNextAsync(context.Bus, ChatPercentId);
        await provider.PollOnceAsync();
        var next = await nextPending;
        Assert.AreEqual(MetricState.Fresh, next.State);
        Assert.AreEqual(80d, next.Value!.Value.AsDouble());
    }

    [TestMethod]
    public async Task RunPublishesFreshSnapshotThenStoppedStateOnCancellation()
    {
        using var context = new RegistryAndBus();
        using var provider = CreateProvider(context.Registry, context.Bus, 24680);
        using var cancellation = new CancellationTokenSource();
        var firstSnapshot = ReadNextAsync(context.Bus, BillingModeId);
        var run = provider.RunAsync(cancellation.Token);

        var first = await firstSnapshot.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(MetricState.Fresh, first.State);
        var stoppedSnapshot = ReadNextAsync(context.Bus, BillingModeId);
        cancellation.Cancel();
        var stopped = await stoppedSnapshot.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(MetricState.Stopped, stopped.State);
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static CopilotQuotaProvider CreateProvider(MetricRegistry registry, MetricBus bus, long accountId)
    {
        var responses = new Queue<string>(AccountResponses(accountId));
        return new CopilotQuotaProvider(registry, bus, CreateAcquirer(responses), TimeProvider.System);
    }

    private static IEnumerable<string> AccountResponses(long accountId) =>
    [
        "{\"state\":\"success\",\"tokenSource\":\"keyring\"}",
        "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) + "}",
        QuotaPayload(accountId),
        "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) + "}"
    ];

    private static CopilotQuotaAcquirer CreateAcquirer(Queue<string> responses) =>
        new((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (responses.Count == 0) throw new InvalidOperationException("Synthetic response queue exhausted.");
            return Task.FromResult(Encoding.UTF8.GetBytes(responses.Dequeue()));
        });

    private static async Task<MetricSnapshot> ReadNextAsync(MetricBus bus, string id)
    {
        await foreach (var snapshot in bus.Subscribe(new MetricId(id)))
            return snapshot;
        throw new InvalidOperationException("Metric stream completed before a snapshot was published.");
    }

    private static string QuotaPayload(long accountId) =>
        "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) +
        ",\"token_based_billing\":true,\"quota_snapshots\":{\"chat\":{\"token_based_billing\":true,\"entitlement\":50,\"credits_used\":10,\"quota_remaining\":40,\"percent_remaining\":80,\"quota_reset_at\":1893456000,\"unlimited\":false,\"has_quota\":false}}}";

    private sealed class RegistryAndBus : IDisposable
    {
        internal MetricRegistry Registry { get; } = new();
        internal MetricBus Bus { get; } = new(subscriberCapacity: 4);

        public void Dispose() => Bus.Dispose();
    }
}
