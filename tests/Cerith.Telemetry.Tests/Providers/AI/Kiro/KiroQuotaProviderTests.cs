#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
        Func<CancellationToken, Task<KiroAcquisition>> readUsage) =>
        new(registry, bus, readUsage, TimeProvider.System);

    private static SubscriptionBatch SubscribeAll(MetricBus bus) => new(bus);

    private static KiroAcquisition Ready(string json) =>
        KiroAcquisition.Ready(Encoding.UTF8.GetBytes(json));

    private static string CompleteUsage(string userId, string accountId) => $$"""
        {
          "userInfo":{"userId":"{{userId}}","accountId":"{{accountId}}","profileArn":"arn:aws:synthetic:profile/test"},
          "usageBreakdownList":[{"resourceType":"CREDIT","currentUsageWithPrecision":"12.75","usageLimitWithPrecision":"100.00"}],
          "subscriptionInfo":{"nextDateReset":1893456000},
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
