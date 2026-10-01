#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.Telemetry.Tests.Providers.AI.Antigravity;
using Cerith.TelemetryService;
using Cerith.TelemetryService.Providers.AI.Antigravity;
using Cerith.TelemetryService.Providers.AI.CommandCode;
using Cerith.TelemetryService.Providers.AI.GitHubCopilot;
using Cerith.TelemetryService.Providers.AI.Kiro;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class CodexQuotaProviderTests
{
    private static readonly string[] Ids =
    [
        "ai.codex.quota.primary.remaining",
        "ai.codex.quota.secondary.remaining",
        "ai.codex.quota.primary.reset",
        "ai.codex.quota.secondary.reset",
        "ai.codex.quota.primary.duration",
        "ai.codex.quota.secondary.duration"
    ];

    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RegistersExactlySixTypedMetricDescriptors()
    {
        using var harness = new Harness();
        var descriptors = harness.Registry.GetSnapshot().OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(Ids.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            descriptors.Select(item => item.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { MetricValueKind.TimeSpan, MetricValueKind.Double,
                MetricValueKind.DateTimeOffset, MetricValueKind.TimeSpan, MetricValueKind.Double,
                MetricValueKind.DateTimeOffset },
            descriptors.Select(item => item.ValueKind).ToArray());
        Assert.AreEqual("%", descriptors.Single(item => item.Id.Value == Ids[0]).Unit);
        Assert.AreEqual("quota_remaining", descriptors.Single(item => item.Id.Value == Ids[0]).SemanticType);
    }

    [TestMethod]
    public void SyntheticFixtureMapsOnlyOrdinaryQuotaWindows()
    {
        var payload = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codex",
            "usage.synthetic.fixture.json"));
        var reading = CodexQuotaParser.Parse(payload, "synthetic-account-primary", "synthetic-user-A",
            InitialUtc, TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(MetricState.Fresh, reading.Points[0].State,
            "The informational quota value remains valid when the separate admission flag denies requests.");
        Assert.AreEqual(62.75, reading.Points[0].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(87.5, reading.Points[1].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1893456000), reading.Points[2].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1894060800), reading.Points[3].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(TimeSpan.FromHours(5), reading.Points[4].Value!.Value.AsTimeSpan());
        Assert.AreEqual(TimeSpan.FromDays(7), reading.Points[5].Value!.Value.AsTimeSpan());
    }

    [TestMethod]
    public void MissingNullAndAdditionalBucketsNeverBecomeQuotaValues()
    {
        const string json = """
            {"credits":{"unlimited":true,"balance":100},"additional_rate_limits":[{"primary_window":{"used_percent":0}}],"rate_limit":{"primary_window":null}}
            """;
        var reading = Parse(json);

        Assert.IsTrue(reading.Points.All(point => point.State == MetricState.Unavailable && point.Value is null));
    }

    [TestMethod]
    public void InvalidFieldsFailIndependentlyAndValidZeroRemainingIsPreserved()
    {
        const string json = """
            {"account_id":"synthetic-account-primary","rate_limit":{"primary_window":{"used_percent":-1,"limit_window_seconds":0},"secondary_window":{"used_percent":100,"reset_at":9223372036854775808,"limit_window_seconds":1}}}
            """;
        var reading = Parse(json);

        Assert.AreEqual(MetricState.Error, reading.Points[0].State);
        Assert.AreEqual("invalid_quota", reading.Points[0].ErrorCode);
        Assert.AreEqual(MetricState.Error, reading.Points[4].State);
        Assert.AreEqual("invalid_duration", reading.Points[4].ErrorCode);
        Assert.AreEqual(MetricState.Fresh, reading.Points[1].State);
        Assert.AreEqual(0d, reading.Points[1].Value!.Value.AsDouble());
        Assert.AreEqual(MetricState.Error, reading.Points[3].State);
        Assert.AreEqual("invalid_reset", reading.Points[3].ErrorCode);
        Assert.AreEqual(TimeSpan.FromSeconds(1), reading.Points[5].Value!.Value.AsTimeSpan());
    }

    [TestMethod]
    public void ElapsedResetInvalidatesItsWholeWindowAndDisagreementKeepsReportedReset()
    {
        var elapsed = InitialUtc.ToUnixTimeSeconds() - 1;
        var elapsedReading = Parse($"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":0,\"reset_at\":{elapsed},\"limit_window_seconds\":18000}}}}}}");
        Assert.IsTrue(new[] { 0, 2, 4 }.All(index => elapsedReading.Points[index].State == MetricState.Unavailable));
        Assert.IsTrue(new[] { 0, 2, 4 }.All(index => elapsedReading.Points[index].ErrorCode == "reset_elapsed"));

        var future = InitialUtc.AddHours(1).ToUnixTimeSeconds();
        var disagreeing = Parse($"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":25,\"reset_at\":{future},\"reset_after_seconds\":1,\"limit_window_seconds\":18000}}}}}}");
        Assert.AreEqual(MetricState.Fresh, disagreeing.Points[2].State);
        Assert.AreEqual("source_disagreement", disagreeing.Points[2].ErrorCode);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(future), disagreeing.Points[2].Value!.Value.AsDateTimeOffset());
    }

    [TestMethod]
    public void MalformedCountdownPreservesAuthoritativeReset()
    {
        var future = InitialUtc.AddHours(1).ToUnixTimeSeconds();
        foreach (var countdown in new[] { "\"bad\"", "false", "{}", "-1", "1e999" })
        {
            var reading = Parse($"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":25,\"reset_at\":{future},\"reset_after_seconds\":{countdown},\"limit_window_seconds\":18000}}}}}}");
            Assert.AreEqual(MetricState.Fresh, reading.Points[2].State, countdown);
            Assert.AreEqual("invalid_reset_after", reading.Points[2].ErrorCode, countdown);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(future), reading.Points[2].Value!.Value.AsDateTimeOffset());
        }
    }

    [TestMethod]
    public void RejectsInvalidSchemaAndResponseIdentity()
    {
        Assert.IsTrue(CodexQuotaParser.Parse(Encoding.UTF8.GetBytes("[]"), "synthetic-account-primary", "synthetic-user-A",
                InitialUtc, TimeSpan.Zero).Points.All(point => point.ErrorCode == "schema_invalid"));
        var mismatch = Parse("{\"account_id\":\"other-account\",\"rate_limit\":{}}");
        Assert.IsTrue(mismatch.Points.All(point => point.ErrorCode == "identity_mismatch"));
        var userMismatch = Parse("{\"user_id\":\"other-user\",\"rate_limit\":{}}");
        Assert.IsTrue(userMismatch.Points.All(point => point.ErrorCode == "identity_mismatch"));
        var malformedWindow = Parse("{\"rate_limit\":{\"primary_window\":false}}");
        Assert.IsTrue(new[] { 0, 2, 4 }.All(index => malformedWindow.Points[index].ErrorCode == "schema_invalid"));
        Assert.IsTrue(new[] { 1, 3, 5 }.All(index => malformedWindow.Points[index].State == MetricState.Unavailable));
    }

    [TestMethod]
    public async Task SendsOnlyTheFixedReadOnlyRequestAndPublishesAllSixValues()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        await ReadAllAfterPublishAsync(harness);

        var request = harness.Handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(CodexQuotaProvider.UsageEndpoint, request.Uri);
        Assert.AreEqual("Bearer", request.AuthorizationScheme);
        Assert.AreEqual("synthetic-access-A", request.AccessToken);
        Assert.AreEqual("synthetic-account-primary", request.AccountId);
        Assert.AreEqual("codex-cli", request.UserAgent);
        Assert.IsNull(request.ContentType);
        Assert.AreEqual(MetricState.Fresh, harness.ReadSnapshots[0].State);
        Assert.AreEqual(62.75, harness.ReadSnapshots[0].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(MetricValueKind.Double, harness.ReadSnapshots[1].Value!.Value.Kind);
        Assert.AreEqual(MetricValueKind.DateTimeOffset, harness.ReadSnapshots[2].Value!.Value.Kind);
        Assert.AreEqual(MetricValueKind.TimeSpan, harness.ReadSnapshots[4].Value!.Value.Kind);
        Assert.AreEqual("Codex.Usage.ReadOnly.v1", harness.ReadSnapshots[0].Source);
        Assert.IsTrue(harness.ReadSnapshots.All(snapshot => snapshot.Confidence == MetricConfidence.Measured));
    }

    [TestMethod]
    public async Task PollCadenceIsSixtySecondsAndCacheMovesFreshStaleThenExpires()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();

        harness.Time.Advance(TimeSpan.FromSeconds(59));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(1, harness.Handler.Requests.Length);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);

        harness.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.AreEqual(MetricState.Fresh, (await ReadOneAsync(harness, Ids[0])).State);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var stale = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual(62.75, stale.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(InitialUtc.AddSeconds(60), stale.ObservedAt);

        harness.Time.Advance(TimeSpan.FromSeconds(780));
        var expired = await ReadAllAsync(harness);
        Assert.IsTrue(expired.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
    }

    [TestMethod]
    public async Task SuccessfulMissingFieldEvictsCacheAndNetworkFailureCannotResurrectIt()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson("{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":3600}}}");
        await harness.Provider.PollOnceAsync();
        var afterMissing = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Unavailable, afterMissing.State);
        Assert.IsNull(afterMissing.Value);

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        await harness.Provider.PollOnceAsync();
        var afterFailure = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, afterFailure.State);
        Assert.AreEqual("upstream_unavailable", afterFailure.ErrorCode);
        Assert.IsNull(afterFailure.Value);
    }

    [TestMethod]
    public async Task OfflineImmediatelyStalesEligibleCacheAndSuccessfulRecoveryRestoresFresh()
    {
        using (var harness = new Harness())
        {
            harness.Handler.EnqueueJson(harness.Fixture);
            await harness.Provider.PollOnceAsync();
            harness.Time.Advance(CodexQuotaProvider.PollInterval);
            harness.Handler.Enqueue((_, _) => throw new HttpRequestException());
            await harness.Provider.PollOnceAsync();
            var stale = await ReadOneAsync(harness, Ids[0]);
            Assert.AreEqual(MetricState.Stale, stale.State);
            Assert.AreEqual("offline", stale.ErrorCode);
            Assert.AreEqual(InitialUtc, stale.ObservedAt);

            harness.Time.Advance(CodexQuotaProvider.PollInterval);
            harness.Handler.EnqueueJson(harness.Fixture);
            await harness.Provider.PollOnceAsync();
            var recovered = await ReadOneAsync(harness, Ids[0]);
            Assert.AreEqual(MetricState.Fresh, recovered.State);
            Assert.IsNull(recovered.ErrorCode);
            Assert.AreEqual(InitialUtc.AddSeconds(120), recovered.ObservedAt);
        }

        using (var harness = new Harness())
        {
            harness.Handler.Enqueue((_, _) => throw new HttpRequestException());
            await harness.Provider.PollOnceAsync();
            var offline = await ReadOneAsync(harness, Ids[0]);
            Assert.AreEqual(MetricState.Unavailable, offline.State);
            Assert.IsNull(offline.ErrorCode);
            Assert.IsNull(offline.Value);
        }
    }

    [TestMethod]
    [DataRow("offline", "offline")]
    [DataRow("timeout", "timeout")]
    [DataRow("http_5xx", "upstream_unavailable")]
    [DataRow("http_429", "rate_limited")]
    public async Task CacheableAcquisitionFailureUsesCurrentCodeAndPreservesResetCache(
        string failureKind, string expectedErrorCode)
    {
        using var harness = new Harness();
        var disputedFixture = harness.Fixture.Replace("\"reset_at\": 1893456000,",
            "\"reset_at\": 1893456000, \"reset_after_seconds\": 1,");
        Assert.AreNotEqual(harness.Fixture, disputedFixture);
        harness.Handler.EnqueueJson(disputedFixture);
        await harness.Provider.PollOnceAsync();

        var cached = await ReadOneAsync(harness, Ids[2]);
        Assert.AreEqual(MetricState.Fresh, cached.State);
        Assert.AreEqual(MetricConfidence.Measured, cached.Confidence);
        Assert.AreEqual("source_disagreement", cached.ErrorCode);
        var cachedReset = cached.Value!.Value.AsDateTimeOffset();

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        switch (failureKind)
        {
            case "offline":
                harness.Handler.Enqueue((_, _) => throw new HttpRequestException());
                await harness.Provider.PollOnceAsync();
                break;
            case "timeout":
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                harness.Handler.Enqueue(async (_, cancellationToken) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });
                var pending = harness.Provider.PollOnceAsync();
                await entered.Task;
                harness.Time.Advance(CodexQuotaProvider.RequestTimeout);
                await pending;
                break;
            case "http_5xx":
                harness.Handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
                await harness.Provider.PollOnceAsync();
                break;
            case "http_429":
                harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests, "1200");
                await harness.Provider.PollOnceAsync();
                break;
            default:
                Assert.Fail($"Unexpected failure kind: {failureKind}");
                break;
        }

        var stale = await ReadOneAsync(harness, Ids[2]);
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual(MetricConfidence.Measured, stale.Confidence);
        Assert.AreEqual(expectedErrorCode, stale.ErrorCode);
        Assert.AreEqual(cachedReset, stale.Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(cached.ObservedAt, stale.ObservedAt);

        if (failureKind == "http_429")
        {
            harness.Time.Advance(CodexQuotaProvider.PollInterval);
            await harness.Provider.PollOnceAsync();
            Assert.AreEqual(2, harness.Handler.Requests.Length,
                "A long Retry-After must suppress the next request.");
        }
    }

    [TestMethod]
    public async Task UnauthorizedClearsCacheSuppressesSameTokenAndAcceptsOfficialTokenRotation()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        await harness.Provider.PollOnceAsync();
        var rejected = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, rejected.State);
        Assert.AreEqual("auth_required", rejected.ErrorCode);
        Assert.IsNull(rejected.Value);

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);

        harness.WriteAuth("synthetic-access-B", "synthetic-account-primary", "synthetic-subject-A", "synthetic-user-A");
        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(3, harness.Handler.Requests.Length);
        Assert.AreEqual(MetricState.Fresh, (await ReadOneAsync(harness, Ids[0])).State);
    }

    [TestMethod]
    public async Task RateLimitHonorsLongRetryAfterWithoutEarlyRequest()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests, "1200");
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(15));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(1, harness.Handler.Requests.Length);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests, "1200");
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task RateLimitBackoffDoublesWithoutRetryAfter()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);

        harness.Time.Advance(TimeSpan.FromSeconds(119));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(3, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task ResetBoundaryClockCorrectionAndIdleOwnerChangeInvalidateCache()
    {
        using (var harness = new Harness())
        {
            var resetAt = harness.Time.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds();
            harness.Handler.EnqueueJson($"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":25,\"reset_at\":{resetAt},\"limit_window_seconds\":3600}}}}}}");
            await harness.Provider.PollOnceAsync();
            harness.Time.Advance(TimeSpan.FromMinutes(2));
            var expired = await ReadAllAsync(harness);
            Assert.IsTrue(new[] { 0, 2, 4 }.All(index => expired[index].State == MetricState.Unavailable &&
                expired[index].ErrorCode == "reset_elapsed"));
        }

        using (var harness = new Harness())
        {
            harness.Handler.EnqueueJson(harness.Fixture);
            await harness.Provider.PollOnceAsync();
            harness.Time.ShiftUtcOnly(TimeSpan.FromHours(-1));
            var invalidated = await ReadAllAsync(harness);
            Assert.IsTrue(invalidated.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
            Assert.IsNull(invalidated[0].ErrorCode);
        }

        using (var harness = new Harness())
        {
            harness.Handler.EnqueueJson(harness.Fixture);
            await harness.Provider.PollOnceAsync();
            harness.WriteAuth("synthetic-access-B", "synthetic-account-secondary", "synthetic-subject-B", "synthetic-user-B");
            var switched = await ReadAllAsync(harness);
            Assert.IsTrue(switched.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        }
    }

    [TestMethod]
    public async Task ForbiddenAuthClearsCacheAndDoesNotRetryTheSameToken()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.Forbidden);
        await harness.Provider.PollOnceAsync();
        var forbidden = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, forbidden.State);
        Assert.AreEqual("auth_forbidden", forbidden.ErrorCode);
        Assert.IsNull(forbidden.Value);
        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task OwnerChangeDiscardsLateSuccessAndLateUnauthorizedResponse()
    {
        await AssertOwnerChangeDiscardsResponseAsync(success: true);
        await AssertOwnerChangeDiscardsResponseAsync(success: false);
    }

    [TestMethod]
    public async Task ResponseIdentityMismatchAndRedirectFailClosed()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson("{\"account_id\":\"other-account\",\"rate_limit\":{}}");
        await harness.Provider.PollOnceAsync();
        var mismatch = await ReadAllAsync(harness);
        Assert.IsTrue(mismatch.All(snapshot => snapshot.State == MetricState.Error &&
            snapshot.ErrorCode == "identity_mismatch" && snapshot.Value is null));

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueRedirect();
        await harness.Provider.PollOnceAsync();
        var redirected = await ReadAllAsync(harness);
        Assert.AreEqual(MetricState.Error, redirected[0].State);
        Assert.AreEqual("redirect_rejected", redirected[0].ErrorCode);
        Assert.AreEqual(2, harness.Handler.Requests.Length);
        Assert.IsTrue(harness.Handler.Requests.All(request => request.Uri == CodexQuotaProvider.UsageEndpoint));
    }

    [TestMethod]
    public async Task RedirectClientErrorAndOversizeClearPreviouslyCachedQuota()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(MetricState.Fresh, (await ReadOneAsync(harness, Ids[0])).State);

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueRedirect();
        await harness.Provider.PollOnceAsync();
        var redirect = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, redirect.State);
        Assert.AreEqual("redirect_rejected", redirect.ErrorCode);
        Assert.IsNull(redirect.Value);

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(MetricState.Fresh, (await ReadOneAsync(harness, Ids[0])).State);

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.BadRequest);
        await harness.Provider.PollOnceAsync();
        var clientError = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, clientError.State);
        Assert.AreEqual("http_failure", clientError.ErrorCode);
        Assert.IsNull(clientError.Value);

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        harness.Handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[CodexQuotaProvider.MaxResponseBytes + 1])
        });
        await harness.Provider.PollOnceAsync();
        var oversized = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, oversized.State);
        Assert.AreEqual("response_too_large", oversized.ErrorCode);
        Assert.IsNull(oversized.Value);
    }

    [TestMethod]
    public async Task ChunkedOversizeBodyIsRejectedAndRequestDeadlineCancelsTransport()
    {
        using var harness = new Harness();
        harness.Handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[CodexQuotaProvider.MaxResponseBytes + 32])
        });
        await harness.Provider.PollOnceAsync();
        var tooLarge = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, tooLarge.State);
        Assert.AreEqual("response_too_large", tooLarge.ErrorCode);

        harness.Time.Advance(CodexQuotaProvider.PollInterval);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var pending = harness.Provider.PollOnceAsync();
        await entered.Task;
        harness.Time.Advance(CodexQuotaProvider.RequestTimeout);
        await pending;
        var timeout = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, timeout.State);
        Assert.AreEqual("timeout", timeout.ErrorCode);
    }

    [TestMethod]
    public async Task LocalStatesKeepPublishingWhileTheUsageRequestIsSlow()
    {
        using var harness = new Harness();
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var initialReads = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return JsonResponse(harness.Fixture);
        });
        using var stop = new CancellationTokenSource();
        var run = Program.KeepServiceAliveAfterProviderExitAsync(
            () => harness.Provider.RunAsync(stop.Token), "codex", stop.Token);
        await entered.Task;
        Assert.IsFalse(run.IsCompleted);

        var initial = await Task.WhenAll(initialReads);
        Assert.IsTrue(initial.All(result => result));
        Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Unavailable));
        var next = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var republished = await Task.WhenAll(next);
        Assert.IsTrue(republished.All(result => result));
        Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Unavailable));

        stop.Cancel();
        release.TrySetResult();
        try { await run; }
        catch (OperationCanceledException) { }
        foreach (var reader in readers) await reader.DisposeAsync();
    }

    [TestMethod]
    public async Task MissingCodexAuthKeepsServiceRunnerAliveUntilShutdown()
    {
        using var harness = new Harness();
        File.Delete(Path.Combine(harness.Home, "auth.json"));
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        using var stop = new CancellationTokenSource();
        var first = reader.MoveNextAsync().AsTask();
        var runner = Program.KeepServiceAliveAfterProviderExitAsync(
            () => harness.Provider.RunAsync(stop.Token), "codex", stop.Token);

        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.IsFalse(runner.IsCompleted);

        var stopped = reader.MoveNextAsync().AsTask();
        stop.Cancel();
        await runner.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
    }

    [TestMethod]
    public async Task RejectedCodexAuthKeepsServiceRunnerAliveUntilShutdown()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        await harness.Provider.PollOnceAsync();

        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        using var stop = new CancellationTokenSource();
        var first = reader.MoveNextAsync().AsTask();
        var runner = Program.KeepServiceAliveAfterProviderExitAsync(
            () => harness.Provider.RunAsync(stop.Token), "codex", stop.Token);

        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Error, reader.Current.State);
        Assert.AreEqual("auth_required", reader.Current.ErrorCode);
        Assert.IsFalse(runner.IsCompleted);

        var stopped = reader.MoveNextAsync().AsTask();
        stop.Cancel();
        await runner.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
    }

    [TestMethod]
    public async Task UnexpectedCodexRunnerFailureDoesNotCompleteServiceWait()
    {
        using var harness = new Harness();
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var stopped = reader.MoveNextAsync().AsTask();
        harness.Time.ThrowOnNextTimestamp = true;
        using var stop = new CancellationTokenSource();
        Task? providerRun = null;
        var runner = Program.KeepServiceAliveAfterProviderExitAsync(() =>
        {
            providerRun = harness.Provider.RunAsync(stop.Token);
            return providerRun;
        }, "codex", stop.Token);
        Assert.IsNotNull(providerRun);
        try
        {
            await providerRun.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Fail("The injected clock failure should fault the provider runner.");
        }
        catch (InvalidOperationException) { }

        var serviceTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serviceWait = Task.WhenAny(serviceTask.Task, runner);
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        Assert.IsFalse(serviceWait.IsCompleted);

        stop.Cancel();
        await runner.WaitAsync(TimeSpan.FromSeconds(1));
        serviceTask.SetResult();
        await serviceWait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task SynchronousProviderStartFailureDoesNotEscapeServiceRunner()
    {
        using var stop = new CancellationTokenSource();
        var runner = Program.KeepServiceAliveAfterProviderExitAsync(
            static () => throw new InvalidOperationException("Synthetic start failure."), "codex", stop.Token);

        Assert.IsFalse(runner.IsCompleted);
        stop.Cancel();
        await runner.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("cursor")]
    [DataRow("antigravity")]
    [DataRow("zcode")]
    [DataRow("github-copilot")]
    [DataRow("opencode-go")]
    [DataRow("kiro")]
    [DataRow("command-code")]
    public async Task UnexpectedProviderFailureStaysIsolatedUntilBoundedShutdown(string providerId)
    {
        using var stop = new CancellationTokenSource();
        var runner = Program.KeepServiceAliveAfterProviderExitAsync(
            static () => Task.FromException(new InvalidOperationException("Synthetic provider failure.")),
            providerId, stop.Token);

        Assert.IsFalse(runner.IsCompleted);
        stop.Cancel();
        await runner.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("missing")]
    [DataRow("auth-failed")]
    [DataRow("slow")]
    public async Task HardwarePresentMonCodexCursorAndAntigravityPublishIndependentlyOnTheSameBus(
        string cursorOutcome)
    {
        using var codexHarness = new Harness();
        var registry = codexHarness.Registry;
        var bus = codexHarness.Bus;
        using var hardwareProvider = new HardwareTelemetryProvider(registry, bus);

        await using var cpuReader = bus.Subscribe(new MetricId("cpu.total.usage")).GetAsyncEnumerator();
        await using var memoryReader = bus.Subscribe(new MetricId("ram.usage")).GetAsyncEnumerator();
        await using var presentMonReader = bus.Subscribe(new MetricId("game.fps.display")).GetAsyncEnumerator();
        await using var codexReader = bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        await using var cursorReader = bus.Subscribe(new MetricId("ai.cursor.quota.plan.total.used_percent"))
            .GetAsyncEnumerator();
        await using var antigravityReader = bus.Subscribe(new MetricId("ai.antigravity.quota.3p-weekly.remaining"))
            .GetAsyncEnumerator();
        var cpuRead = cpuReader.MoveNextAsync().AsTask();
        var memoryRead = memoryReader.MoveNextAsync().AsTask();
        var presentMonRead = presentMonReader.MoveNextAsync().AsTask();
        var codexRead = codexReader.MoveNextAsync().AsTask();
        var cursorRead = cursorReader.MoveNextAsync().AsTask();
        var antigravityRead = antigravityReader.MoveNextAsync().AsTask();

        codexHarness.Handler.EnqueueJson(codexHarness.Fixture);
        await hardwareProvider.PublishNextAsync();
        await PresentMonTelemetryProviderTests.PublishSyntheticReadingAsync(registry, bus);
        await codexHarness.Provider.PollOnceAsync();
        await codexHarness.Provider.PublishCurrentAsync();
        await CursorQuotaProviderTests.PublishSyntheticOutcomeAsync(registry, bus, cursorOutcome);
        await AntigravityQuotaProviderTests.PublishSyntheticQuotaAsync(registry, bus);

        var received = await Task.WhenAll(cpuRead, memoryRead, presentMonRead, codexRead, cursorRead, antigravityRead)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(received.All(result => result), "Every provider should deliver through the subscribed shared bus.");

        var cpu = cpuReader.Current;
        var cpuMeasured = cpu.State == MetricState.Fresh && cpu.Value.HasValue &&
            cpu.Confidence == MetricConfidence.Measured;
        var cpuUnavailable = cpu.State == MetricState.Unavailable && !cpu.Value.HasValue &&
            cpu.Confidence == MetricConfidence.Unknown;
        Assert.IsTrue(cpuMeasured || cpuUnavailable,
            $"Real CPU telemetry must be measured or truthfully unavailable; received {cpu.State}/{cpu.Confidence}.");

        Assert.AreEqual(MetricState.Fresh, memoryReader.Current.State);
        Assert.AreEqual(MetricConfidence.Measured, memoryReader.Current.Confidence);
        Assert.IsTrue(memoryReader.Current.Value.HasValue);
        Assert.AreEqual("GlobalMemoryStatusEx", memoryReader.Current.Source);

        Assert.AreEqual(MetricState.Fresh, presentMonReader.Current.State);
        Assert.AreEqual(MetricConfidence.Measured, presentMonReader.Current.Confidence);
        Assert.IsTrue(presentMonReader.Current.Value.HasValue);
        Assert.AreEqual(MetricState.Fresh, codexReader.Current.State);
        Assert.AreEqual(MetricConfidence.Measured, codexReader.Current.Confidence);
        switch (cursorOutcome)
        {
            case "success":
                Assert.AreEqual(MetricState.Fresh, cursorReader.Current.State);
                Assert.AreEqual(MetricConfidence.Measured, cursorReader.Current.Confidence);
                Assert.IsTrue(cursorReader.Current.Value.HasValue);
                break;
            case "missing":
                Assert.AreEqual(MetricState.Unavailable, cursorReader.Current.State);
                Assert.AreEqual(MetricConfidence.Unknown, cursorReader.Current.Confidence);
                Assert.IsNull(cursorReader.Current.Value);
                break;
            case "auth-failed":
                Assert.AreEqual(MetricState.Error, cursorReader.Current.State);
                Assert.AreEqual("auth_rejected", cursorReader.Current.ErrorCode);
                Assert.IsNull(cursorReader.Current.Value);
                break;
            case "slow":
                Assert.AreEqual(MetricState.Error, cursorReader.Current.State);
                Assert.IsNull(cursorReader.Current.Value);
                break;
        }
        Assert.AreEqual(MetricState.Stale, antigravityReader.Current.State);
        Assert.AreEqual(MetricConfidence.Measured, antigravityReader.Current.Confidence);
    }

    [TestMethod]
    public async Task HardwareAndAcceptedProvidersPublishTwiceWhileCursorRequestIsGated()
    {
        using var codexHarness = new Harness();
        var registry = codexHarness.Registry;
        var bus = codexHarness.Bus;
        using var hardwareProvider = new HardwareTelemetryProvider(registry, bus);
        using var presentMonProvider = PresentMonTelemetryProviderTests.CreateSyntheticProvider(registry, bus);
        using var antigravityProvider = AntigravityQuotaProviderTests.CreateSyntheticProvider(registry, bus);
        using var zcodeHarness = new ZCodeQuotaProviderTests.Harness(registry, bus);
        var copilotResponses = new Queue<string>();
        const string accountId = "24680";
        const string authStatus = "{\"state\":\"success\",\"tokenSource\":\"keyring\"}";
        const string copilotIdentity = "{\"id\":" + accountId + "}";
        const string copilotQuota = "{\"id\":" + accountId +
            ",\"token_based_billing\":true,\"quota_snapshots\":{" +
            "\"chat\":{\"token_based_billing\":true,\"entitlement\":50,\"credits_used\":10," +
            "\"quota_remaining\":40,\"percent_remaining\":80,\"quota_reset_at\":4102444800," +
            "\"unlimited\":false,\"has_quota\":false}}}";
        for (var round = 0; round < 2; round++)
        {
            copilotResponses.Enqueue(authStatus);
            copilotResponses.Enqueue(copilotIdentity);
            copilotResponses.Enqueue(copilotQuota);
            copilotResponses.Enqueue(copilotIdentity);
        }
        var copilotTime = new TestTimeProvider(DateTimeOffset.UtcNow);
        var copilotAcquirer = new CopilotQuotaAcquirer((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Encoding.UTF8.GetBytes(copilotResponses.Dequeue()));
        });
        using var copilotProvider = new CopilotQuotaProvider(registry, bus, copilotAcquirer, copilotTime);

        const string kiroUsage = """
            {"userInfo":{"userId":"synthetic-user","accountId":"synthetic-account"},
             "subscriptionInfo":{"nextDateReset":1893456000},
             "usageBreakdownList":[{"resourceType":"CREDIT","currentUsageWithPrecision":"12.75","usageLimitWithPrecision":"100"}]}
            """;
        using var kiroProvider = new KiroQuotaProvider(registry, bus,
            _ => Task.FromResult(KiroAcquisition.Ready(Encoding.UTF8.GetBytes(kiroUsage))), TimeProvider.System,
            _ => Task.FromResult(KiroIdentityObservation.Ready(new byte[32])));

        using var commandHandler = new RecordingHandler();
        commandHandler.EnqueueJson("{\"data\":{\"org\":{\"id\":\"synthetic-org\"}}}");
        commandHandler.EnqueueJson("""
            {"windowLimits":{"fiveHour":{"used":25,"cap":100,"resetAt":4102444800000},
              "weekly":{"used":5,"cap":20,"resetAt":4103049600000}}}
            """);
        commandHandler.EnqueueJson("{\"data\":{\"planId\":\"individual-pro-v1\",\"status\":\"active\"}}");
        using var commandClient = new HttpClient(commandHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var commandAuthReader = new CommandCodeQuotaAuthReader(
            () => "synthetic-command-code-key", () => null, () => null, _ => null);
        using var commandCodeProvider = new CommandCodeQuotaProvider(
            registry, bus, commandAuthReader, commandClient, TimeProvider.System);

        const string authContent = "{\"opencode-go\":{\"type\":\"api\",\"key\":\"synthetic-opencode-go-key\"}}";
        var authReader = new OpenCodeGoAuthReader(getEnvironmentVariable: name =>
            StringComparer.Ordinal.Equals(name, "OPENCODE_AUTH_CONTENT") ? authContent : null);
        using var openCodeHandler = new RecordingHandler();
        openCodeHandler.EnqueueJson("""
            {"usage":{
              "rolling":{"percent":37.5,"resetsAt":"2099-10-01T00:00:00Z"},
              "weekly":{"percent":12.5,"resetsAt":"2099-10-08T00:00:00Z"},
              "monthly":{"percent":4,"resetsAt":"2099-11-01T00:00:00Z"}
            }}
            """);
        using var openCodeProvider = new OpenCodeGoQuotaProvider(registry, bus, authReader,
            openCodeHandler, TimeProvider.System, _ => 0);
        zcodeHarness.Handler.EnqueueJson(zcodeHarness.Fixture);
        await zcodeHarness.Provider.PollOnceAsync();
        await kiroProvider.PollOnceAsync();
        await commandCodeProvider.PollOnceAsync();
        await using var cpuReader = bus.Subscribe(new MetricId("cpu.total.usage")).GetAsyncEnumerator();
        await using var memoryReader = bus.Subscribe(new MetricId("ram.usage")).GetAsyncEnumerator();
        await using var presentMonReader = bus.Subscribe(new MetricId("game.fps.display")).GetAsyncEnumerator();
        await using var codexReader = bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        await using var antigravityReader = bus.Subscribe(new MetricId("ai.antigravity.quota.3p-weekly.remaining"))
            .GetAsyncEnumerator();
        await using var copilotReader = bus.Subscribe(new MetricId(
            "ai.github_copilot.quota.chat.percent_remaining")).GetAsyncEnumerator();
        await using var openCodeReader = bus.Subscribe(new MetricId(
            "ai.opencode-go.quota.rolling.used")).GetAsyncEnumerator();
        await using var zcodeReader = bus.Subscribe(new MetricId(
            "ai.zcode.coding_plan.five_hour.remaining_percent")).GetAsyncEnumerator();
        await using var kiroReader = bus.Subscribe(new MetricId(
            "ai.kiro.credits.included.remaining")).GetAsyncEnumerator();
        await using var commandCodeReader = bus.Subscribe(new MetricId(
            "ai.commandcode.quota.5h.used_percent")).GetAsyncEnumerator();

        codexHarness.Handler.EnqueueJson(codexHarness.Fixture);
        await codexHarness.Provider.PollOnceAsync();
        await antigravityProvider.PollOnceAsync();
        await openCodeProvider.PollOnceAsync();

        async Task PublishAndReadRoundAsync()
        {
            var cpuRead = cpuReader.MoveNextAsync().AsTask();
            var memoryRead = memoryReader.MoveNextAsync().AsTask();
            var presentMonRead = presentMonReader.MoveNextAsync().AsTask();
            var codexRead = codexReader.MoveNextAsync().AsTask();
            var antigravityRead = antigravityReader.MoveNextAsync().AsTask();
            var copilotRead = copilotReader.MoveNextAsync().AsTask();
            var openCodeRead = openCodeReader.MoveNextAsync().AsTask();
            var zcodeRead = zcodeReader.MoveNextAsync().AsTask();
            var kiroRead = kiroReader.MoveNextAsync().AsTask();
            var commandCodeRead = commandCodeReader.MoveNextAsync().AsTask();

            await hardwareProvider.PublishNextAsync();
            await presentMonProvider.PublishNextAsync();
            await codexHarness.Provider.PublishCurrentAsync();
            await antigravityProvider.PublishCurrentAsync();
            await copilotProvider.PollOnceAsync();
            await openCodeProvider.PublishCurrentAsync();
            await zcodeHarness.Provider.PublishCurrentAsync();
            await kiroProvider.PublishOnceAsync();
            await commandCodeProvider.PublishCurrentAsync();

            var received = await Task.WhenAll(cpuRead, memoryRead, presentMonRead, codexRead, antigravityRead,
                    copilotRead, openCodeRead, zcodeRead, kiroRead, commandCodeRead)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(received.All(result => result), "Every independent source must publish during the gated Cursor request.");

            var cpu = cpuReader.Current;
            var cpuMeasured = cpu.State == MetricState.Fresh && cpu.Value.HasValue &&
                cpu.Confidence == MetricConfidence.Measured;
            var cpuUnavailable = cpu.State == MetricState.Unavailable && !cpu.Value.HasValue &&
                cpu.Confidence == MetricConfidence.Unknown;
            Assert.IsTrue(cpuMeasured || cpuUnavailable,
                $"Real CPU telemetry must be measured or truthfully unavailable; received {cpu.State}/{cpu.Confidence}.");

            var memory = memoryReader.Current;
            Assert.AreEqual(MetricState.Fresh, memory.State);
            Assert.AreEqual(MetricConfidence.Measured, memory.Confidence);
            Assert.IsTrue(memory.Value.HasValue);
            Assert.AreEqual("GlobalMemoryStatusEx", memory.Source);

            Assert.AreEqual(MetricState.Fresh, presentMonReader.Current.State);
            Assert.AreEqual(MetricConfidence.Measured, presentMonReader.Current.Confidence);
            Assert.IsTrue(presentMonReader.Current.Value.HasValue);
            Assert.AreEqual(MetricState.Fresh, codexReader.Current.State);
            Assert.AreEqual(MetricConfidence.Measured, codexReader.Current.Confidence);
            Assert.AreEqual(MetricState.Stale, antigravityReader.Current.State);
            Assert.AreEqual(MetricConfidence.Measured, antigravityReader.Current.Confidence);
            Assert.AreEqual(MetricState.Fresh, copilotReader.Current.State);
            Assert.AreEqual(80d, copilotReader.Current.Value!.Value.AsDouble(), 0.000001);
            Assert.AreEqual(MetricState.Fresh, openCodeReader.Current.State);
            Assert.AreEqual(37.5d, openCodeReader.Current.Value!.Value.AsDouble(), 0.000001);
            Assert.AreEqual(MetricState.Fresh, zcodeReader.Current.State);
            Assert.AreEqual(MetricConfidence.Measured, zcodeReader.Current.Confidence);
            Assert.AreEqual(75d, zcodeReader.Current.Value!.Value.AsDouble(), 0.001);
            Assert.AreEqual(MetricState.Fresh, kiroReader.Current.State);
            Assert.AreEqual(MetricConfidence.Measured, kiroReader.Current.Confidence);
            Assert.AreEqual(87.25m, kiroReader.Current.Value!.Value.AsDecimal());
            Assert.AreEqual("Kiro.Usage.ACP.v3", kiroReader.Current.Source);
            Assert.AreEqual(MetricState.Fresh, commandCodeReader.Current.State);
            Assert.AreEqual(MetricConfidence.Measured, commandCodeReader.Current.Confidence);
            Assert.AreEqual(25d, commandCodeReader.Current.Value!.Value.AsDouble(), 0.000001);
            Assert.AreEqual("CommandCode.ObservedBillingApi.v1", commandCodeReader.Current.Source);
        }

        await CursorQuotaProviderTests.RunWithPendingQuotaRequestAsync(registry, bus, async () =>
        {
            await PublishAndReadRoundAsync();
            copilotTime.Advance(CopilotQuotaProvider.PollInterval);
            await PublishAndReadRoundAsync();
        });
    }

    [TestMethod]
    public async Task OwnerChangeCancelsBlockedPublishButCannotRetractBufferedPreTransitionSnapshot()
    {
        using var harness = new Harness();
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var initial = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);

        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        await harness.Provider.PublishCurrentAsync();
        var blockedPublish = harness.Provider.PublishCurrentAsync().AsTask();
        Assert.IsFalse(blockedPublish.IsCompleted);

        harness.WriteAuth("synthetic-access-B", "synthetic-account-secondary", "synthetic-subject-B", "synthetic-user-B");
        await harness.Provider.ObserveLocalStateAsync();
        await blockedPublish.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsTrue(reader.Current.Value.HasValue);
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        var next = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await next);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
    }

    [TestMethod]
    public async Task RunPublishesValuelessStoppedMetricsOnShutdown()
    {
        using var harness = new Harness();
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var initialReads = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var stop = new CancellationTokenSource();
        try
        {
            var run = harness.Provider.RunAsync(stop.Token);
            await Task.WhenAll(initialReads);
            await entered.Task;
            var stoppedReads = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            stop.Cancel();
            try { await run; }
            catch (OperationCanceledException) { }
            Assert.IsTrue((await Task.WhenAll(stoppedReads)).All(result => result));
            Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Stopped &&
                reader.Current.Value is null && reader.Current.Confidence == MetricConfidence.Unknown));
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposeCancelsAndDrainsAnInFlightRequest()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var pending = harness.Provider.PollOnceAsync();
        await entered.Task;
        await Task.Run(harness.Provider.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
        var canceled = false;
        try { await pending; }
        catch (OperationCanceledException) { canceled = true; }
        Assert.IsTrue(canceled);
    }

    [TestMethod]
    public void AuthIdentityIsStableAcrossTokenRotationAndChangesWithSelectedOwner()
    {
        using var harness = new Harness();
        var first = harness.AuthReader.Read();
        Assert.AreEqual(CodexAuthStatus.Ready, first.Status);
        harness.WriteAuth("synthetic-access-B", "synthetic-account-primary", "synthetic-subject-A", "synthetic-user-A");
        var rotated = harness.AuthReader.Read();
        Assert.AreEqual(first.Identity!.StableId, rotated.Identity!.StableId);
        harness.WriteAuth("synthetic-access-C", "synthetic-account-secondary", "synthetic-subject-B", "synthetic-user-B");
        var changedOwner = harness.AuthReader.Read();
        Assert.AreNotEqual(first.Identity.StableId, changedOwner.Identity!.StableId);
    }

    [TestMethod]
    public async Task NonstandardConfiguredRouteDoesNotIssueARequest()
    {
        using var harness = new Harness();
        File.WriteAllText(Path.Combine(harness.Home, "config.toml"), "openai_base_url = \"https://route.invalid\"\n");
        await harness.Provider.PollOnceAsync();

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        var snapshot = await ReadOneAsync(harness, Ids[0]);
        Assert.AreEqual(MetricState.Error, snapshot.State);
        Assert.AreEqual("route_unsupported", snapshot.ErrorCode);
    }

    [TestMethod]
    public async Task EscapedAndMalformedRoutingConfigFailsClosed()
    {
        var escapedKey = "\"" + '\\' + "u0063hatgpt_base_url\" = \"https://route.invalid\"";
        foreach (var config in new[]
        {
            escapedKey,
            "\"unterminated = \"https://route.invalid\"",
            "not a TOML assignment",
            "model = \"unterminated",
            "cli_auth_credentials_store = \"file",
            "cli_auth_credentials_store = file",
            "cli_auth_credentials_store = \"\"file\"\"",
            "items = [1"
        })
        {
            using var harness = new Harness();
            File.WriteAllText(Path.Combine(harness.Home, "config.toml"), config);
            Assert.AreEqual(CodexAuthStatus.Unsupported, harness.AuthReader.Read().Status);
            await harness.Provider.PollOnceAsync();
            Assert.AreEqual(0, harness.Handler.Requests.Length);
        }
    }

    [TestMethod]
    public void AuthReaderRejectsOversizedFileAndNonChatGptAuth()
    {
        using var harness = new Harness();
        File.WriteAllText(Path.Combine(harness.Home, "auth.json"), new string(' ', CodexQuotaAuthReader.MaxFileBytes + 1));
        Assert.AreEqual(CodexAuthStatus.Error, harness.AuthReader.Read().Status);
        File.WriteAllText(Path.Combine(harness.Home, "auth.json"), "{\"auth_mode\":\"api_key\"}");
        Assert.AreEqual(CodexAuthStatus.Unsupported, harness.AuthReader.Read().Status);
    }

    private static CodexQuotaReading Parse(string json) => CodexQuotaParser.Parse(Encoding.UTF8.GetBytes(json),
        "synthetic-account-primary", "synthetic-user-A", InitialUtc, TimeSpan.FromMilliseconds(250));

    private static async Task AssertOwnerChangeDiscardsResponseAsync(bool success)
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return success ? JsonResponse(harness.Fixture) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var pending = harness.Provider.PollOnceAsync();
        await entered.Task;
        harness.WriteAuth("synthetic-access-B", "synthetic-account-secondary", "synthetic-subject-B", "synthetic-user-B");
        await harness.Provider.ObserveLocalStateAsync();
        release.SetResult();
        await pending;
        var snapshots = await ReadAllAsync(harness);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Value is null && snapshot.State == MetricState.Unavailable));
    }

    private static async Task<MetricSnapshot> ReadOneAsync(Harness harness, string id)
    {
        await using var reader = harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await pending);
        return reader.Current;
    }

    private static async Task<MetricSnapshot[]> ReadAllAfterPublishAsync(Harness harness)
    {
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        await harness.Provider.PollOnceAsync();
        await harness.Provider.PublishCurrentAsync();
        await Task.WhenAll(pending);
        harness.ReadSnapshots = readers.Select(reader => reader.Current).ToArray();
        foreach (var reader in readers) await reader.DisposeAsync();
        return harness.ReadSnapshots;
    }

    private static async Task<MetricSnapshot[]> ReadAllAsync(Harness harness)
    {
        var result = await ReadAllAfterPublishAsyncWithoutPoll(harness);
        return result;
    }

    private static async Task<MetricSnapshot[]> ReadAllAfterPublishAsyncWithoutPoll(Harness harness)
    {
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        await harness.Provider.PublishCurrentAsync();
        await Task.WhenAll(pending);
        var snapshots = readers.Select(reader => reader.Current).ToArray();
        foreach (var reader in readers) await reader.DisposeAsync();
        return snapshots;
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return response;
    }

    private sealed class Harness : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "cerith-codex-test-" + Guid.NewGuid().ToString("N"));
        public string Fixture { get; } = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codex",
            "usage.synthetic.fixture.json"));
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public TestTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public CodexQuotaAuthReader AuthReader { get; }
        public CodexQuotaProvider Provider { get; }
        public MetricSnapshot[] ReadSnapshots { get; set; } = [];

        public Harness()
        {
            Directory.CreateDirectory(Home);
            WriteAuth("synthetic-access-A", "synthetic-account-primary", "synthetic-subject-A", "synthetic-user-A");
            AuthReader = new CodexQuotaAuthReader(Home, static () =>
            {
                var key = new byte[32];
                Array.Fill(key, (byte)0x53);
                return key;
            });
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Provider = new CodexQuotaProvider(Registry, Bus, AuthReader, Client, Time, static _ => 0);
        }

        public void WriteAuth(string token, string accountId, string subject, string userId)
        {
            var claim = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["https://api.openai.com/auth"] = new
                {
                    chatgpt_user_id = userId,
                    chatgpt_account_id = accountId
                }
            });
            var claimBytes = Encoding.UTF8.GetBytes(claim);
            var encoded = Convert.ToBase64String(claimBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var idToken = "synthetic." + encoded + ".signature";
            var contents = JsonSerializer.Serialize(new
            {
                auth_mode = "chatgpt",
                tokens = new { access_token = token, account_id = accountId, id_token = idToken }
            });
            File.WriteAllText(Path.Combine(Home, "auth.json"), contents);
        }

        public void Dispose()
        {
            Provider.Dispose();
            Client.Dispose();
            Bus.Dispose();
            try { Directory.Delete(Home, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
        private readonly ConcurrentQueue<RequestObservation> _requests = new();
        public RequestObservation[] Requests => _requests.ToArray();

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue((_, _) => Task.FromResult(response));
        public void EnqueueJson(string json) => Enqueue(JsonResponse(json));
        public void EnqueueStatus(HttpStatusCode status, string? retryAfter = null)
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is not null) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(int.Parse(retryAfter)));
            Enqueue(response);
        }
        public void EnqueueRedirect()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/redirect");
            Enqueue(response);
        }
        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var authorization = request.Headers.Authorization;
            var account = request.Headers.TryGetValues("ChatGPT-Account-Id", out var values) ? values.Single() : null;
            _requests.Enqueue(new RequestObservation(request.Method, request.RequestUri!, authorization?.Scheme,
                authorization?.Parameter, account, request.Headers.UserAgent.ToString(), request.Content?.Headers.ContentType?.MediaType));
            var result = _responses.TryDequeue(out var response)
                ? await response(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            result.RequestMessage ??= request;
            return result;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, string? AuthorizationScheme,
        string? AccessToken, string? AccountId, string UserAgent, string? ContentType);

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly ConcurrentBag<ManualTimer> _timers = new();
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;
        public bool ThrowOnNextTimestamp { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp()
        {
            lock (_gate)
            {
                if (ThrowOnNextTimestamp)
                {
                    ThrowOnNextTimestamp = false;
                    throw new InvalidOperationException("Synthetic clock failure.");
                }
                return _timestamp;
            }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] timers;
            long now;
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
                now = _timestamp;
                timers = _timers.ToArray();
            }
            foreach (var timer in timers) timer.FireDue(now);
        }

        public void ShiftUtcOnly(TimeSpan elapsed)
        {
            lock (_gate) _utcNow += elapsed;
        }

        private sealed class ManualTimer(TestTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private readonly object _gate = new();
            private long _due = long.MaxValue;
            private long _period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed) return false;
                    var now = owner.GetTimestamp();
                    _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : now + dueTime.Ticks;
                    _period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
                    return true;
                }
            }

            public void FireDue(long now)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed || _due > now) return;
                        _due = _period == 0 ? long.MaxValue : _due + _period;
                    }
                    callback(state);
                }
            }

            public void Dispose() { lock (_gate) _disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
