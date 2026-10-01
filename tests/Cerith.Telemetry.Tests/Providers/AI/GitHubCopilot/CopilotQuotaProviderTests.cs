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
        var hasQuotaDescriptor = context.Registry.GetSnapshot().Single(item => item.Id.Value == ChatHasQuotaId);
        Assert.AreEqual("quota_source_flag", hasQuotaDescriptor.SemanticType);
        StringAssert.Contains(hasQuotaDescriptor.Description!, "not an admission decision");

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
        var time = new TestTimeProvider(InitialUtc);
        var responses = new Queue<string>(AccountResponses(24680).Concat(AccountResponses(13579))
            .Concat(AccountResponses(13579)));
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateAcquirer(responses), time);

        var firstPending = ReadNextAsync(context.Bus, ChatPercentId);
        await provider.PollOnceAsync();
        Assert.AreEqual(MetricState.Fresh, (await firstPending).State);

        time.Advance(CopilotQuotaProvider.PollInterval);
        var clearPending = ReadNextAsync(context.Bus, ChatPercentId);
        await provider.PollOnceAsync();
        var cleared = await clearPending;
        Assert.AreEqual(MetricState.Unavailable, cleared.State);
        Assert.IsNull(cleared.Value);

        time.Advance(CopilotQuotaProvider.PollInterval);
        var nextPending = ReadNextAsync(context.Bus, ChatPercentId);
        await provider.PollOnceAsync();
        var next = await nextPending;
        Assert.AreEqual(MetricState.Fresh, next.State);
        Assert.AreEqual(80d, next.Value!.Value.AsDouble());
    }

    [TestMethod]
    public async Task SameAccountRateLimitPreservesOriginalObservationThenCacheExpiresBeforeRetry()
    {
        using var context = new RegistryAndBus();
        var time = new TestTimeProvider(InitialUtc);
        var requests = new List<CopilotGhRequest>();
        var script = new Queue<ScriptedResponse>();
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, InitialUtc.AddHours(1)));
        EnqueueRateLimitedRound(script, 24680, 1200);
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, InitialUtc.AddHours(1)));
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateScriptedAcquirer(script, requests), time);
        await using var reader = context.Bus.Subscribe(new MetricId(ChatPercentId)).GetAsyncEnumerator();

        var freshRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await freshRead);
        var fresh = reader.Current;
        Assert.AreEqual(MetricState.Fresh, fresh.State);

        time.Advance(CopilotQuotaProvider.PollInterval);
        var staleRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await staleRead);
        var stale = reader.Current;
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual(fresh.ObservedAt, stale.ObservedAt);
        Assert.AreEqual("rate_limited", stale.ErrorCode);

        time.Advance(TimeSpan.FromMinutes(14));
        var expiredRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await expiredRead);
        var expired = reader.Current;
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.AreEqual("cache_expired", expired.ErrorCode);
        Assert.AreEqual(8, requests.Count);

        time.Advance(TimeSpan.FromMinutes(6));
        var recoveredRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await recoveredRead);
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(12, requests.Count);
    }

    [TestMethod]
    public async Task ResetCrossingExpiresBucketCacheWithoutShorteningRetryAfter()
    {
        using var context = new RegistryAndBus();
        var time = new TestTimeProvider(InitialUtc);
        var requests = new List<CopilotGhRequest>();
        var script = new Queue<ScriptedResponse>();
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, InitialUtc.AddSeconds(90)));
        EnqueueRateLimitedRound(script, 24680, 1200);
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateScriptedAcquirer(script, requests), time);
        await using var percentReader = context.Bus.Subscribe(new MetricId(ChatPercentId)).GetAsyncEnumerator();
        await using var billingReader = context.Bus.Subscribe(new MetricId(BillingModeId)).GetAsyncEnumerator();

        var firstPercentRead = percentReader.MoveNextAsync().AsTask();
        var firstBillingRead = billingReader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await firstPercentRead);
        Assert.IsTrue(await firstBillingRead);
        time.Advance(CopilotQuotaProvider.PollInterval);
        var stalePercentRead = percentReader.MoveNextAsync().AsTask();
        var staleBillingRead = billingReader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await stalePercentRead);
        Assert.AreEqual(MetricState.Stale, percentReader.Current.State);
        Assert.IsTrue(await staleBillingRead);

        time.Advance(TimeSpan.FromSeconds(30));
        var resetPercentRead = percentReader.MoveNextAsync().AsTask();
        var resetBillingRead = billingReader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await resetPercentRead);
        Assert.AreEqual(MetricState.Unavailable, percentReader.Current.State);
        Assert.AreEqual("reset_elapsed", percentReader.Current.ErrorCode);
        Assert.IsTrue(await resetBillingRead);
        Assert.AreEqual(MetricState.Stale, billingReader.Current.State);
        Assert.AreEqual(8, requests.Count);
    }

    [TestMethod]
    public async Task ResetCrossingCancelsPendingStalePublicationBeforePublishingUnavailable()
    {
        using var context = new RegistryAndBus(subscriberCapacity: 1);
        var time = new TestTimeProvider(InitialUtc);
        var requests = new List<CopilotGhRequest>();
        var script = new Queue<ScriptedResponse>();
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, InitialUtc.AddSeconds(150)));
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, InitialUtc.AddSeconds(150)));
        EnqueueRateLimitedRound(script, 24680, 1200);
        var rateLimitRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateScriptedAcquirer(script, requests, () =>
            {
                if (requests.Count == 12) rateLimitRead.TrySetResult();
            }), time);
        await using var percentReader = context.Bus.Subscribe(new MetricId(ChatPercentId)).GetAsyncEnumerator();
        await using var billingReader = context.Bus.Subscribe(new MetricId(BillingModeId)).GetAsyncEnumerator();

        var initialPercent = percentReader.MoveNextAsync().AsTask();
        var initialBilling = billingReader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await initialPercent);
        Assert.IsTrue(await initialBilling);
        time.Advance(CopilotQuotaProvider.PollInterval);
        await provider.PollOnceAsync();

        time.Advance(CopilotQuotaProvider.PollInterval);
        var pendingStale = provider.PollOnceAsync();
        await rateLimitRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(pendingStale.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(30));
        var resetPoll = provider.PollOnceAsync();
        await pendingStale.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await billingReader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, billingReader.Current.State);
        Assert.IsTrue(await percentReader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, percentReader.Current.State);
        await resetPoll.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await percentReader.MoveNextAsync());
        Assert.AreEqual(MetricState.Unavailable, percentReader.Current.State);
        Assert.AreEqual("reset_elapsed", percentReader.Current.ErrorCode);
        Assert.AreEqual(12, requests.Count);
    }

    [TestMethod]
    public async Task AccountSwitchCancelsPendingPublicationFromPreviousScope()
    {
        using var context = new RegistryAndBus(subscriberCapacity: 1);
        var time = new TestTimeProvider(InitialUtc);
        var requests = new List<CopilotGhRequest>();
        var script = new Queue<ScriptedResponse>();
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680));
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680));
        EnqueueSuccessfulRound(script, 13579, QuotaPayload(13579, percentRemaining: 25));
        EnqueueSuccessfulRound(script, 13579, QuotaPayload(13579, percentRemaining: 25));
        var switchRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateScriptedAcquirer(script, requests, () =>
            {
                if (requests.Count == 12) switchRead.TrySetResult();
            }), time);
        await using var percentReader = context.Bus.Subscribe(new MetricId(ChatPercentId)).GetAsyncEnumerator();
        await using var billingReader = context.Bus.Subscribe(new MetricId(BillingModeId)).GetAsyncEnumerator();

        var initialPercent = percentReader.MoveNextAsync().AsTask();
        var initialBilling = billingReader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await initialPercent);
        Assert.IsTrue(await initialBilling);
        time.Advance(CopilotQuotaProvider.PollInterval);
        await provider.PollOnceAsync();

        time.Advance(CopilotQuotaProvider.PollInterval);
        var pendingSwitch = provider.PollOnceAsync();
        await switchRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(pendingSwitch.IsCompleted);

        time.Advance(CopilotQuotaProvider.PollInterval);
        var newAccountPoll = provider.PollOnceAsync();
        await pendingSwitch.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await billingReader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, billingReader.Current.State);
        Assert.IsTrue(await percentReader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, percentReader.Current.State);
        await newAccountPoll.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await percentReader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, percentReader.Current.State);
        Assert.AreEqual(25d, percentReader.Current.Value!.Value.AsDouble());
    }

    [TestMethod]
    public async Task RetryBackoffDoublesWithoutRetryAfterAndSuppressesEarlyRequests()
    {
        using var context = new RegistryAndBus();
        var time = new TestTimeProvider(InitialUtc);
        var requests = new List<CopilotGhRequest>();
        var script = new Queue<ScriptedResponse>([
            new(CopilotGhRequest.AuthStatus, "{\"state\":\"failure\"}"),
            new(CopilotGhRequest.AuthStatus, "{\"state\":\"failure\"}"),
            new(CopilotGhRequest.AuthStatus, "{\"state\":\"failure\"}")
        ]);
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateScriptedAcquirer(script, requests), time);

        await provider.PollOnceAsync();
        Assert.AreEqual(1, requests.Count);
        await provider.PollOnceAsync();
        Assert.AreEqual(1, requests.Count);

        time.Advance(CopilotQuotaProvider.RetryBaseDelay);
        await provider.PollOnceAsync();
        Assert.AreEqual(2, requests.Count);
        time.Advance(CopilotQuotaProvider.RetryBaseDelay);
        await provider.PollOnceAsync();
        Assert.AreEqual(2, requests.Count);
        time.Advance(CopilotQuotaProvider.RetryBaseDelay);
        await provider.PollOnceAsync();
        Assert.AreEqual(3, requests.Count);
    }

    [TestMethod]
    public async Task UnknownIdentityAfterOfflineQuotaRequestDoesNotReuseOldCache()
    {
        using var context = new RegistryAndBus();
        var time = new TestTimeProvider(InitialUtc);
        var requests = new List<CopilotGhRequest>();
        var script = new Queue<ScriptedResponse>();
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680));
        Enqueue(script, CopilotGhRequest.AuthStatus, "{\"state\":\"success\"}");
        Enqueue(script, CopilotGhRequest.AuthenticatedUser, "{\"id\":24680}");
        Enqueue(script, CopilotGhRequest.Quota, errorCode: "request_failed");
        Enqueue(script, CopilotGhRequest.AuthenticatedUser, errorCode: "identity_unavailable");
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus,
            CreateScriptedAcquirer(script, requests), time);
        await using var reader = context.Bus.Subscribe(new MetricId(ChatPercentId)).GetAsyncEnumerator();

        var freshRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await freshRead);
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        time.Advance(CopilotQuotaProvider.PollInterval);
        var unavailableRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await unavailableRead);
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(8, requests.Count);
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

    [TestMethod]
    public async Task RunObservesResetAndCancelsObsoletePublicationUnderBackpressure()
    {
        using var context = new RegistryAndBus(subscriberCapacity: 1);
        var time = new TestTimeProvider(InitialUtc);
        var resetAt = InitialUtc.AddSeconds(150);
        var script = new Queue<ScriptedResponse>();
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, resetAt, 80));
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, resetAt, 70));
        EnqueueRateLimitedRound(script, 24680, 600);
        var requests = new List<CopilotGhRequest>();
        var roundSignals = new[] { NewSignal(), NewSignal(), NewSignal() };
        var requestCount = 0;
        var acquirer = CreateScriptedAcquirer(script, requests, () =>
        {
            var count = Interlocked.Increment(ref requestCount);
            if (count % 4 == 0) roundSignals[(count / 4) - 1].TrySetResult();
        });
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus, acquirer, time);
        using var cancellation = new CancellationTokenSource();
        await using var reader = context.Bus.Subscribe(new MetricId(ChatPercentId)).GetAsyncEnumerator();

        var firstRead = reader.MoveNextAsync().AsTask();
        var run = provider.RunAsync(cancellation.Token);
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(80d, reader.Current.Value!.Value.AsDouble());

        time.Advance(CopilotQuotaProvider.PollInterval);
        await roundSignals[1].Task.WaitAsync(TimeSpan.FromSeconds(3));
        time.Advance(CopilotQuotaProvider.PollInterval);
        await roundSignals[2].Task.WaitAsync(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(70d, reader.Current.Value!.Value.AsDouble());
        Assert.AreEqual(InitialUtc.AddSeconds(60), reader.Current.ObservedAt);

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task RunOwnerTransitionCancelsPendingClearWhenSubscriberIsBackpressured()
    {
        using var context = new RegistryAndBus(subscriberCapacity: 1);
        var time = new TestTimeProvider(InitialUtc);
        var script = new Queue<ScriptedResponse>();
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, percentRemaining: 80));
        EnqueueSuccessfulRound(script, 24680, QuotaPayload(24680, percentRemaining: 70));
        EnqueueSuccessfulRound(script, 13579, QuotaPayload(13579, percentRemaining: 50));
        EnqueueSuccessfulRound(script, 13579, QuotaPayload(13579, percentRemaining: 40));
        var requests = new List<CopilotGhRequest>();
        var roundSignals = Enumerable.Range(0, 4).Select(_ => NewSignal()).ToArray();
        var requestCount = 0;
        var acquirer = CreateScriptedAcquirer(script, requests, () =>
        {
            var count = Interlocked.Increment(ref requestCount);
            if (count % 4 == 0) roundSignals[(count / 4) - 1].TrySetResult();
        });
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus, acquirer, time);
        using var cancellation = new CancellationTokenSource();
        await using var reader = context.Bus.Subscribe(new MetricId(ChatPercentId)).GetAsyncEnumerator();

        var firstRead = reader.MoveNextAsync().AsTask();
        var run = provider.RunAsync(cancellation.Token);
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(80d, reader.Current.Value!.Value.AsDouble());

        time.Advance(CopilotQuotaProvider.PollInterval);
        await roundSignals[1].Task.WaitAsync(TimeSpan.FromSeconds(3));
        time.Advance(CopilotQuotaProvider.PollInterval);
        await roundSignals[2].Task.WaitAsync(TimeSpan.FromSeconds(3));
        time.Advance(CopilotQuotaProvider.PollInterval);
        await roundSignals[3].Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(50);

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(70d, reader.Current.Value!.Value.AsDouble());
        Assert.AreEqual(InitialUtc.AddSeconds(60), reader.Current.ObservedAt);

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(40d, reader.Current.Value!.Value.AsDouble());
        Assert.AreEqual(InitialUtc.AddSeconds(180), reader.Current.ObservedAt);

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task RunCancelsAndDrainsAnInFlightAcquisitionBeforeStoppedPublication()
    {
        using var context = new RegistryAndBus();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquirer = new CopilotQuotaAcquirer(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus, acquirer,
            TimeProvider.System);
        var readers = context.Registry.GetSnapshot().Select(descriptor =>
            context.Bus.Subscribe(descriptor.Id).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var run = provider.RunAsync(cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue((await Task.WhenAll(pending)).All(result => result));
            Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Stopped &&
                reader.Current.Value is null && reader.Current.Confidence == MetricConfidence.Unknown));
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RunBoundsStoppedPublicationWhenAcquisitionFailureMeetsDisposedBus()
    {
        using var context = new RegistryAndBus();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquirer = new CopilotQuotaAcquirer(async (request, _) =>
        {
            entered.TrySetResult();
            await finishFailure.Task;
            Assert.AreEqual(CopilotGhRequest.AuthStatus, request);
            return Encoding.UTF8.GetBytes("{\"state\":\"failure\"}");
        });
        using var provider = new CopilotQuotaProvider(context.Registry, context.Bus, acquirer,
            TimeProvider.System);

        var run = provider.RunAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        context.Bus.Dispose();
        finishFailure.TrySetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await run.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static CopilotQuotaProvider CreateProvider(MetricRegistry registry, MetricBus bus, long accountId,
        TimeProvider? timeProvider = null)
    {
        var responses = new Queue<string>(AccountResponses(accountId));
        return new CopilotQuotaProvider(registry, bus, CreateAcquirer(responses),
            timeProvider ?? TimeProvider.System);
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

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CopilotQuotaAcquirer CreateScriptedAcquirer(Queue<ScriptedResponse> responses,
        List<CopilotGhRequest> requests, Action? requestObserved = null) =>
        new((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            requestObserved?.Invoke();
            if (responses.Count == 0)
                throw new InvalidOperationException("Synthetic response queue exhausted.");
            var response = responses.Dequeue();
            Assert.AreEqual(response.Request, request);
            if (response.ErrorCode is not null)
                throw new CopilotQuotaAcquisitionException(response.ErrorCode,
                    response.RetryAfterDelay, response.RetryAfterAt);
            return Task.FromResult(Encoding.UTF8.GetBytes(response.Payload!));
        });

    private static void EnqueueSuccessfulRound(Queue<ScriptedResponse> responses, long accountId,
        string quotaPayload)
    {
        Enqueue(responses, CopilotGhRequest.AuthStatus, "{\"state\":\"success\",\"tokenSource\":\"keyring\"}");
        Enqueue(responses, CopilotGhRequest.AuthenticatedUser,
            "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) + "}");
        Enqueue(responses, CopilotGhRequest.Quota, quotaPayload);
        Enqueue(responses, CopilotGhRequest.AuthenticatedUser,
            "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) + "}");
    }

    private static void EnqueueRateLimitedRound(Queue<ScriptedResponse> responses, long accountId,
        long retryAfterSeconds)
    {
        Enqueue(responses, CopilotGhRequest.AuthStatus, "{\"state\":\"success\"}");
        Enqueue(responses, CopilotGhRequest.AuthenticatedUser,
            "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) + "}");
        Enqueue(responses, CopilotGhRequest.Quota,
            "HTTP/2 429 Too Many Requests\r\nRetry-After: " + retryAfterSeconds.ToString(CultureInfo.InvariantCulture) +
            "\r\n\r\n{\"message\":\"synthetic\"}");
        Enqueue(responses, CopilotGhRequest.AuthenticatedUser,
            "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) + "}");
    }

    private static void Enqueue(Queue<ScriptedResponse> responses, CopilotGhRequest request,
        string? payload = null, string? errorCode = null, TimeSpan? retryAfterDelay = null,
        DateTimeOffset? retryAfterAt = null) =>
        responses.Enqueue(new ScriptedResponse(request, payload, errorCode, retryAfterDelay, retryAfterAt));

    private static async Task<MetricSnapshot> ReadNextAsync(MetricBus bus, string id)
    {
        await foreach (var snapshot in bus.Subscribe(new MetricId(id)))
            return snapshot;
        throw new InvalidOperationException("Metric stream completed before a snapshot was published.");
    }

    private static string QuotaPayload(long accountId, DateTimeOffset? resetAt = null,
        double percentRemaining = 80) =>
        "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) +
        ",\"token_based_billing\":true,\"quota_snapshots\":{\"chat\":{\"token_based_billing\":true,\"entitlement\":50,\"credits_used\":10,\"quota_remaining\":40,\"percent_remaining\":" +
        percentRemaining.ToString(CultureInfo.InvariantCulture) + ",\"quota_reset_at\":" +
        (resetAt?.ToUnixTimeSeconds() ?? 1893456000).ToString(CultureInfo.InvariantCulture) +
        ",\"unlimited\":false,\"has_quota\":false}}}";

    private const string InitialUtcText = "2026-10-01T00:00:00Z";
    private static readonly DateTimeOffset InitialUtc = DateTimeOffset.Parse(InitialUtcText,
        CultureInfo.InvariantCulture);

    private sealed class RegistryAndBus : IDisposable
    {
        internal MetricRegistry Registry { get; } = new();
        internal MetricBus Bus { get; }

        internal RegistryAndBus(int subscriberCapacity = 4) => Bus = new(subscriberCapacity);

        public void Dispose() => Bus.Dispose();
    }

    private sealed record ScriptedResponse(CopilotGhRequest Request, string? Payload = null,
        string? ErrorCode = null, TimeSpan? RetryAfterDelay = null, DateTimeOffset? RetryAfterAt = null);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, period, _timestamp);
                _timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
                due = _timers.Where(timer => timer.PrepareFire(_timestamp)).ToList();
                _timers.RemoveAll(timer => timer.IsDisposed);
            }

            foreach (var timer in due) timer.Invoke();
        }

        private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) return timer.Change(_timestamp, dueTime, period);
        }

        private void RemoveTimer(ManualTimer timer)
        {
            lock (_gate)
            {
                timer.MarkDisposed();
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(TestTimeProvider owner, TimerCallback callback,
            object? state, TimeSpan dueTime, TimeSpan period, long timestamp) : ITimer
        {
            private long _dueTimestamp = DueTimestamp(timestamp, dueTime);
            private long _periodTicks = PeriodTicks(period);

            internal bool IsDisposed { get; private set; }

            internal bool PrepareFire(long timestamp)
            {
                if (IsDisposed || timestamp < _dueTimestamp) return false;
                _dueTimestamp = _periodTicks > 0 ? timestamp + _periodTicks : long.MaxValue;
                return true;
            }

            internal void Invoke() => callback(state);

            internal bool Change(long timestamp, TimeSpan dueTime, TimeSpan period)
            {
                if (IsDisposed) return false;
                _dueTimestamp = DueTimestamp(timestamp, dueTime);
                _periodTicks = PeriodTicks(period);
                return true;
            }

            internal void MarkDisposed() => IsDisposed = true;

            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.ChangeTimer(this, dueTime, period);

            public void Dispose() => owner.RemoveTimer(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            private static long DueTimestamp(long timestamp, TimeSpan dueTime) =>
                dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : timestamp + Math.Max(0, dueTime.Ticks);

            private static long PeriodTicks(TimeSpan period) =>
                period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
        }
    }
}
