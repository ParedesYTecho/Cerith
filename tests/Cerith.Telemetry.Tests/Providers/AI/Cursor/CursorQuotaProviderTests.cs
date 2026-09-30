#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class CursorQuotaProviderTests
{
    private static readonly string[] Ids =
    [
        "ai.cursor.quota.plan.total.used_percent",
        "ai.cursor.quota.plan.auto.used_percent",
        "ai.cursor.quota.plan.api.used_percent",
        "ai.cursor.quota.plan.reset",
        "ai.cursor.quota.plan.duration",
        "ai.cursor.quota.grok_bot.period.used_percent",
        "ai.cursor.quota.grok_bot.period.reset",
        "ai.cursor.quota.grok_bot.period.duration"
    ];

    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string UsageSummary = """
        {
          "individualUsage": {"plan": {
            "totalPercentUsed": 24,
            "autoPercentUsed": 50,
            "apiPercentUsed": 100
          }},
          "billingCycleStart": "2026-09-01T00:00:00Z",
          "billingCycleEnd": "2026-10-01T00:00:00Z",
          "teamUsage": {"plan": {"totalPercentUsed": 1}},
          "onDemandUsage": {"percentUsed": 2, "amountCents": 900}
        }
        """;

    [TestMethod]
    public void RegistersOnlyTypedAccountPlanAndGrokBotMetrics()
    {
        using var harness = new Harness();
        var descriptors = Ids.Select(id =>
        {
            Assert.IsTrue(harness.Registry.TryGet(new MetricId(id), out var descriptor));
            return descriptor!;
        }).ToArray();

        CollectionAssert.AreEqual(Ids, descriptors.Select(item => item.Id.Value).ToArray());
        CollectionAssert.AreEqual(
            new[] { MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double,
                MetricValueKind.DateTimeOffset, MetricValueKind.TimeSpan, MetricValueKind.Double,
                MetricValueKind.DateTimeOffset, MetricValueKind.TimeSpan },
            descriptors.Select(item => item.ValueKind).ToArray());
        Assert.IsTrue(descriptors.Where(item => item.ValueKind == MetricValueKind.Double)
            .All(item => item.Unit == "%" && item.SemanticType == "quota_used"));
        Assert.AreEqual("s", descriptors[4].Unit);
        Assert.AreEqual("s", descriptors[7].Unit);
    }

    [TestMethod]
    public void UsageSummaryUsesOnlyDirectPlanPercentagesAndExplicitBillingCycle()
    {
        var points = CursorQuotaParser.ParseUsageSummary(Encoding.UTF8.GetBytes(UsageSummary), InitialUtc);

        Assert.AreEqual(24d, points[0].Value!.Value.AsDouble());
        Assert.AreEqual(50d, points[1].Value!.Value.AsDouble());
        Assert.AreEqual(100d, points[2].Value!.Value.AsDouble());
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            points[3].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(TimeSpan.FromDays(30), points[4].Value!.Value.AsTimeSpan());
        Assert.IsTrue(points.All(point => point.State == MetricState.Fresh));

        const string noPlan = """
            {"teamUsage":{"plan":{"totalPercentUsed":1}},"onDemandUsage":{"percentUsed":0},
             "billingCycleStart":"2026-09-01T00:00:00Z","billingCycleEnd":"2026-10-01T00:00:00Z"}
            """;
        var absent = CursorQuotaParser.ParseUsageSummary(Encoding.UTF8.GetBytes(noPlan), InitialUtc);
        Assert.IsTrue(absent.Take(3).All(point => point.State == MetricState.Unavailable && point.Value is null));
        Assert.AreEqual(MetricState.Fresh, absent[3].State);
        Assert.AreEqual(MetricState.Fresh, absent[4].State);

        const string invalid = """
            {"individualUsage":{"plan":{"totalPercentUsed":101,"autoPercentUsed":"40","apiPercentUsed":0}},
             "billingCycleStart":"2026-09-01","billingCycleEnd":"2026-10-01T00:00:00Z"}
            """;
        var malformed = CursorQuotaParser.ParseUsageSummary(Encoding.UTF8.GetBytes(invalid), InitialUtc);
        Assert.AreEqual("invalid_quota", malformed[0].ErrorCode);
        Assert.AreEqual("invalid_quota", malformed[1].ErrorCode);
        Assert.AreEqual(MetricState.Fresh, malformed[2].State);
        Assert.AreEqual("invalid_duration", malformed[4].ErrorCode);
    }

    [TestMethod]
    public void SandUsageUsesReportedPercentAndTimesAndSuppressesZeroIncludedLimit()
    {
        var start = InitialUtc.AddDays(-7);
        var reset = InitialUtc.AddDays(7);
        var payload = MakeSandUsage(start, reset, 32.5, includedLimitZero: false,
            usesPooledEnterpriseAllowance: true, hasAvailableUsage: true);

        var access = CursorQuotaParser.ParseSandAccess(MakeSandAccess(1));
        Assert.IsTrue(access.Granted);
        var points = CursorQuotaParser.ParseSandUsage(payload, access.Granted, InitialUtc);
        Assert.AreEqual(32.5, points[0].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(reset, points[1].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(TimeSpan.FromDays(14), points[2].Value!.Value.AsTimeSpan());

        var absentPercent = CursorQuotaParser.ParseSandUsage(
            MakeSandUsage(start, reset, usagePercent: null, includedLimitZero: null), true, InitialUtc);
        Assert.AreEqual(MetricState.Unavailable, absentPercent[0].State);
        Assert.IsNull(absentPercent[0].Value);
        Assert.IsNull(absentPercent[0].ErrorCode);

        var missingLimitFlag = CursorQuotaParser.ParseSandUsage(
            MakeSandUsage(start, reset, 32.5, includedLimitZero: null), true, InitialUtc);
        Assert.AreEqual(MetricState.Fresh, missingLimitFlag[0].State);
        Assert.AreEqual(32.5, missingLimitFlag[0].Value!.Value.AsDouble(), 0.000001);

        var noIncludedLimit = CursorQuotaParser.ParseSandUsage(
            MakeSandUsage(start, reset, 32.5, includedLimitZero: true), true, InitialUtc);
        Assert.IsTrue(noIncludedLimit.All(point => point.State == MetricState.Unavailable && point.Value is null));

        using var malformedLimitFlag = new MemoryStream();
        malformedLimitFlag.Write(MakeSandUsage(start, reset, 32.5));
        WriteVarint(malformedLimitFlag, (4UL << 3) | 1UL);
        malformedLimitFlag.Write(new byte[sizeof(double)]);
        var malformed = CursorQuotaParser.ParseSandUsage(malformedLimitFlag.ToArray(), true, InitialUtc);
        Assert.IsTrue(malformed.All(point => point.State == MetricState.Error &&
            point.ErrorCode == "schema_invalid" && point.Value is null));

        var denied = CursorQuotaParser.ParseSandUsage(payload, granted: false, InitialUtc);
        Assert.IsTrue(denied.All(point => point.State == MetricState.Unavailable && point.Value is null));
        Assert.IsFalse(CursorQuotaParser.ParseSandAccess(MakeSandAccess(0)).Granted);
    }

    [TestMethod]
    public void DesktopStateTokenWorksWithoutCliConfigAndReadDoesNotChangeDatabase()
    {
        using var harness = new Harness();
        harness.WriteAuth("auth0|user_syntheticA", cliSubject: null);
        var databasePath = harness.StateDatabasePath;
        var before = SHA256.HashData(File.ReadAllBytes(databasePath));

        var read = harness.AuthReader.Read();

        var after = SHA256.HashData(File.ReadAllBytes(databasePath));
        Assert.AreEqual(CursorAuthStatus.Ready, read.Status);
        Assert.IsNotNull(read.Identity);
        Assert.AreEqual("user_syntheticA", read.Identity!.AccountId);
        Assert.IsTrue(read.Identity.CookieValue.StartsWith(
            "WorkosCursorSessionToken=user_syntheticA%3A%3A", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, after);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"authInfo\":{}}")]
    public void MissingOptionalCliIdentityFieldsUseDesktopStateToken(string config)
    {
        using var harness = new Harness();
        harness.WriteAuth("auth0|user_syntheticA", cliSubject: null);
        harness.WriteCliConfig(config);

        var read = harness.AuthReader.Read();

        Assert.AreEqual(CursorAuthStatus.Ready, read.Status);
        Assert.AreEqual("user_syntheticA", read.Identity!.AccountId);
    }

    [TestMethod]
    public void CliSubjectMustMatchTokenSubjectAfterReferenceNormalization()
    {
        using var harness = new Harness();
        harness.WriteAuth("auth0|user_syntheticA", "auth0|user_syntheticA");
        Assert.AreEqual(CursorAuthStatus.Ready, harness.AuthReader.Read().Status);

        harness.WriteAuth("auth0|user_syntheticA", "google-oauth2|other-account");
        var mismatch = harness.AuthReader.Read();
        Assert.AreEqual(CursorAuthStatus.Error, mismatch.Status);
        Assert.AreEqual("auth_identity_mismatch", mismatch.ErrorCode);
        Assert.IsNull(mismatch.Identity);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("{\"authInfo\":null}")]
    [DataRow("{\"authInfo\":{\"authId\":42}}")]
    [DataRow("{\"authInfo\":{\"authId\":\"invalid\"}}")]
    public async Task InvalidPresentCliConfigFailsClosedWithoutRequestsOrQuotaValues(string config)
    {
        using var harness = new Harness();
        harness.WriteCliConfig(config);

        var snapshots = await PollAndReadAllAsync(harness);

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error &&
            snapshot.ErrorCode == "config_invalid" && snapshot.Value is null &&
            snapshot.Confidence == MetricConfidence.Unknown));
    }

    [TestMethod]
    public async Task UnreadablePresentCliConfigFailsClosedWithoutRequestsOrQuotaValues()
    {
        using var harness = new Harness();
        harness.WriteCliConfig("{\"authInfo\":{\"authId\":\"auth0|user_syntheticA\"}}");
        using var configLock = harness.LockCliConfig();

        var snapshots = await PollAndReadAllAsync(harness);

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error &&
            snapshot.ErrorCode == "config_invalid" && snapshot.Value is null &&
            snapshot.Confidence == MetricConfidence.Unknown));
    }

    [TestMethod]
    public async Task UsesFixedReadOnlyHostsAndPublishesTruthfulMeasuredSnapshots()
    {
        using var harness = new Harness();
        harness.Handler.Responder = (request, _) => Task.FromResult(request.RequestUri switch
        {
            { } uri when uri == CursorQuotaProvider.UsageSummaryEndpoint => JsonResponse(UsageSummary),
            { } uri when uri == CursorQuotaProvider.SandAccessEndpoint => ProtoResponse(MakeSandAccess(1)),
            { } uri when uri == CursorQuotaProvider.SandUsageEndpoint => ProtoResponse(
                MakeSandUsage(InitialUtc.AddDays(-7), InitialUtc.AddDays(7), 32.5)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var snapshots = await PollAndReadAllAsync(harness);
        var requests = harness.Handler.Requests;
        Assert.AreEqual(3, requests.Length);
        var usageRequest = requests.Single(request => request.Uri == CursorQuotaProvider.UsageSummaryEndpoint);
        Assert.AreEqual(HttpMethod.Get, usageRequest.Method);
        Assert.IsTrue(usageRequest.HasCursorCookie);
        Assert.IsNull(usageRequest.AuthorizationScheme);
        var rpcRequests = requests.Where(request => request.Uri.Host == "api2.cursor.sh").ToArray();
        Assert.AreEqual(2, rpcRequests.Length);
        Assert.IsTrue(rpcRequests.All(request => request.Method == HttpMethod.Post &&
            request.AuthorizationScheme == "Bearer" && request.ContentType == "application/proto"));
        Assert.IsTrue(requests.All(request => request.Uri.Scheme == Uri.UriSchemeHttps &&
            request.Uri.Host is "cursor.com" or "api2.cursor.sh"));

        Assert.AreEqual(24d, snapshots[0].Value!.Value.AsDouble());
        Assert.AreEqual(50d, snapshots[1].Value!.Value.AsDouble());
        Assert.AreEqual(100d, snapshots[2].Value!.Value.AsDouble());
        Assert.AreEqual(32.5d, snapshots[5].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            snapshots[3].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(TimeSpan.FromDays(30), snapshots[4].Value!.Value.AsTimeSpan());
        Assert.AreEqual(TimeSpan.FromDays(14), snapshots[7].Value!.Value.AsTimeSpan());
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Fresh &&
            snapshot.Confidence == MetricConfidence.Measured));
        Assert.AreEqual("Cursor.UsageSummary.v1", snapshots[0].Source);
        Assert.AreEqual("Cursor.GrokBot.UsageStatus.v1", snapshots[5].Source);
    }

    [TestMethod]
    public async Task MissingLocalAuthPublishesUnavailableWithoutNetworkRequests()
    {
        using var harness = new Harness(withAuth: false);
        var snapshots = await PollAndReadAllAsync(harness);

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable &&
            snapshot.Value is null && snapshot.Confidence == MetricConfidence.Unknown));
    }

    [TestMethod]
    public async Task RenewableTokenReplacementIsReadSilentlyOnTheNextPoll()
    {
        using var harness = new Harness();
        var planCalls = 0;
        var replacementToken = MakeToken("auth0|user_syntheticA", "rotated");
        var replacementObserved = false;
        harness.Handler.Responder = (request, _) =>
        {
            if (request.RequestUri == CursorQuotaProvider.UsageSummaryEndpoint)
            {
                if (Interlocked.Increment(ref planCalls) == 1)
                {
                    harness.WriteAuth("auth0|user_syntheticA", cliSubject: null, tokenMarker: "rotated");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                replacementObserved = request.Headers.TryGetValues("Cookie", out var cookies) &&
                    cookies.Any(cookie => cookie.Contains(replacementToken, StringComparison.Ordinal));
                return Task.FromResult(JsonResponse(UsageSummary));
            }
            return Task.FromResult(request.RequestUri == CursorQuotaProvider.SandAccessEndpoint
                ? ProtoResponse(MakeSandAccess(0))
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var rejected = await PollAndReadOneAsync(harness);
        Assert.AreEqual(MetricState.Error, rejected.State);
        Assert.AreEqual("auth_rejected", rejected.ErrorCode);

        harness.Time.Advance(CursorQuotaProvider.PollInterval);
        var recovered = await PollAndReadOneAsync(harness);
        Assert.IsTrue(replacementObserved);
        Assert.AreEqual(MetricState.Fresh, recovered.State);
        Assert.AreEqual(24d, recovered.Value!.Value.AsDouble());
        Assert.AreEqual(4, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task RedirectAndRejectedAuthNeverFollowOrExposeValues()
    {
        using var harness = new Harness();
        harness.Handler.Responder = (request, _) => Task.FromResult(request.RequestUri == CursorQuotaProvider.UsageSummaryEndpoint
            ? RedirectResponse(new Uri("https://example.invalid/redirect"))
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var snapshots = await PollAndReadAllAsync(harness);

        Assert.AreEqual(2, harness.Handler.Requests.Length);
        Assert.IsTrue(harness.Handler.Requests.All(request => request.Uri.Host is "cursor.com" or "api2.cursor.sh"));
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Value is null));
        Assert.AreEqual("redirect_rejected", snapshots[0].ErrorCode);
        Assert.AreEqual("auth_rejected", snapshots[5].ErrorCode);
    }

    [TestMethod]
    public async Task RetryAfterBeyondCacheLifetimeIsHonoredWhileCacheExpires()
    {
        using var harness = new Harness();
        var planCall = 0;
        harness.Handler.Responder = (request, _) => Task.FromResult(request.RequestUri switch
        {
            { } uri when uri == CursorQuotaProvider.UsageSummaryEndpoint =>
                Interlocked.Increment(ref planCall) == 1 || planCall >= 3
                    ? JsonResponse(UsageSummary)
                    : StatusResponse(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(20)),
            { } uri when uri == CursorQuotaProvider.SandAccessEndpoint => ProtoResponse(MakeSandAccess(0)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var fresh = await PollAndReadOneAsync(harness);
        Assert.AreEqual(MetricState.Fresh, fresh.State);
        harness.Time.Advance(CursorQuotaProvider.PollInterval);
        var stale = await PollAndReadOneAsync(harness);
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual(24d, stale.Value!.Value.AsDouble());
        Assert.AreEqual("rate_limited", stale.ErrorCode);

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        var expired = await ObserveAndReadOneAsync(harness);
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.AreEqual("cache_expired", expired.ErrorCode);
        var requestCountBeforeRetry = harness.Handler.Requests.Length;

        var stillWaiting = await PollAndReadOneAsync(harness);
        Assert.AreEqual(MetricState.Unavailable, stillWaiting.State);
        Assert.AreEqual(requestCountBeforeRetry, harness.Handler.Requests.Length,
            "The 20-minute Retry-After must outlive the 15-minute cache TTL.");

        harness.Time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(1)));
        await PollAndReadOneAsync(harness);
        Assert.AreEqual(requestCountBeforeRetry + 2, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task OwnerChangeCancelsBlockedOldGenerationPublishBeforeNewStateIsReleased()
    {
        using var harness = new Harness();
        harness.SetSuccessfulAccountResponse();
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        await FillSubscriberWithInitialSnapshotAsync(harness, reader);

        var blockedOldPublish = harness.Provider.PublishCurrentAsync();
        Assert.IsFalse(blockedOldPublish.IsCompleted, "The bounded subscriber must hold the old publication pending.");

        harness.WriteAuth("auth0|user_syntheticB", "auth0|user_syntheticB");
        var transition = harness.Provider.ObserveLocalStateAsync();
        await blockedOldPublish.WaitAsync(TimeSpan.FromSeconds(2));
        await transition;
        var newPublication = harness.Provider.PublishCurrentAsync();

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "The snapshot already buffered before the transition cannot be retracted.");
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State,
            "The blocked old-owner value must be canceled before the new generation is published.");
        Assert.IsNull(reader.Current.Value);
        await newPublication.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CacheExpiryCancelsBlockedFreshPublication()
    {
        await AssertBlockedValueInvalidatedAsync(TimeSpan.FromMinutes(16), "cache_expired");
    }

    [TestMethod]
    public async Task ResetCrossingCancelsBlockedFreshPublication()
    {
        await AssertBlockedValueInvalidatedAsync(TimeSpan.FromHours(12), "reset_elapsed");
    }

    [TestMethod]
    public async Task InFlightOldOwnerResponsesAreDiscardedAfterAccountTransition()
    {
        using var harness = new Harness();
        var bothRequestsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingRequests = 0;
        harness.Handler.Responder = async (request, cancellationToken) =>
        {
            if (request.RequestUri == CursorQuotaProvider.SandUsageEndpoint)
                return ProtoResponse(MakeSandUsage(InitialUtc.AddDays(-7), InitialUtc.AddDays(7), 20));
            if (Interlocked.Increment(ref waitingRequests) == 2) bothRequestsStarted.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return request.RequestUri == CursorQuotaProvider.UsageSummaryEndpoint
                ? JsonResponse(UsageSummary)
                : ProtoResponse(MakeSandAccess(1));
        };

        var pendingPoll = harness.Provider.PollOnceAsync();
        await bothRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.WriteAuth("auth0|user_syntheticB", "auth0|user_syntheticB");
        await harness.Provider.ObserveLocalStateAsync();
        release.SetResult();
        await pendingPoll.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshots = await PublishAndReadAllAsync(harness);

        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
    }

    [TestMethod]
    public async Task RunCancellationPublishesStoppedLifecycleState()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var root = Path.Combine(Path.GetTempPath(), "cerith-cursor-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reader = bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        using var provider = new CursorQuotaProvider(registry, bus,
            new CursorQuotaAuthReader(Path.Combine(root, "missing-appdata"), Path.Combine(root, "missing-profile")),
            client, TimeProvider.System);
        using var stop = new CancellationTokenSource();

        var firstRead = reader.MoveNextAsync().AsTask();
        var run = provider.RunAsync(stop.Token);
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        var stoppedRead = reader.MoveNextAsync().AsTask();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await stoppedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        await reader.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task RuntimeMonitorInvalidatesBlockedFreshValueAtResetWithoutExternalObservation()
    {
        using var harness = new Harness();
        harness.SetSuccessfulAccountResponse();
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        await FillSubscriberWithInitialSnapshotAsync(harness, reader);
        var blockedPublication = harness.Provider.PublishCurrentAsync();
        Assert.IsFalse(blockedPublication.IsCompleted);
        using var stop = new CancellationTokenSource();
        var run = harness.Provider.RunAsync(stop.Token);
        try
        {
            harness.Time.Advance(TimeSpan.FromHours(12));
            await blockedPublication.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(MetricState.Fresh, reader.Current.State);
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
            Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
            Assert.IsNull(reader.Current.Value);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    public async Task RuntimeWorkerFaultStillPublishesStoppedAndReachesSupervisor()
    {
        using var harness = new Harness();
        var failure = new InvalidOperationException("Synthetic worker fault.");
        harness.Handler.Responder = (_, _) => Task.FromException<HttpResponseMessage>(failure);
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var stoppedRead = reader.MoveNextAsync().AsTask();
        var run = harness.Provider.RunAsync(CancellationToken.None);

        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Fail("The unexpected worker fault must reach its supervisor.");
        }
        catch (InvalidOperationException exception)
        {
            Assert.AreSame(failure, exception);
        }
        Assert.IsTrue(await stoppedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
    }

    [TestMethod]
    public async Task StoppedPublicationIsBoundedAndOtherMetricsProceedPastBlockedSubscriber()
    {
        using var harness = new Harness();
        harness.SetSuccessfulAccountResponse();
        await using var blockedReader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        await FillSubscriberWithInitialSnapshotAsync(harness, blockedReader);
        await using var stoppedReader = harness.Bus.Subscribe(new MetricId(Ids[5])).GetAsyncEnumerator();
        var stoppedRead = stoppedReader.MoveNextAsync().AsTask();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await harness.Provider.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await stoppedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stopped, stoppedReader.Current.State);
        Assert.IsTrue(await blockedReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, blockedReader.Current.State);
    }

    [TestMethod]
    public async Task OfflineAndTimeoutHaveNoFabricatedValuesWithoutCache()
    {
        using (var offline = new Harness())
        {
            offline.Handler.Responder = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException());
            var snapshots = await PollAndReadAllAsync(offline);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable &&
                snapshot.Value is null && snapshot.ErrorCode == "offline"));
        }
        using (var timeout = new Harness(requestTimeout: TimeSpan.FromMilliseconds(40)))
        {
            timeout.Handler.Responder = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertFailedException("Deadline cancellation must end the request.");
            };
            var snapshots = await PollAndReadAllAsync(timeout);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error &&
                snapshot.Value is null && snapshot.ErrorCode == "timeout"));
        }
    }

    [TestMethod]
    public async Task KnownAndUnknownLengthBodiesAreBoundedBeforeParsing()
    {
        foreach (var unknownLength in new[] { false, true })
        {
            using var harness = new Harness();
            harness.Handler.Responder = (request, _) =>
            {
                if (request.RequestUri != CursorQuotaProvider.UsageSummaryEndpoint)
                    return Task.FromResult(ProtoResponse(MakeSandAccess(0)));
                var bytes = new byte[CursorQuotaProvider.MaxResponseBytes + 1];
                HttpContent content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            };
            var snapshot = await PollAndReadOneAsync(harness);
            Assert.AreEqual(MetricState.Error, snapshot.State);
            Assert.AreEqual("response_too_large", snapshot.ErrorCode);
            Assert.IsNull(snapshot.Value);
        }
    }

    [TestMethod]
    public async Task OwnerTransitionDoesNotInheritAnotherAccountsRetryAfter()
    {
        using var harness = new Harness();
        harness.Handler.Responder = (request, _) => Task.FromResult(
            request.RequestUri == CursorQuotaProvider.UsageSummaryEndpoint
                ? StatusResponse(HttpStatusCode.TooManyRequests, TimeSpan.FromDays(1))
                : ProtoResponse(MakeSandAccess(0)));
        await PollAndReadOneAsync(harness);
        var originalCount = harness.Handler.Requests.Length;
        harness.WriteAuth("auth0|user_syntheticB", "auth0|user_syntheticB");
        harness.SetSuccessfulAccountResponse();
        var snapshot = await PollAndReadOneAsync(harness);
        Assert.AreEqual(originalCount + 2, harness.Handler.Requests.Length);
        Assert.AreEqual(MetricState.Fresh, snapshot.State);
    }

    private static async Task<MetricSnapshot[]> PollAndReadAllAsync(Harness harness)
    {
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        await harness.Provider.PollOnceAsync();
        await Task.WhenAll(pending);
        var snapshots = readers.Select(reader => reader.Current).ToArray();
        foreach (var reader in readers) await reader.DisposeAsync();
        return snapshots;
    }

    private static async Task<MetricSnapshot> PollAndReadOneAsync(Harness harness)
    {
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await harness.Provider.PollOnceAsync();
        Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        return reader.Current;
    }

    private static async Task<MetricSnapshot> ObserveAndReadOneAsync(Harness harness)
    {
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await harness.Provider.ObserveLocalStateAsync();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        return reader.Current;
    }

    private static async Task<MetricSnapshot[]> PublishAndReadAllAsync(Harness harness)
    {
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        await harness.Provider.PublishCurrentAsync();
        await Task.WhenAll(pending);
        var snapshots = readers.Select(reader => reader.Current).ToArray();
        foreach (var reader in readers) await reader.DisposeAsync();
        return snapshots;
    }

    private static async Task AssertBlockedValueInvalidatedAsync(TimeSpan advance, string expectedError)
    {
        using var harness = new Harness();
        harness.SetSuccessfulAccountResponse();
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        await FillSubscriberWithInitialSnapshotAsync(harness, reader);

        var blockedFreshPublish = harness.Provider.PublishCurrentAsync();
        Assert.IsFalse(blockedFreshPublish.IsCompleted,
            "A full subscriber must keep the duplicate value-bearing publication pending.");
        harness.Time.Advance(advance);
        var invalidation = harness.Provider.ObserveLocalStateAsync();
        await blockedFreshPublish.WaitAsync(TimeSpan.FromSeconds(2));
        await invalidation;
        var newPublication = harness.Provider.PublishCurrentAsync();

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "An item buffered before expiry remains observable.");
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual(expectedError, reader.Current.ErrorCode);
        Assert.IsNull(reader.Current.Value);
        await newPublication.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<MetricSnapshot> FillSubscriberWithInitialSnapshotAsync(Harness harness,
        IAsyncEnumerator<MetricSnapshot> reader)
    {
        var firstRead = reader.MoveNextAsync().AsTask();
        await harness.Provider.PollOnceAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        var buffered = reader.Current;
        await harness.Bus.PublishAsync(buffered);
        return buffered;
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ProtoResponse(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/proto") } }
    };

    private static HttpResponseMessage StatusResponse(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is not null) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        return response;
    }

    private static HttpResponseMessage RedirectResponse(Uri location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = location;
        return response;
    }

    private static byte[] MakeSandAccess(ulong state)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, (1UL << 3) | 0);
        WriteVarint(stream, state);
        return stream.ToArray();
    }

    private static byte[] MakeSandUsage(DateTimeOffset start, DateTimeOffset reset, double? usagePercent,
        bool? includedLimitZero = false, bool usesPooledEnterpriseAllowance = false, bool hasAvailableUsage = false)
    {
        using var stream = new MemoryStream();
        WriteMessage(stream, 1, MakeTimestamp(start));
        WriteMessage(stream, 2, MakeTimestamp(reset));
        if (usagePercent is double value)
        {
            WriteVarint(stream, (3UL << 3) | 1);
            Span<byte> bytes = stackalloc byte[sizeof(double)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            stream.Write(bytes);
        }
        if (includedLimitZero is bool zeroLimit) WriteBoolean(stream, 4, zeroLimit);
        WriteBoolean(stream, 6, usesPooledEnterpriseAllowance);
        WriteBoolean(stream, 7, hasAvailableUsage);
        return stream.ToArray();
    }

    private static byte[] MakeTimestamp(DateTimeOffset timestamp)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, (1UL << 3) | 0);
        WriteVarint(stream, unchecked((ulong)timestamp.ToUnixTimeSeconds()));
        return stream.ToArray();
    }

    private static void WriteMessage(Stream stream, int field, byte[] bytes)
    {
        WriteVarint(stream, ((ulong)field << 3) | 2);
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteBoolean(Stream stream, int field, bool value)
    {
        WriteVarint(stream, ((ulong)field << 3) | 0);
        WriteVarint(stream, value ? 1UL : 0UL);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cerith-cursor-test-" + Guid.NewGuid().ToString("N"));
        public string StateDatabasePath => Path.Combine(_root, "appdata", "Cursor", "User", "globalStorage", "state.vscdb");
        private string ProfilePath => Path.Combine(_root, "profile");
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public TestTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public CursorQuotaAuthReader AuthReader { get; }
        public CursorQuotaProvider Provider { get; }

        public Harness(bool withAuth = true, TimeSpan? requestTimeout = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateDatabasePath)!);
            Directory.CreateDirectory(ProfilePath);
            if (withAuth) WriteAuth("auth0|user_syntheticA", "auth0|user_syntheticA");
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            AuthReader = new CursorQuotaAuthReader(Path.Combine(_root, "appdata"), ProfilePath);
            Provider = new CursorQuotaProvider(Registry, Bus, AuthReader, Client, Time, requestTimeout);
        }

        public void WriteAuth(string tokenSubject, string? cliSubject, string tokenMarker = "initial")
        {
            WriteStateToken(MakeToken(tokenSubject, tokenMarker));
            var configPath = Path.Combine(ProfilePath, ".cursor", "cli-config.json");
            if (cliSubject is null)
            {
                if (File.Exists(configPath)) File.Delete(configPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, JsonSerializer.Serialize(new { authInfo = new { authId = cliSubject } }));
        }

        public void WriteCliConfig(string contents)
        {
            var configPath = Path.Combine(ProfilePath, ".cursor", "cli-config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, contents);
        }

        public FileStream LockCliConfig()
        {
            var configPath = Path.Combine(ProfilePath, ".cursor", "cli-config.json");
            return new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        public void SetSuccessfulAccountResponse()
        {
            Handler.Responder = (request, _) => Task.FromResult(request.RequestUri switch
            {
                { } uri when uri == CursorQuotaProvider.UsageSummaryEndpoint => JsonResponse(UsageSummary),
                { } uri when uri == CursorQuotaProvider.SandAccessEndpoint => ProtoResponse(MakeSandAccess(0)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }

        private void WriteStateToken(string token)
        {
            var result = SqliteOpen(StateDatabasePath, out var database, 0x00000002 | 0x00000004, IntPtr.Zero);
            if (result != 0 || database == IntPtr.Zero) throw new InvalidOperationException("Synthetic SQLite open failed.");
            try
            {
                Exec(database, "CREATE TABLE IF NOT EXISTS ItemTable(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
                Exec(database, "DELETE FROM ItemTable WHERE key = 'cursorAuth/accessToken';");
                Exec(database, "INSERT INTO ItemTable(key, value) VALUES ('cursorAuth/accessToken', '" +
                    token.Replace("'", "''", StringComparison.Ordinal) + "');");
            }
            finally { _ = SqliteClose(database); }
        }

        public void Dispose()
        {
            Provider.Dispose();
            Client.Dispose();
            Bus.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private static string MakeToken(string subject, string marker = "synthetic")
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = subject, jti = marker })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "synthetic." + payload + ".signature";
    }

    private static void Exec(IntPtr database, string sql)
    {
        var result = SqliteExec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) SqliteFree(error);
        if (result != 0) throw new InvalidOperationException("Synthetic SQLite statement failed.");
    }

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, out IntPtr database,
        int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteExec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        IntPtr callback, IntPtr callbackState, out IntPtr errorMessage);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SqliteFree(IntPtr pointer);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteClose(IntPtr database);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RequestObservation> _requests = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }
        public RequestObservation[] Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var cookie = request.Headers.TryGetValues("Cookie", out var values) &&
                values.Any(value => value.StartsWith("WorkosCursorSessionToken=", StringComparison.Ordinal));
            _requests.Enqueue(new RequestObservation(request.Method, request.RequestUri!, cookie,
                request.Headers.Authorization?.Scheme, request.Content?.Headers.ContentType?.MediaType));
            var response = Responder is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : await Responder(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, bool HasCursorCookie,
        string? AuthorizationScheme, string? ContentType);

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class TestTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(callback, state, dueTime, period);

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
