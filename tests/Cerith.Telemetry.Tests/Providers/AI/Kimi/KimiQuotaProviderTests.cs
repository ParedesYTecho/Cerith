#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Kimi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Kimi;

[TestClass]
public sealed class KimiQuotaProviderTests
{
    private static readonly string[] Ids =
    [
        "ai.kimi.quota.5h.remaining", "ai.kimi.quota.5h.reset",
        "ai.kimi.quota.7d.remaining", "ai.kimi.quota.7d.reset",
        "ai.kimi.quota.monthly.remaining", "ai.kimi.quota.monthly.reset"
    ];

    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RegistersSixTypedMetricsForOnlyVerifiedQuotaWindows()
    {
        using var registry = new MetricBus(4);
        var metrics = new MetricRegistry();
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using var provider = new KimiQuotaProvider(metrics, registry, new KimiQuotaAuthReader(),
            client, TimeProvider.System);

        var descriptors = metrics.GetSnapshot().OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(Ids.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            descriptors.Select(item => item.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { MetricValueKind.Double, MetricValueKind.DateTimeOffset,
                MetricValueKind.Double, MetricValueKind.DateTimeOffset, MetricValueKind.Double,
                MetricValueKind.DateTimeOffset },
            descriptors.Select(item => item.ValueKind).ToArray());
        Assert.AreEqual("%", descriptors.Single(item => item.Id.Value == Ids[0]).Unit);
        Assert.AreEqual("quota_remaining", descriptors.Single(item => item.Id.Value == Ids[0]).SemanticType);
    }

    [TestMethod]
    public void EndpointSelectionAllowsOnlyOfficialHttpsHosts()
    {
        Assert.AreEqual(KimiQuotaProvider.UsageEndpoint, KimiQuotaProvider.ResolveUsageEndpoint(null));
        Assert.IsNull(KimiQuotaProvider.ResolveUsageEndpoint("https://api.kimi.ai/coding/v1/"),
            "The global route uses a scoped OAuth slot and must not reuse the default credential.");
        Assert.IsNull(KimiQuotaProvider.ResolveUsageEndpoint("http://api.kimi.com/coding/v1"));
        Assert.IsNull(KimiQuotaProvider.ResolveUsageEndpoint("https://api.kimi.com.attacker.invalid/coding/v1"));
        Assert.IsNull(KimiQuotaProvider.ResolveUsageEndpoint("https://api.kimi.com/coding/v1?redirect=evil"));
    }

    [TestMethod]
    public void ParserMapsRatiosAndResetsWithoutTreatingCodeSplitOrWalletAsQuota()
    {
        const string json = """
            {"usages":{"limit_5h":{"used_ratio":0.25,"reset_time":"2026-09-30T17:00:00Z"},"limit_7d":{"used_ratio":"0.5","reset_time":"2026-10-06T12:00:00+00:00"},"limit_month_total":{"used_ratio":1,"reset_time":"2026-10-31T00:00:00Z"},"limit_month_code":{"used_ratio":0.1,"reset_time":"2026-09-30T14:00:00Z"}},"boosterWallet":{"balance":100}}
            """;

        var reading = KimiQuotaParser.Parse(Encoding.UTF8.GetBytes(json), InitialUtc);
        Assert.AreEqual(75d, reading.Points[KimiQuotaParser.FiveHourRemaining].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-30T17:00:00Z"),
            reading.Points[KimiQuotaParser.FiveHourReset].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(50d, reading.Points[KimiQuotaParser.SevenDayRemaining].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(0d, reading.Points[KimiQuotaParser.MonthlyRemaining].Value!.Value.AsDouble());
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-31T00:00:00Z"),
            reading.Points[KimiQuotaParser.MonthlyReset].Value!.Value.AsDateTimeOffset());
    }

    [TestMethod]
    public void MissingLegacyWindowRemainsUnavailableAndOldPayloadIsNotGuessed()
    {
        const string current = """
            {"usages":{"limit_5h":{"used_ratio":0.2},"limit_month_total":{"used_ratio":0.4}}}
            """;
        var reading = KimiQuotaParser.Parse(Encoding.UTF8.GetBytes(current), InitialUtc);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[KimiQuotaParser.SevenDayRemaining].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[KimiQuotaParser.SevenDayReset].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[KimiQuotaParser.FiveHourReset].State);

        var oldShape = KimiQuotaParser.Parse(Encoding.UTF8.GetBytes("{\"usage\":{},\"limits\":[{}]}"), InitialUtc);
        Assert.IsTrue(oldShape.Points.All(point => point.State == MetricState.Error && point.ErrorCode == "schema_invalid"));
    }

    [TestMethod]
    public void InvalidRatioAndResetFailIndependentlyAndElapsedWindowIsUnavailable()
    {
        const string invalid = """
            {"usages":{"limit_5h":{"used_ratio":1.2,"reset_time":"2026-09-30T15:00:00Z"},"limit_7d":{"used_ratio":0.25,"reset_time":"2026-10-06T12:00:00"}}}
            """;
        var reading = KimiQuotaParser.Parse(Encoding.UTF8.GetBytes(invalid), InitialUtc);
        Assert.AreEqual("invalid_quota", reading.Points[0].ErrorCode);
        Assert.AreEqual(MetricState.Fresh, reading.Points[1].State);
        Assert.AreEqual(MetricState.Fresh, reading.Points[2].State);
        Assert.AreEqual("invalid_reset", reading.Points[3].ErrorCode);

        const string elapsed = """
            {"usages":{"limit_5h":{"used_ratio":0,"reset_time":"2026-09-30T11:59:59Z"}}}
            """;
        var expired = KimiQuotaParser.Parse(Encoding.UTF8.GetBytes(elapsed), InitialUtc);
        Assert.AreEqual(MetricState.Unavailable, expired.Points[0].State);
        Assert.AreEqual("reset_elapsed", expired.Points[0].ErrorCode);
        Assert.AreEqual(MetricState.Unavailable, expired.Points[1].State);
    }

    [TestMethod]
    public async Task ReadOnlyAcquisitionUsesFixedHttpsGetAndPublishesSourceValues()
    {
        await using var harness = new Harness();
        harness.WriteCredentials("synthetic-access-token");
        harness.Responder = (request, _) => Task.FromResult(JsonResponse(request, HttpStatusCode.OK, ValidUsage));

        var snapshots = await harness.PollAndReadAsync();
        var request = harness.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(KimiQuotaProvider.UsageEndpoint, request.Uri);
        Assert.AreEqual("Bearer", request.AuthScheme);
        Assert.AreEqual("synthetic-access-token", request.AuthParameter);
        Assert.AreEqual(80d, snapshots[0].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(MetricState.Fresh, snapshots[0].State);
        Assert.AreEqual("Kimi.Usage.ReadOnly.v1", snapshots[0].Source);
        Assert.IsFalse(snapshots.Any(snapshot => snapshot.Source?.Contains("synthetic-access-token", StringComparison.Ordinal) == true ||
            snapshot.ErrorCode?.Contains("synthetic-access-token", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task TokenRotationDuringFetchRejectsResponseAndClearsOwnerCache()
    {
        await using var harness = new Harness();
        harness.WriteCredentials("token-before");
        harness.Responder = (request, _) =>
        {
            harness.WriteCredentials("token-after");
            return Task.FromResult(JsonResponse(request, HttpStatusCode.OK, ValidUsage));
        };

        var snapshots = await harness.PollAndReadAsync();
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        Assert.AreEqual(1, harness.Requests.Count);
    }

    [TestMethod]
    public async Task TokenRotationCancelsSnapshotBlockedBySubscriberBackpressure()
    {
        await using var harness = new Harness(subscriberCapacity: 1, subscribeAll: false);
        harness.WriteCredentials("token-before");
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await Task.Yield();
        await harness.Provider.PollOnceAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));

        await harness.Provider.PublishCurrentAsync();
        var blockedPublication = harness.Provider.PublishCurrentAsync().AsTask();
        Assert.IsFalse(blockedPublication.IsCompleted, "The unconsumed bounded channel should hold publication.");

        harness.WriteCredentials("token-after");
        await harness.Provider.ObserveLocalStateAsync();
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "The snapshot already buffered before the owner transition may still be consumed.");
        var nextRead = reader.MoveNextAsync().AsTask();
        Assert.IsFalse(nextRead.IsCompleted, "No second pre-transition snapshot may remain queued.");
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await nextRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
    }

    [TestMethod]
    [DataRow("reset", "reset_elapsed")]
    [DataRow("cache", "cache_expired")]
    public async Task CacheInvalidationCancelsSnapshotBlockedBySubscriberBackpressure(string expiry, string errorCode)
    {
        await using var harness = new Harness(subscriberCapacity: 1, subscribeAll: false);
        harness.WriteCredentials("synthetic-token");
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await Task.Yield();
        await harness.Provider.PollOnceAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await harness.Provider.PublishCurrentAsync();
        var blockedPublication = harness.Provider.PublishCurrentAsync().AsTask();
        Assert.IsFalse(blockedPublication.IsCompleted, "The unconsumed bounded channel should hold publication.");

        harness.Time.Advance(expiry == "reset" ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(16));
        await harness.Provider.ObserveLocalStateAsync();
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "The snapshot already buffered before expiry may still be consumed.");
        var nextRead = reader.MoveNextAsync().AsTask();
        Assert.IsFalse(nextRead.IsCompleted, "No second pre-expiry snapshot may remain queued.");
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await nextRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(errorCode, reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task RedirectsAreRejectedAndAuthenticationFailureNeverRefreshesCredentials()
    {
        await using var redirect = new Harness();
        redirect.WriteCredentials("synthetic-token");
        redirect.Responder = (request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri("https://attacker.invalid/collect");
            return Task.FromResult(response);
        };
        var redirected = await redirect.PollAndReadAsync();
        Assert.IsTrue(redirected.All(snapshot => snapshot.ErrorCode == "redirect_rejected"));
        Assert.AreEqual(1, redirect.Requests.Count);

        await using var unauthorized = new Harness();
        unauthorized.WriteCredentials("synthetic-token");
        unauthorized.Responder = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            RequestMessage = request
        });
        var denied = await unauthorized.PollAndReadAsync();
        Assert.IsTrue(denied.All(snapshot => snapshot.ErrorCode == "auth_required"));
        Assert.AreEqual(1, unauthorized.Requests.Count);
        Assert.IsTrue(File.ReadAllText(unauthorized.CredentialPath).Contains("synthetic-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TimeoutAndOversizedBodyProduceBoundedSanitizedFailures()
    {
        await using var timeout = new Harness(requestTimeout: TimeSpan.FromMilliseconds(30));
        timeout.WriteCredentials("synthetic-token");
        timeout.Responder = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        };
        var timedOut = await timeout.PollAndReadAsync();
        Assert.IsTrue(timedOut.All(snapshot => snapshot.ErrorCode == "timeout"));

        await using var large = new Harness();
        large.WriteCredentials("synthetic-token");
        large.Responder = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(new byte[KimiQuotaProvider.MaxResponseBytes + 1])
        });
        var oversized = await large.PollAndReadAsync();
        Assert.IsTrue(oversized.All(snapshot => snapshot.ErrorCode == "response_too_large"));
    }

    [TestMethod]
    public async Task TransientFailureKeepsOnlyBoundedStaleCacheThenExpiresIt()
    {
        await using var harness = new Harness();
        harness.WriteCredentials("synthetic-token");
        harness.Responder = (request, _) => Task.FromResult(JsonResponse(request, HttpStatusCode.OK, ValidUsage));
        var fresh = await harness.PollAndReadAsync();
        Assert.AreEqual(MetricState.Fresh, fresh[0].State);

        harness.Responder = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request
        });
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var stale = await harness.PollAndReadAsync();
        Assert.AreEqual(MetricState.Stale, stale[0].State);
        Assert.AreEqual(80d, stale[0].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(fresh[0].ObservedAt, stale[0].ObservedAt);
        Assert.AreEqual("upstream_unavailable", stale[0].ErrorCode);

        harness.Time.Advance(TimeSpan.FromMinutes(14));
        var expired = await harness.PollAndReadAsync();
        Assert.AreEqual(MetricState.Unavailable, expired[0].State);
        Assert.IsNull(expired[0].Value);
        Assert.AreEqual("cache_expired", expired[0].ErrorCode);
    }

    [TestMethod]
    public async Task RetryAfterLongerThanCacheLifetimeIsHonoredWhileCacheExpiresIndependently()
    {
        await using var harness = new Harness();
        harness.WriteCredentials("synthetic-token");
        harness.Responder = (request, _) => Task.FromResult(JsonResponse(request, HttpStatusCode.OK, ValidUsage));
        await harness.PollAndReadAsync();

        harness.Responder = (request, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429) { RequestMessage = request };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return Task.FromResult(response);
        };
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAndReadAsync();

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        var expired = await harness.PollAndReadAsync();
        Assert.AreEqual(2, harness.Requests.Count, "A 20-minute server delay must not be shortened to 15 minutes.");
        Assert.AreEqual(MetricState.Unavailable, expired[0].State);
        Assert.AreEqual("cache_expired", expired[0].ErrorCode);
    }

    [TestMethod]
    public async Task RunCancellationPublishesStoppedAndDisposeCancelsInFlightRequest()
    {
        await using var lifecycle = new Harness();
        var firstRead = lifecycle.StartBatchRead();
        using var stop = new CancellationTokenSource();
        var run = lifecycle.Provider.RunAsync(stop.Token);
        var initial = await firstRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(initial.All(snapshot => snapshot.State == MetricState.Unavailable));

        var stoppedRead = lifecycle.StartBatchRead();
        stop.Cancel();
        try { await run; Assert.Fail("RunAsync should stop when its cancellation token is canceled."); }
        catch (OperationCanceledException) { }
        var stopped = await stoppedRead.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 4 && stopped.Any(snapshot => snapshot.State != MetricState.Stopped); i++)
            stopped = await lifecycle.StartBatchRead().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(stopped.All(snapshot => snapshot.State == MetricState.Stopped));

        await using var inFlight = new Harness();
        inFlight.WriteCredentials("synthetic-token");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inFlight.Responder = async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        };
        var poll = inFlight.Provider.PollOnceAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        inFlight.Provider.Dispose();
        try { await poll.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Fail("Dispose should cancel the active request."); }
        catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task RunPublishesStoppedAndPreservesWorkerFaultWhenClockThrows()
    {
        await using var harness = new Harness(subscriberCapacity: 1, subscribeAll: false);
        harness.WriteCredentials("synthetic-token");
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[0])).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await Task.Yield();
        await harness.Provider.PollOnceAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        var expected = new InvalidOperationException("synthetic clock failure");
        harness.Time.FailNextUtcRead(expected);
        var stopped = ReadStoppedAsync();
        var run = harness.Provider.RunAsync(CancellationToken.None);
        InvalidOperationException? observed = null;
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (InvalidOperationException exception) { observed = exception; }
        Assert.IsNotNull(observed, "RunAsync must surface the worker fault.");
        Assert.AreSame(expected, observed, "Worker faults must remain observable after shutdown publication.");
        var stoppedSnapshot = await stopped.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(stoppedSnapshot.Value);
        Assert.AreEqual(MetricState.Stopped, stoppedSnapshot.State);
        Assert.AreEqual(MetricConfidence.Unknown, stoppedSnapshot.Confidence);

        async Task<MetricSnapshot> ReadStoppedAsync()
        {
            while (await reader.MoveNextAsync())
                if (reader.Current.State == MetricState.Stopped) return reader.Current;
            throw new InvalidOperationException("The metric stream ended before Stopped was published.");
        }
    }

    [TestMethod]
    [DataRow("KIMI_API_KEY", "synthetic-model-key", true)]
    [DataRow("KIMI_CODE_BASE_URL", "https://api.kimi.com/coding/v1/", true)]
    [DataRow("KIMI_CODE_BASE_URL", "https://api.kimi.ai/coding/v1", false)]
    [DataRow("KIMI_CODE_BASE_URL", "", false)]
    [DataRow("KIMI_CODE_OAUTH_HOST", "https://auth.example.invalid", false)]
    [DataRow("KIMI_OAUTH_HOST", "https://auth.example.invalid", false)]
    public void AuthSelectionSeparatesModelApiKeyAndRejectsUnverifiedOAuthSlots(
        string variable, string value, bool defaultSubscription)
    {
        using var harness = new Harness();
        harness.WriteCredentials("synthetic-subscription-token");
        var reader = new KimiQuotaAuthReader(harness.CodeHome, harness.LegacyHome, harness.Time,
            name => name == variable ? value : null);
        var read = reader.Read();
        Assert.AreEqual(defaultSubscription ? KimiAuthStatus.Ready : KimiAuthStatus.Error, read.Status);
        if (!defaultSubscription)
        {
            Assert.IsNull(read.Identity);
            Assert.AreEqual("auth_selection_unverified", read.ErrorCode);
        }
    }

    [TestMethod]
    public async Task ExistingConfigurationCannotSelectCachedOAuthByCredentialFilePresence()
    {
        await using var harness = new Harness();
        harness.WriteCredentials("synthetic-old-token");
        File.WriteAllText(Path.Combine(harness.CodeHome, "config.toml"),
            "[providers.kimi-code]\nbase_url = 'https://api.kimi.ai/coding/v1'\n");
        var snapshots = await harness.PollAndReadAsync();
        Assert.AreEqual(0, harness.Requests.Count);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Value is null &&
            snapshot.ErrorCode == "auth_selection_unverified"));
    }

    [TestMethod]
    public async Task ConfigurationChangeDuringFetchRejectsResponseAndClearsCache()
    {
        await using var harness = new Harness();
        harness.WriteCredentials("synthetic-token");
        Assert.AreEqual(MetricState.Fresh, (await harness.PollAndReadAsync())[0].State);
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        harness.Responder = (request, _) =>
        {
            File.WriteAllText(Path.Combine(harness.CodeHome, "config.toml"),
                "[providers.kimi-code.oauth]\nkey = 'oauth/another-profile'\n");
            return Task.FromResult(JsonResponse(request, HttpStatusCode.OK, ValidUsage));
        };
        var snapshots = await harness.PollAndReadAsync();
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Value is null &&
            snapshot.ErrorCode == "auth_selection_unverified"));
    }

    [TestMethod]
    public async Task OfficialOwnerTokenReplacementRecoversExpiredAccessWithoutProviderCredentialWrites()
    {
        await using var harness = new Harness();
        harness.WriteCredentials("expired-access", expiresAt: InitialUtc.AddSeconds(-1));
        var expired = await harness.PollAndReadAsync();
        Assert.IsTrue(expired.All(snapshot => snapshot.Value is null && snapshot.ErrorCode == "auth_expired"));
        Assert.AreEqual(0, harness.Requests.Count);

        harness.WriteCredentials("owner-renewed-access", expiresAt: InitialUtc.AddHours(1));
        var ownerWrittenBytes = File.ReadAllBytes(harness.CredentialPath);
        var recovered = await harness.PollAndReadAsync();
        Assert.AreEqual(MetricState.Fresh, recovered[0].State);
        Assert.AreEqual("owner-renewed-access", harness.Requests.Single().AuthParameter);
        CollectionAssert.AreEqual(ownerWrittenBytes, File.ReadAllBytes(harness.CredentialPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AuthReaderPreservesFractionalAccessExpiryAndAcceptsZeroUnknownExpiry(bool stringValue)
    {
        using var harness = new Harness();
        harness.WriteCredentials("synthetic-token");
        var expiry = FormattableString.Invariant($"{InitialUtc.ToUnixTimeSeconds() + 0.5m}");
        var jsonValue = stringValue ? $"\"{expiry}\"" : expiry;
        File.WriteAllText(harness.CredentialPath,
            $"{{\"access_token\":\"synthetic-token\",\"expires_at\":{jsonValue}}}");
        var reader = new KimiQuotaAuthReader(harness.CodeHome, harness.LegacyHome, harness.Time, _ => null);
        Assert.AreEqual(KimiAuthStatus.Ready, reader.Read().Status);
        harness.Time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(KimiAuthStatus.Ready, reader.Read().Status);
        harness.Time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(KimiAuthStatus.Expired, reader.Read().Status);

        File.WriteAllText(harness.CredentialPath, "{\"access_token\":\"synthetic-token\",\"expires_at\":0}");
        Assert.AreEqual(KimiAuthStatus.Ready, reader.Read().Status);
    }

    [TestMethod]
    public void AuthReaderUsesOfficialCredentialLocationsAndRejectsExpiredOrMalformedCurrentFile()
    {
        using var harness = new Harness();
        harness.WriteCredentials("legacy-token", legacy: true);
        var reader = new KimiQuotaAuthReader(harness.CodeHome, harness.LegacyHome, harness.Time, _ => null);
        Assert.AreEqual("legacy-token", reader.Read().Identity?.AccessToken);

        harness.WriteCredentials("ignored-fallback", legacy: false, expiresAt: InitialUtc.AddSeconds(-1));
        var expired = reader.Read();
        Assert.AreEqual(KimiAuthStatus.Expired, expired.Status);
        Assert.AreEqual("auth_expired", expired.ErrorCode);

        File.WriteAllText(harness.CredentialPath, "{not-json");
        Assert.AreEqual("auth_file_invalid", reader.Read().ErrorCode);
    }

    private const string ValidUsage = """
        {"usages":{"limit_5h":{"used_ratio":0.2,"reset_time":"2026-09-30T17:00:00Z"},"limit_7d":{"used_ratio":0.5,"reset_time":"2026-10-06T12:00:00Z"},"limit_month_total":{"used_ratio":0.1,"reset_time":"2026-10-31T00:00:00Z"}}}
        """;

    private static HttpResponseMessage JsonResponse(HttpRequestMessage request, HttpStatusCode status, string json) =>
        new(status) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Harness : IAsyncDisposable, IDisposable
    {
        private readonly HttpClient _client;
        private readonly IAsyncEnumerator<MetricSnapshot>[] _readers;
        private readonly DelegateHandler _handler;
        private bool _disposed;

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cerith-kimi-" + Guid.NewGuid().ToString("N"));
        public string CodeHome => Path.Combine(Root, "code");
        public string LegacyHome => Path.Combine(Root, "legacy");
        public string CredentialPath => Path.Combine(CodeHome, "credentials", "kimi-code.json");
        public TestTimeProvider Time { get; } = new(InitialUtc);
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; }
        public KimiQuotaProvider Provider { get; }
        public List<RequestObservation> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; }

        public Harness(TimeSpan? requestTimeout = null, int subscriberCapacity = 32, bool subscribeAll = true)
        {
            Bus = new MetricBus(subscriberCapacity);
            Responder = (request, _) => Task.FromResult(JsonResponse(request, HttpStatusCode.OK, ValidUsage));
            _handler = new DelegateHandler(async (request, cancellationToken) =>
            {
                Requests.Add(new RequestObservation(request.Method, request.RequestUri,
                    request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
                var response = await Responder(request, cancellationToken).ConfigureAwait(false);
                response.RequestMessage ??= request;
                return response;
            });
            _client = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
            Provider = new KimiQuotaProvider(Registry, Bus,
                new KimiQuotaAuthReader(CodeHome, LegacyHome, Time, _ => null), _client, Time, requestTimeout);
            _readers = subscribeAll
                ? Ids.Select(id => Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray()
                : Array.Empty<IAsyncEnumerator<MetricSnapshot>>();
        }

        public void WriteCredentials(string token, bool legacy = false, DateTimeOffset? expiresAt = null)
        {
            var home = legacy ? LegacyHome : CodeHome;
            var path = Path.Combine(home, "credentials", "kimi-code.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var expiry = expiresAt is DateTimeOffset value ? $",\"expires_at\":{value.ToUnixTimeSeconds()}" : string.Empty;
            File.WriteAllText(path, $"{{\"access_token\":\"{token}\",\"refresh_token\":\"synthetic-refresh-token\"{expiry}}}");
        }

        public async Task<MetricSnapshot[]> PollAndReadAsync()
        {
            var reads = StartBatchRead();
            await Task.Yield();
            await Provider.PollOnceAsync().ConfigureAwait(false);
            return await reads.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        public Task<MetricSnapshot[]> StartBatchRead() => ReadBatchAsync();

        private async Task<MetricSnapshot[]> ReadBatchAsync()
        {
            var pending = _readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
            return _readers.Select(reader => reader.Current).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            foreach (var reader in _readers) await reader.DisposeAsync().ConfigureAwait(false);
            Bus.Dispose();
            _client.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Provider.Dispose();
            Bus.Dispose();
            _client.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri? Uri, string? AuthScheme, string? AuthParameter);

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }

    private sealed class TestTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = initialUtc;
        private long _timestamp;
        private Exception? _nextUtcReadFailure;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                if (_nextUtcReadFailure is Exception exception)
                {
                    _nextUtcReadFailure = null;
                    throw exception;
                }
                return _utcNow;
            }
        }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);

        public void FailNextUtcRead(Exception exception)
        {
            lock (_gate) _nextUtcReadFailure = exception;
        }

        public void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _utcNow += duration;
                _timestamp += duration.Ticks;
            }
        }
    }
}
