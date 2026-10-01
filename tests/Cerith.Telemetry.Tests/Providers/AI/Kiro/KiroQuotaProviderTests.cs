#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Kiro;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Kiro;

[TestClass]
public sealed class KiroQuotaProviderTests
{
    private static readonly byte[] IdentityKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ParserKeepsIncludedAddonAndBonusCreditsSeparate()
    {
        var parsed = Parse(CompleteUsage("synthetic-user-a", "synthetic-account-a"));

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Fresh, parsed.Reading.IncludedRemaining.State);
        Assert.AreEqual(87.25m, Decimal(parsed.Reading.IncludedRemaining));
        Assert.AreEqual(MetricState.Fresh, parsed.Reading.IncludedReset.State);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_893_456_000),
            parsed.Reading.IncludedReset.Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(29.75m, Decimal(parsed.Reading.AddOnRemaining));
        Assert.AreEqual(8.75m, Decimal(parsed.Reading.BonusRemaining));
        Assert.AreEqual(2, parsed.Reading.BonusAllocations!.Length);
    }

    [TestMethod]
    public void ParserRejectsAmbiguousIncludedCreditPools()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"usageLimitWithPrecision\":\"100.00\"}",
                "\"usageLimitWithPrecision\":\"100.00\"},{\"resourceType\":\"CREDIT\",\"currentUsage\":0,\"usageLimit\":100}");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Error, parsed.Reading.IncludedRemaining.State);
        Assert.AreEqual("quota_ambiguous", parsed.Reading.IncludedRemaining.ErrorCode);
    }

    [TestMethod]
    public void ParserKeepsMissingBucketsUnavailableInsteadOfInventingZero()
    {
        const string json = """
            {"userInfo":{"userId":"synthetic-user-a"},"usageBreakdownList":[]}
            """;

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.IncludedRemaining.State);
        Assert.IsNull(parsed.Reading.IncludedRemaining.Value);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.IncludedReset.State);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.AddOnRemaining.State);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.BonusRemaining.State);
    }

    [TestMethod]
    public void ParserMarksMalformedCreditAsErrorRatherThanZero()
    {
        const string json = """
            {"user_info":{"user_id":"synthetic-user-a"},"usage_breakdown_list":[
              {"resource_type":"CREDIT","current_usage_with_precision":"not-a-number","usage_limit_with_precision":"100"}]}
            """;

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Error, parsed.Reading.IncludedRemaining.State);
        Assert.AreEqual("invalid_quota", parsed.Reading.IncludedRemaining.ErrorCode);
    }

    [TestMethod]
    public void ParserRejectsOverLimitIncludedCreditsWithoutClamping()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"currentUsageWithPrecision\":\"12.75\"",
                "\"currentUsageWithPrecision\":\"100.01\"");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.IncludedRemaining, "invalid_quota");
    }

    [TestMethod]
    public void ParserRejectsOverLimitAddOnCreditsWithoutClamping()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"addOnTotalUsage\":\"10.25\"", "\"addOnTotalUsage\":\"40.01\"");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.AddOnRemaining, "invalid_addon");
    }

    [TestMethod]
    public void ParserRejectsOverLimitBonusCreditsWithoutClamping()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"currentUsage\":\"1.25\"", "\"currentUsage\":\"10.01\"");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.BonusRemaining, "invalid_bonus");
        Assert.IsNull(parsed.Reading.BonusAllocations);
    }

    [TestMethod]
    public void ParserMarksMalformedAddOnAndBonusCreditsAsValuelessErrors()
    {
        var addOn = Parse(CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"addOnTotalUsage\":\"10.25\"", "\"addOnTotalUsage\":\"unknown\""));
        var bonus = Parse(CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"currentUsage\":\"1.25\"", "\"currentUsage\":\"unknown\""));

        Assert.IsNotNull(addOn.Reading);
        Assert.IsNotNull(bonus.Reading);
        AssertValuelessError(addOn.Reading.AddOnRemaining, "invalid_addon");
        AssertValuelessError(bonus.Reading.BonusRemaining, "invalid_bonus");
        Assert.IsNull(bonus.Reading.BonusAllocations);
    }

    [TestMethod]
    public void ParserRejectsBonusListsAboveTheFixedAllocationBound()
    {
        var allocations = string.Join(",", Enumerable.Repeat(
            "{\"currentUsage\":\"0\",\"usageLimit\":\"1\"}",
            KiroQuotaParser.MaximumBonusAllocations + 1));
        var parsed = Parse(string.Concat(
            "{\"userInfo\":{\"userId\":\"synthetic-user-a\"},\"freeTrialInfo\":{\"bonuses\":[",
            allocations, "]}}"));

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.BonusRemaining, "bonus_limit_exceeded");
        Assert.IsNull(parsed.Reading.BonusAllocations);
    }

    [TestMethod]
    public void AcpUsageDispatchRequiresAdvertisedTypedExtension()
    {
        using var supported = JsonDocument.Parse("""
            {"agentCapabilities":{"_meta":{"kiro":{"extensionMethods":["_kiro/account/getUsage"]}}}}
            """);
        using var unsupported = JsonDocument.Parse("""
            {"agentCapabilities":{"_meta":{"kiro":{"extensionMethods":["_kiro/session/context"]}}}}
            """);
        using var empty = JsonDocument.Parse("{}");

        Assert.IsTrue(KiroAcpUsageClient.AdvertisesUsageMethod(supported.RootElement));
        Assert.IsFalse(KiroAcpUsageClient.AdvertisesUsageMethod(unsupported.RootElement));
        Assert.IsFalse(KiroAcpUsageClient.AdvertisesUsageMethod(empty.RootElement));
    }

    [TestMethod]
    public void AcpStartInfoUsesOfficialCliAuthAndDoesNotForwardApiKey()
    {
        var startInfo = KiroAcpUsageClient.CreateStartInfo();

        CollectionAssert.AreEqual(new[] { "acp", "--agent-engine=v3", "--auth-method=cli" },
            startInfo.ArgumentList.ToArray());
        Assert.AreEqual(Path.GetTempPath(), startInfo.WorkingDirectory);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.AreEqual("error", startInfo.Environment["KIRO_LOG_LEVEL"]);
        Assert.IsTrue(startInfo.Environment.TryGetValue("KIRO_API_KEY", out var apiKey));
        Assert.IsNull(apiKey);
    }

    [TestMethod]
    public async Task AcpClientSendsInitializeAndUsageOnlyWithoutPrompting()
    {
        const string script = """
            $initialize = [Console]::In.ReadLine() | ConvertFrom-Json
            if ($initialize.method -ne 'initialize') { exit 21 }
            [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"result":{"agentCapabilities":{"_meta":{"kiro":{"extensionMethods":["_kiro/account/getUsage"]}}}}}')
            $usage = [Console]::In.ReadLine() | ConvertFrom-Json
            if ($usage.method -ne '_kiro/account/getUsage') { exit 22 }
            [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":2,"result":{"synthetic":true}}')
            """;
        var acquisition = await KiroAcpUsageClient.ReadAsync(TimeProvider.System, CancellationToken.None,
            CreatePowerShellStartInfo(script), TimeSpan.FromSeconds(5));

        Assert.AreEqual(KiroAcquisitionStatus.Ready, acquisition.Status);
        Assert.AreEqual("{\"synthetic\":true}", Encoding.UTF8.GetString(acquisition.Payload!));
    }

    [TestMethod]
    public async Task AcpClientMapsMissingCliToUnavailableWithoutStartingAnInference()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), $"missing-kiro-{Guid.NewGuid():N}.exe"));

        var acquisition = await KiroAcpUsageClient.ReadAsync(TimeProvider.System, CancellationToken.None,
            startInfo, TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(KiroAcquisitionStatus.Unavailable, acquisition.Status);
        Assert.AreEqual("cli_unavailable", acquisition.ErrorCode);
    }

    [TestMethod]
    public async Task AcpClientTimesOutAndStopsAStalledSyntheticChild()
    {
        const string script = """
            $null = [Console]::In.ReadLine()
            Start-Sleep -Seconds 30
            """;
        var started = TimeProvider.System.GetTimestamp();

        var acquisition = await KiroAcpUsageClient.ReadAsync(TimeProvider.System, CancellationToken.None,
            CreatePowerShellStartInfo(script), TimeSpan.FromMilliseconds(150));
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.AreEqual(KiroAcquisitionStatus.Error, acquisition.Status);
        Assert.AreEqual("request_timeout", acquisition.ErrorCode);
        Assert.IsTrue(elapsed < TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AcpLineReaderRejectsOversizedResponseLine()
    {
        var bytes = new byte[KiroAcpUsageClient.MaxResponseBytes + 2];
        Array.Fill(bytes, (byte)'x');
        bytes[^1] = (byte)'\n';
        var reader = new KiroAcpUsageClient.BoundedLineReader(new MemoryStream(bytes),
            KiroAcpUsageClient.MaxResponseBytes);

        var rejected = false;
        try { _ = await reader.ReadLineAsync(CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        Assert.IsTrue(rejected);
    }

    [TestMethod]
    public async Task ProviderPublishesTypedMeasuredValuesAndUsesCreditUnits()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))));
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(4, snapshots.Length);
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.State == MetricState.Fresh));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.Confidence == MetricConfidence.Measured));
        Assert.AreEqual(87.25m, Decimal(snapshots[0]));
        Assert.AreEqual(29.75m, Decimal(snapshots[2]));
        Assert.AreEqual(8.75m, Decimal(snapshots[3]));
        Assert.AreEqual("credits", registry.GetSnapshot().Single(descriptor =>
            descriptor.Id.Value == "ai.kiro.credits.included.remaining").Unit);
        Assert.AreEqual(MetricValueKind.DateTimeOffset, registry.GetSnapshot().Single(descriptor =>
            descriptor.Id.Value == "ai.kiro.credits.included.reset").ValueKind);
    }

    [TestMethod]
    public async Task ProviderClearsCachedValuesWhenOfficialUsageIdentityChanges()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var call = 0;
        using var provider = CreateProvider(registry, bus, _ =>
        {
            var payload = Interlocked.Increment(ref call) == 1
                ? CompleteUsage("synthetic-user-a", "synthetic-account-a")
                : """{"userInfo":{"userId":"synthetic-user-b"},"usageBreakdownList":[]}""";
            return Task.FromResult(Ready(payload));
        });
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var first = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Fresh, first[0].State);

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var afterIdentityChange = await readers.ReadNextBatchAsync();

        Assert.AreEqual(MetricState.Unavailable, afterIdentityChange[0].State);
        Assert.IsNull(afterIdentityChange[0].Value);
        Assert.AreEqual(MetricState.Unavailable, afterIdentityChange[2].State);
        Assert.AreEqual(MetricState.Unavailable, afterIdentityChange[3].State);
    }

    [TestMethod]
    public async Task ProviderPublishesCliUnavailableWithoutFabricatingValues()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(KiroAcquisition.Unavailable("cli_unavailable")));
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(4, snapshots.Length);
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.State == MetricState.Unavailable));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.Value is null));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.ErrorCode == "cli_unavailable"));
    }

    [TestMethod]
    public async Task RunCancelsPendingAcquisitionAndPublishesStoppedState()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(registry, bus, async cancellationToken =>
        {
            readStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return KiroAcquisition.Error("unreachable");
        });
        using var stop = new CancellationTokenSource();
        await using var readers = SubscribeAll(bus);
        var stoppedReaders = readers.ReadUntilStoppedAsync(
            Enumerable.Range(0, 4).Select(static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray());
        var run = provider.RunAsync(stop.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();

        var stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(2));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    [TestMethod]
    public async Task ProviderPreservesOriginalObservationWhileStaleThenExpiresMonotonicCache()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt);
        var calls = 0;
        using var provider = CreateProvider(registry, bus, _ => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))
                : KiroAcquisition.Error("request_timeout")), time);
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var fresh = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Fresh, fresh[0].State);

        time.Advance(KiroQuotaProvider.StaleAfter);
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var stale = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Stale, stale[0].State);
        Assert.AreEqual(87.25m, Decimal(stale[0]));
        Assert.AreEqual(ObservedAt, stale[0].ObservedAt);
        Assert.AreEqual("request_timeout", stale[0].ErrorCode);

        time.Advance(KiroQuotaProvider.MaximumCacheAge - KiroQuotaProvider.StaleAfter);
        await provider.PublishOnceAsync();
        var expired = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Unavailable, expired[0].State);
        Assert.IsNull(expired[0].Value);
        Assert.AreEqual(ObservedAt, expired[0].ObservedAt);
        Assert.AreEqual("cache_expired", expired[0].ErrorCode);
    }

    [TestMethod]
    public async Task ProviderInvalidatesIncludedCreditsAtPlanResetCrossing()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt);
        var resetAt = ObservedAt.AddMinutes(1);
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a",
                resetAt.ToUnixTimeSeconds()))), time);
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        _ = await readers.ReadNextBatchAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await provider.PublishOnceAsync();
        var reset = await readers.ReadNextBatchAsync();

        Assert.AreEqual(MetricState.Unavailable, reset[0].State);
        Assert.AreEqual("reset_elapsed", reset[0].ErrorCode);
        Assert.AreEqual(ObservedAt, reset[0].ObservedAt);
        Assert.IsNull(reset[0].Value);
        Assert.AreEqual(MetricState.Unavailable, reset[1].State);
        Assert.AreEqual("reset_elapsed", reset[1].ErrorCode);
        Assert.AreEqual(MetricState.Fresh, reset[2].State);
    }

    [TestMethod]
    public async Task ProviderRecalculatesBonusCreditsAtAllocationExpiry()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt);
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("2030-01-02T00:00:00Z", "2026-10-01T08:01:00Z");
        using var provider = CreateProvider(registry, bus, _ => Task.FromResult(Ready(json)), time);
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        _ = await readers.ReadNextBatchAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await provider.PublishOnceAsync();
        var expired = await readers.ReadNextBatchAsync();

        Assert.AreEqual(MetricState.Stale, expired[3].State);
        Assert.AreEqual(0m, Decimal(expired[3]));
        Assert.AreEqual(ObservedAt, expired[3].ObservedAt);
    }

    [TestMethod]
    public async Task ProviderSerializesLateAcquisitionsAndPublishesTheLatestIdentity()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var provider = CreateProvider(registry, bus, async cancellationToken =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"));
            }
            return Ready(CompleteUsage("synthetic-user-b", "synthetic-account-b")
                .Replace("\"currentUsageWithPrecision\":\"12.75\"",
                    "\"currentUsageWithPrecision\":\"50.00\""));
        });
        await using var readers = SubscribeAll(bus);
        readers.Start();

        var first = provider.PollOnceAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = provider.PollOnceAsync();
        releaseFirst.TrySetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(2, calls);
        Assert.AreEqual(50m, Decimal(snapshots[0]));
    }

    [TestMethod]
    public async Task ProviderCancelsPendingPublicationWhenIdentityChanges()
    {
        const string includedId = "ai.kiro.credits.included.remaining";
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        var calls = 0;
        using var provider = CreateProvider(registry, bus, _ => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))
                : Ready(CompleteUsage("synthetic-user-b", "synthetic-account-b")
                    .Replace("\"currentUsageWithPrecision\":\"12.75\"",
                        "\"currentUsageWithPrecision\":\"50.00\""))));
        using var stop = new CancellationTokenSource();
        await using var reader = bus.Subscribe(new MetricId(includedId), stop.Token).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(new MetricSnapshot(new MetricId(includedId), null, ObservedAt,
            MetricState.Unavailable, MetricConfidence.Unknown, "test"));
        await initialRead.WaitAsync(TimeSpan.FromSeconds(2));

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var pendingPublication = provider.PublishOnceAsync();
        await WaitForActivePublicationAsync(provider, pendingPublication);
        await provider.PollOnceAsync();
        await pendingPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(87.25m, Decimal(reader.Current));
        var publishNewIdentity = provider.PublishOnceAsync();
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await publishNewIdentity;
        Assert.AreEqual(50m, Decimal(reader.Current));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task RunBoundsStoppedPublicationWhenSubscriberQueueIsFull()
    {
        const string includedId = "ai.kiro.credits.included.remaining";
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))));
        using var stop = new CancellationTokenSource();
        using var subscriptionStop = new CancellationTokenSource();
        await using var reader = bus.Subscribe(new MetricId(includedId), subscriptionStop.Token).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(new MetricSnapshot(new MetricId(includedId), null, ObservedAt,
            MetricState.Unavailable, MetricConfidence.Unknown, "test"));
        await initialRead.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();

        var run = provider.RunAsync(stop.Token);
        await Task.Delay(25);
        var started = TimeProvider.System.GetTimestamp();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.IsTrue(elapsed < TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RunPublishesStoppedSnapshotsAfterWorkerFaults()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt) { ThrowOnGetTimestamp = true };
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))), time);
        await using var readers = SubscribeAll(bus);
        var stoppedReaders = readers.ReadUntilStoppedAsync(
            Enumerable.Range(0, 4).Select(static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray());
        var run = provider.RunAsync(CancellationToken.None);

        var stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(3));
        InvalidOperationException? failure = null;
        try { await run; }
        catch (InvalidOperationException exception) { failure = exception; }

        Assert.IsNotNull(failure);
        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    [TestMethod]
    public async Task RunPublishesStoppedSnapshotsAfterCancellation()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))));
        using var stop = new CancellationTokenSource();
        await using var readers = SubscribeAll(bus);
        readers.Start();
        var freshSeen = Enumerable.Range(0, 4)
            .Select(static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var stoppedReaders = readers.ReadUntilStoppedAsync(freshSeen);
        var run = provider.RunAsync(stop.Token);

        MetricSnapshot[] stopped;
        try
        {
            await Task.WhenAll(freshSeen.Select(static signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(5));
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    private static KiroParseResult Parse(string json) => KiroQuotaParser.Parse(
        Encoding.UTF8.GetBytes(json), IdentityKey, ObservedAt);

    private static KiroQuotaProvider CreateProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<KiroAcquisition>> readUsage, TimeProvider? timeProvider = null) =>
        new(registry, bus, readUsage, timeProvider ?? TimeProvider.System);

    private static SubscriptionBatch SubscribeAll(MetricBus bus) => new(bus);

    private static KiroAcquisition Ready(string json) =>
        KiroAcquisition.Ready(Encoding.UTF8.GetBytes(json));

    private static string CompleteUsage(string userId, string accountId, long resetAt = 1893456000) => $$"""
        {
          "userInfo":{"userId":"{{userId}}","accountId":"{{accountId}}","profileArn":"arn:aws:synthetic:profile/test"},
          "usageBreakdownList":[{"resourceType":"CREDIT","currentUsageWithPrecision":"12.75","usageLimitWithPrecision":"100.00"}],
          "subscriptionInfo":{"nextDateReset":{{resetAt}}},
          "addOnMetadata":{"addOnTotalLimit":"40.00","addOnTotalUsage":"10.25"},
          "freeTrialInfo":{"bonuses":[
            {"currentUsage":"1.25","usageLimit":"10.00","expiresAt":"2030-01-02T00:00:00Z"},
            {"currentUsage":"2","usageLimit":"3","expiresAt":"2020-01-01T00:00:00Z"}
          ]}
        }
        """;

    private static decimal Decimal(KiroMetricPoint point)
    {
        Assert.IsTrue(point.Value.HasValue);
        return point.Value!.Value.AsDecimal();
    }

    private static decimal Decimal(MetricSnapshot snapshot)
    {
        Assert.IsTrue(snapshot.Value.HasValue);
        return snapshot.Value!.Value.AsDecimal();
    }

    private static void AssertValuelessError(KiroMetricPoint point, string errorCode)
    {
        Assert.AreEqual(MetricState.Error, point.State);
        Assert.IsNull(point.Value);
        Assert.AreEqual(errorCode, point.ErrorCode);
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string script)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var startInfo = new ProcessStartInfo(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0",
            "powershell.exe"));
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        startInfo.Environment.Clear();
        startInfo.Environment["SystemRoot"] = windows;
        startInfo.Environment["WINDIR"] = windows;
        startInfo.Environment["TEMP"] = Path.GetTempPath();
        startInfo.Environment["TMP"] = Path.GetTempPath();
        startInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return startInfo;
    }

    private static async Task WaitForActivePublicationAsync(KiroQuotaProvider provider, Task publication)
    {
        var field = typeof(KiroQuotaProvider).GetField("_activePublicationStop",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var started = TimeProvider.System.GetTimestamp();
        while (field.GetValue(provider) is null && !publication.IsCompleted &&
            TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            await Task.Delay(1);
        Assert.IsNotNull(field.GetValue(provider));
        Assert.IsFalse(publication.IsCompleted);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public bool ThrowOnGetTimestamp { get; init; }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => ThrowOnGetTimestamp
            ? throw new InvalidOperationException("Synthetic time source failure.")
            : _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(callback, state, dueTime, period);

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }

    private sealed class SubscriptionBatch : IAsyncDisposable
    {
        private static readonly string[] Ids =
        [
            "ai.kiro.credits.included.remaining",
            "ai.kiro.credits.included.reset",
            "ai.kiro.credits.addon.remaining",
            "ai.kiro.credits.bonus.remaining"
        ];

        private readonly IAsyncEnumerator<MetricSnapshot>[] _readers;
        private readonly CancellationTokenSource _stop = new();
        private Task<bool>[]? _pendingMoves;
        private Task<MetricSnapshot[]>? _readUntilStopped;

        public SubscriptionBatch(MetricBus bus)
        {
            _readers = Ids.Select(id => bus.Subscribe(new MetricId(id), _stop.Token).GetAsyncEnumerator()).ToArray();
        }

        public void Start() => _pendingMoves ??= _readers.Select(static reader => reader.MoveNextAsync().AsTask()).ToArray();

        public async Task<MetricSnapshot[]> ReadNextBatchAsync()
        {
            Start();
            var moved = await Task.WhenAll(_pendingMoves!).ConfigureAwait(false);
            Assert.IsTrue(moved.All(static hasValue => hasValue));
            var snapshots = _readers.Select(static reader => reader.Current).ToArray();
            _pendingMoves = null;
            return snapshots;
        }

        public Task<MetricSnapshot[]> ReadUntilStoppedAsync(TaskCompletionSource<bool>[] freshSeen)
        {
            Start();
            _readUntilStopped = ReadUntilStoppedCoreAsync(freshSeen);
            return _readUntilStopped;
        }

        private async Task<MetricSnapshot[]> ReadUntilStoppedCoreAsync(TaskCompletionSource<bool>[] freshSeen)
        {
            var tasks = _readers.Select(async (reader, index) =>
            {
                var move = _pendingMoves![index];
                while (await move.ConfigureAwait(false))
                {
                    var snapshot = reader.Current;
                    if (snapshot.State == MetricState.Fresh) freshSeen[index].TrySetResult(true);
                    if (snapshot.State == MetricState.Stopped) return snapshot;
                    move = reader.MoveNextAsync().AsTask();
                }
                throw new AssertFailedException("Metric subscription ended before Stopped was published.");
            }).ToArray();
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_readUntilStopped is not null)
                await IgnoreReadTerminationAsync(_readUntilStopped).ConfigureAwait(false);
            if (_pendingMoves is not null)
                await IgnoreReadTerminationAsync(Task.WhenAll(_pendingMoves)).ConfigureAwait(false);
            foreach (var reader in _readers) await reader.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }

        private static async Task IgnoreReadTerminationAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (AssertFailedException) { return; }
        }
    }
}
