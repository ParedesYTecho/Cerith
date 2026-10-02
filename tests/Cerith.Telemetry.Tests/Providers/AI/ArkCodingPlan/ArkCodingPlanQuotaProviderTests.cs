#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class ArkCodingPlanQuotaProviderTests
{
    private const string SessionUsedId = "ai.ark-coding-plan.quota.session.used";
    private const string IdentityA = """
        {"logged_in":true,"auth_method":"sso","account_id":"210000","user_id":"12345","trn":"trn:iam::210000:user/alice","is_root":false}
        """;
    private const string IdentityB = """
        {"logged_in":true,"auth_method":"sso","account_id":"999999","user_id":"12345","trn":"trn:iam::999999:user/alice","is_root":false}
        """;
    private static readonly DateTimeOffset InitialUtc = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RegistersOnlyUsedPercentAndSourceResetMetrics()
    {
        using var fixture = new Fixture();
        var descriptors = fixture.Registry.GetSnapshot().OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(new[]
        {
            "ai.ark-coding-plan.quota.monthly.reset",
            "ai.ark-coding-plan.quota.monthly.used",
            "ai.ark-coding-plan.quota.session.reset",
            "ai.ark-coding-plan.quota.session.used",
            "ai.ark-coding-plan.quota.weekly.reset",
            "ai.ark-coding-plan.quota.weekly.used"
        }, descriptors.Select(item => item.Id.Value).ToArray());
        Assert.IsTrue(descriptors.Where(item => item.SemanticType == "quota_used")
            .All(item => item.Unit == "%" && item.ValueKind == MetricValueKind.Double));
        Assert.IsTrue(descriptors.Where(item => item.SemanticType == "timestamp")
            .All(item => item.Unit is null && item.ValueKind == MetricValueKind.DateTimeOffset));
    }

    [TestMethod]
    public async Task FailedRefreshRetainsOriginalObservationTimeAsStale()
    {
        using var fixture = new Fixture();
        fixture.EnqueueIdentity(IdentityA);
        fixture.EnqueuePlan(UsageJson(InitialUtc.AddMinutes(5)));
        fixture.EnqueueIdentity(IdentityA);
        await fixture.Provider.PollOnceAsync();

        fixture.Time.Advance(ArkCodingPlanQuotaProvider.PollInterval);
        fixture.EnqueueIdentity(IdentityA);
        fixture.EnqueueFailure("process_failed");
        await fixture.Provider.PollOnceAsync();

        var snapshot = await fixture.ReadMetricAsync(SessionUsedId);
        Assert.AreEqual(MetricState.Stale, snapshot.State);
        Assert.AreEqual(InitialUtc, snapshot.ObservedAt);
        Assert.AreEqual(50d, snapshot.Value!.Value.AsDouble());
        Assert.AreEqual("process_failed", snapshot.ErrorCode);
    }

    [TestMethod]
    public async Task IdentityTransitionCancelsBackpressuredOldOwnerPublication()
    {
        using var fixture = new Fixture();
        fixture.EnqueueIdentity(IdentityA);
        fixture.EnqueuePlan(UsageJson(InitialUtc.AddMinutes(5)));
        fixture.EnqueueIdentity(IdentityA);
        await fixture.Provider.PollOnceAsync();

        await using var reader = fixture.Bus.Subscribe(new MetricId(SessionUsedId)).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await fixture.Provider.PublishCurrentAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await fixture.Provider.PublishCurrentAsync();
        var blockedPublication = fixture.Provider.PublishCurrentAsync().AsTask();
        await WaitUntilBlockedAsync(blockedPublication);

        fixture.EnqueueIdentity(IdentityB);
        await fixture.Provider.ObserveIdentityOnceAsync();
        await AssertCanceledAsync(blockedPublication);

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "This queue item was published before the owner transition was observed.");

        var changedRead = reader.MoveNextAsync().AsTask();
        await fixture.Provider.PublishCurrentAsync();
        Assert.IsTrue(await changedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("owner_changed", reader.Current.ErrorCode);
        Assert.IsNull(reader.Current.Value);
    }

    [TestMethod]
    public async Task KnownResetExpiresMonotonicallyAndCancelsPublicationWhileAcquisitionIsPending()
    {
        using var fixture = new Fixture();
        fixture.EnqueueIdentity(IdentityA);
        fixture.EnqueuePlan(UsageJson(InitialUtc.AddMinutes(5)));
        fixture.EnqueueIdentity(IdentityA);
        await fixture.Provider.PollOnceAsync();

        await using var reader = fixture.Bus.Subscribe(new MetricId(SessionUsedId)).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await fixture.Provider.PublishCurrentAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        await fixture.Provider.PublishCurrentAsync();

        fixture.Time.Advance(ArkCodingPlanQuotaProvider.PollInterval);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.EnqueuePending(entered);
        using var pollStop = new CancellationTokenSource();
        var pendingPoll = fixture.Provider.PollOnceAsync(pollStop.Token);
        Assert.IsTrue(await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        var pendingPublication = fixture.Provider.PublishCurrentAsync().AsTask();
        await WaitUntilBlockedAsync(pendingPublication);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.Provider.MaintainStateAsync();

        await AssertCanceledAsync(pendingPublication);
        Assert.IsFalse(pendingPoll.IsCompleted,
            "Reset expiry is evaluated independently from the still-pending CLI acquisition.");
        pollStop.Cancel();
        await AssertCanceledAsync(pendingPoll);
    }

    [TestMethod]
    public async Task ClockRollbackDoesNotExtendKnownMonotonicResetDeadline()
    {
        using var fixture = new Fixture();
        fixture.EnqueueIdentity(IdentityA);
        fixture.EnqueuePlan(UsageJson(InitialUtc.AddMinutes(5)));
        fixture.EnqueueIdentity(IdentityA);
        await fixture.Provider.PollOnceAsync();

        await using var reader = fixture.Bus.Subscribe(new MetricId(SessionUsedId)).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await fixture.Provider.PublishCurrentAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        await fixture.Provider.PublishCurrentAsync();
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        fixture.Time.MoveWallClock(InitialUtc.AddHours(-1));
        await fixture.Provider.MaintainStateAsync();
        await fixture.Provider.PublishCurrentAsync();
        var blockedPublication = fixture.Provider.PublishCurrentAsync().AsTask();
        await WaitUntilBlockedAsync(blockedPublication);

        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.Provider.MaintainStateAsync();
        await AssertCanceledAsync(blockedPublication);
    }

    [TestMethod]
    public async Task ResetObservedAfterClockRollbackUsesMonotonicUtcAnchor()
    {
        using var fixture = new Fixture();
        fixture.Time.MoveWallClock(InitialUtc.AddHours(-1));
        fixture.EnqueueIdentity(IdentityA);
        fixture.EnqueuePlan(UsageJson(InitialUtc.AddMinutes(5)));
        fixture.EnqueueIdentity(IdentityA);
        await fixture.Provider.PollOnceAsync();

        var fresh = await fixture.ReadMetricAsync(SessionUsedId);
        Assert.AreEqual(MetricState.Fresh, fresh.State);

        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.Provider.MaintainStateAsync();
        var expired = await fixture.ReadMetricAsync(SessionUsedId);
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.AreEqual("reset_elapsed", expired.ErrorCode);
    }

    [TestMethod]
    public async Task RunAsyncPublishesStoppedWhenCanceled()
    {
        using var fixture = new Fixture();
        fixture.EnqueueIdentity(IdentityA);
        fixture.EnqueuePlan(UsageJson(InitialUtc.AddMinutes(5)));
        fixture.EnqueueIdentity(IdentityA);

        await using var reader = fixture.Bus.Subscribe(new MetricId(SessionUsedId)).GetAsyncEnumerator();
        var stoppedSnapshot = ReadUntilStoppedAsync(reader);
        using var stop = new CancellationTokenSource();
        var run = fixture.Provider.RunAsync(stop.Token);
        await fixture.Runner.ThreeCommandsObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await stoppedSnapshot.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(MetricState.Stopped, stopped.State);
        Assert.IsNull(stopped.Value);
    }

    [TestMethod]
    public async Task RunAsyncPropagatesUnexpectedWorkerFault()
    {
        using var fixture = new Fixture();
        fixture.Runner.EnqueueUnexpectedFailure();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Provider.RunAsync(CancellationToken.None));

        Assert.AreEqual("synthetic worker fault", exception.Message);
    }

    private static string UsageJson(DateTimeOffset resetAt)
    {
        var reset = resetAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var weekly = resetAt.AddDays(7).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var monthly = resetAt.AddDays(30).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"12345","user_name":"alice","account_id":"210000","profile":"personal-profile"},
              "items":[{"product":"coding-plan","edition":"personal","subscribed":true,"periods":[
                {"label":"session","used":25,"total":50,"percent":50,"reset_at":"{{reset}}"},
                {"label":"weekly","percent":20,"reset_at":"{{weekly}}"},
                {"label":"monthly","percent":10,"reset_at":"{{monthly}}"}
              ]}]
            }
            """;
    }

    private static async Task WaitUntilBlockedAsync(Task task)
    {
        await Task.Delay(50);
        Assert.IsFalse(task.IsCompleted, "The bounded subscriber should hold this publication pending.");
    }

    private static async Task<MetricSnapshot> ReadUntilStoppedAsync(IAsyncEnumerator<MetricSnapshot> reader)
    {
        while (await reader.MoveNextAsync())
            if (reader.Current.State == MetricState.Stopped)
                return reader.Current;
        throw new InvalidOperationException("The metric subscription ended before the provider stopped.");
    }

    private static async Task AssertCanceledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Assert.Fail("Expected the pending operation to be canceled.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cerith-ark-provider-" + Guid.NewGuid().ToString("N"));
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public ManualTimeProvider Time { get; } = new(InitialUtc);
        public ScriptedRunner Runner { get; }
        public ArkCodingPlanQuotaAcquirer Acquirer { get; }
        public ArkCodingPlanQuotaProvider Provider { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var cliPath = Path.Combine(_root, "arkcli.exe");
            File.WriteAllBytes(cliPath, []);
            Runner = new ScriptedRunner();
            Acquirer = new ArkCodingPlanQuotaAcquirer(cliPath, _root,
                name => name == "ARK_PROFILE" ? "personal-profile" : null,
                Runner.RunAsync, new byte[32]);
            Provider = new ArkCodingPlanQuotaProvider(Registry, Bus, Acquirer, Time, static _ => 0);
        }

        public void EnqueueIdentity(string json) => Runner.EnqueueJson(json);
        public void EnqueuePlan(string json) => Runner.EnqueueJson(json);
        public void EnqueueFailure(string code) => Runner.EnqueueFailure(code);
        public void EnqueuePending(TaskCompletionSource<bool> entered) => Runner.EnqueuePending(entered);

        public async Task<MetricSnapshot> ReadMetricAsync(string id)
        {
            await using var reader = Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await Provider.PublishCurrentAsync();
            Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            return reader.Current;
        }

        public void Dispose()
        {
            Provider.Dispose();
            Bus.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ScriptedRunner
    {
        private readonly Queue<Func<CancellationToken, Task<byte[]>>> _responses = new();
        public List<ProcessStartInfo> Commands { get; } = [];
        public TaskCompletionSource<bool> ThreeCommandsObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueJson(string json) => _responses.Enqueue(cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Encoding.UTF8.GetBytes(json));
        });

        public void EnqueueFailure(string errorCode) => _responses.Enqueue(_ =>
            throw new ArkCodingPlanAcquisitionException(errorCode));

        public void EnqueuePending(TaskCompletionSource<bool> entered) => _responses.Enqueue(async cancellationToken =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });

        public void EnqueueUnexpectedFailure() => _responses.Enqueue(_ =>
            throw new InvalidOperationException("synthetic worker fault"));

        public Task<byte[]> RunAsync(ProcessStartInfo startInfo, int maximumOutputBytes,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(startInfo);
            if (Commands.Count == 3) ThreeCommandsObserved.TrySetResult(true);
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected CLI invocation.");
            return _responses.Dequeue()(cancellationToken);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utc = initialUtc;
        private long _timestamp = -TimeSpan.TicksPerDay;

        public override DateTimeOffset GetUtcNow() => _utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _utc += elapsed;
            _timestamp += elapsed.Ticks;
        }

        public void MoveWallClock(DateTimeOffset value) => _utc = value;
    }
}
