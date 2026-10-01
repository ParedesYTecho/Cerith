#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.GrokBuild;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.GrokBuild;

[TestClass]
public sealed class GrokBuildQuotaTests
{
    private static readonly string[] MetricIds =
    [
        "ai.grok_build.quota.included.used_percent",
        "ai.grok_build.quota.included.remaining_percent",
        "ai.grok_build.quota.included.period.type",
        "ai.grok_build.quota.included.period.starts_at",
        "ai.grok_build.quota.included.period.resets_at",
        "ai.grok_build.quota.included.period.duration",
        "ai.grok_build.balance.on_demand.used",
        "ai.grok_build.balance.on_demand.cap",
        "ai.grok_build.balance.prepaid",
        "ai.grok_build.quota.included.unified_billing",
        "ai.grok_build.usage.product.grok_build.used_percent"
    ];

    private static readonly DateTimeOffset InitialUtc = new(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);

    private const string CreditsJson = """
        {"config":{"creditUsagePercent":42.5,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-06-01T00:00:00Z","end":"2026-06-08T00:00:00Z"},"onDemandUsed":{"val":300},"onDemandCap":{"val":5000},"prepaidBalance":{"val":1250},"isUnifiedBillingUser":true,"productUsage":[{"product":"PRODUCT_GROK_BUILD","usagePercent":61.2}]}}
        """;

    [TestMethod]
    public void RegistersTypedMetricsAndMapsOfficialCreditPeriodFields()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(CreditsJson);

        harness.Provider.PollOnceAsync().GetAwaiter().GetResult();
        var request = harness.Handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(GrokBuildQuotaProvider.BillingEndpoint, request.Uri);
        Assert.IsTrue(request.HasBearerToken);
        Assert.AreEqual("synthetic-user-a", request.UserId);
        Assert.AreEqual("xai-grok-cli", request.TokenAuth);
        Assert.AreEqual("1.0.34", request.ClientVersion);
        Assert.AreEqual("headless", request.ClientMode);

        var snapshots = ReadPublishedAsync(harness).GetAwaiter().GetResult()
            .ToDictionary(item => item.MetricId.Value, StringComparer.Ordinal);
        Assert.AreEqual(11, harness.Registry.Count);
        Assert.AreEqual(42.5, snapshots[MetricIds[0]].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(57.5, snapshots[MetricIds[1]].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual("USAGE_PERIOD_TYPE_WEEKLY", snapshots[MetricIds[2]].Value!.Value.AsString());
        Assert.AreEqual(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            snapshots[MetricIds[3]].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero),
            snapshots[MetricIds[4]].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(TimeSpan.FromDays(7), snapshots[MetricIds[5]].Value!.Value.AsTimeSpan());
        Assert.AreEqual(300, snapshots[MetricIds[6]].Value!.Value.AsInt64());
        Assert.AreEqual(5000, snapshots[MetricIds[7]].Value!.Value.AsInt64());
        Assert.AreEqual(1250, snapshots[MetricIds[8]].Value!.Value.AsInt64());
        Assert.IsTrue(snapshots[MetricIds[9]].Value!.Value.AsBoolean());
        Assert.AreEqual(61.2, snapshots[MetricIds[10]].Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual("%", harness.Registry.GetSnapshot().Single(item => item.Id.Value == MetricIds[0]).Unit);
        Assert.AreEqual("USD cents", harness.Registry.GetSnapshot().Single(item => item.Id.Value == MetricIds[8]).Unit);
        Assert.AreEqual("%", harness.Registry.GetSnapshot().Single(item => item.Id.Value == MetricIds[10]).Unit);
    }

    [TestMethod]
    public void MissingPercentAndBalanceRemainUnavailableWhilePresentEmptyCentMeansZero()
    {
        const string json = """
            {"config":{"onDemandCap":{}}}
            """;
        var reading = GrokBuildQuotaParser.Parse(Encoding.UTF8.GetBytes(json), InitialUtc);

        Assert.AreEqual(MetricState.Unavailable, reading.Points[GrokBuildQuotaParser.IncludedUsedPercent].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[GrokBuildQuotaParser.IncludedRemainingPercent].State);
        Assert.AreEqual(MetricState.Fresh, reading.Points[GrokBuildQuotaParser.OnDemandCap].State);
        Assert.AreEqual(0, reading.Points[GrokBuildQuotaParser.OnDemandCap].Value!.Value.AsInt64());
        Assert.AreEqual(MetricState.Unavailable, reading.Points[GrokBuildQuotaParser.OnDemandUsed].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[GrokBuildQuotaParser.PrepaidBalance].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[GrokBuildQuotaParser.UnifiedBilling].State);
        Assert.AreEqual(MetricState.Unavailable, reading.Points[GrokBuildQuotaParser.ProductUsagePercent].State);
    }

    [TestMethod]
    public void InvalidFieldsFailIndependentlyAndExpiredPeriodClearsIncludedQuota()
    {
        const string invalid = """
            {"config":{"creditUsagePercent":101,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_MONTHLY","start":"2026-06-01T00:00:00","end":"2026-07-01T00:00:00Z"},"onDemandUsed":{"val":12},"prepaidBalance":{"val":-1}}}
            """;
        var invalidReading = GrokBuildQuotaParser.Parse(Encoding.UTF8.GetBytes(invalid), InitialUtc);
        Assert.AreEqual(MetricState.Error, invalidReading.Points[GrokBuildQuotaParser.IncludedUsedPercent].State);
        Assert.AreEqual("invalid_quota", invalidReading.Points[GrokBuildQuotaParser.IncludedRemainingPercent].ErrorCode);
        Assert.AreEqual(MetricState.Error, invalidReading.Points[GrokBuildQuotaParser.PeriodStart].State);
        Assert.AreEqual(MetricState.Fresh, invalidReading.Points[GrokBuildQuotaParser.OnDemandUsed].State);
        Assert.AreEqual(MetricState.Error, invalidReading.Points[GrokBuildQuotaParser.PrepaidBalance].State);

        const string elapsed = """
            {"config":{"creditUsagePercent":20,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-05-25T00:00:00Z","end":"2026-06-01T00:00:00Z"}}}
            """;
        var elapsedReading = GrokBuildQuotaParser.Parse(Encoding.UTF8.GetBytes(elapsed), InitialUtc);
        Assert.AreEqual(MetricState.Unavailable, elapsedReading.Points[GrokBuildQuotaParser.IncludedUsedPercent].State);
        Assert.AreEqual("reset_elapsed", elapsedReading.Points[GrokBuildQuotaParser.IncludedRemainingPercent].ErrorCode);
        Assert.AreEqual(MetricState.Unavailable, elapsedReading.Points[GrokBuildQuotaParser.ProductUsagePercent].State);
    }

    [TestMethod]
    public void MalformedAndMissingConfigDoNotBecomeZeroValues()
    {
        var malformed = GrokBuildQuotaParser.Parse(Encoding.UTF8.GetBytes("{"), InitialUtc);
        Assert.IsTrue(malformed.Points.All(point => point.State == MetricState.Error));
        var missing = GrokBuildQuotaParser.Parse(Encoding.UTF8.GetBytes("""{"config":null}"""), InitialUtc);
        Assert.IsTrue(missing.Points.All(point => point.State == MetricState.Unavailable && point.Value is null));
    }

    [TestMethod]
    public async Task OwnerChangeCancelsInFlightRequestBeforeItCanReplaceSnapshots()
    {
        using var harness = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.EnqueueRequest(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var pendingPoll = harness.Provider.PollOnceAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        harness.Observation = Observation("synthetic-user-b");
        await harness.Provider.ObserveLocalStateAsync();
        await pendingPoll.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(1, harness.Handler.Requests.Length);
        var snapshots = await ReadPublishedAsync(harness);
        Assert.IsTrue(snapshots.All(item => item.State == MetricState.Unavailable && item.Value is null));
    }

    [TestMethod]
    public async Task RedirectIsRejectedAndLongRetryAfterOutlivesCacheAge()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(CreditsJson);
        await harness.Provider.PollOnceAsync();

        harness.Time.Advance(TimeSpan.FromSeconds(60));
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1));
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(16));
        await harness.Provider.PublishCurrentAsync();
        await harness.Provider.PollOnceAsync();

        Assert.AreEqual(2, harness.Handler.Requests.Length,
            "Retry-After remains authoritative after the in-memory quota cache expires.");

        using var redirectHarness = new Harness();
        redirectHarness.Handler.EnqueueRedirect();
        await redirectHarness.Provider.PollOnceAsync();
        Assert.AreEqual(1, redirectHarness.Handler.Requests.Length);
        var redirectSnapshots = await ReadPublishedAsync(redirectHarness);
        Assert.IsTrue(redirectSnapshots.All(item => item.ErrorCode == "redirect_rejected"));
    }

    [TestMethod]
    public async Task ApiKeyModeNeverSendsARequestOrFallsBackToInteractiveLogin()
    {
        using var harness = new Harness();
        harness.Observation = GrokBuildAuthObservation.Unavailable("auth_unsupported");

        await harness.Provider.PollOnceAsync();

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        var snapshots = await ReadPublishedAsync(harness);
        Assert.IsTrue(snapshots.All(item => item.State == MetricState.Error && item.ErrorCode == "auth_unsupported"));
    }

    private static GrokBuildAuthObservation Observation(string userId)
    {
        var identity = new GrokBuildAuthIdentity("cached_token", userId, "User", userId, null, null);
        var token = CreateJwt(userId);
        return new GrokBuildAuthObservation(identity, token, "1.0.34", null);
    }

    private static string CreateJwt(string subject)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { sub = subject });
        return "synthetic." + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_') +
            ".signature";
    }

    private static async Task<MetricSnapshot[]> ReadPublishedAsync(Harness harness)
    {
        var readers = MetricIds.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        await harness.Provider.PublishCurrentAsync();
        await Task.WhenAll(pending);
        var snapshots = readers.Select(reader => reader.Current).ToArray();
        foreach (var reader in readers)
            await reader.DisposeAsync();
        return snapshots;
    }

    private sealed class Harness : IDisposable
    {
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public ManualTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public GrokBuildQuotaProvider Provider { get; }
        public GrokBuildAuthObservation Observation { get; set; } = Observation("synthetic-user-a");

        public Harness()
        {
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Provider = new GrokBuildQuotaProvider(Registry, Bus, Client, Time,
                _ => Task.FromResult(Observation), static _ => 0);
        }

        public void Dispose()
        {
            Provider.Dispose();
            Client.Dispose();
            Bus.Dispose();
        }
    }

    private sealed record RequestObservation(
        HttpMethod Method,
        Uri Uri,
        bool HasBearerToken,
        string? UserId,
        string? TokenAuth,
        string? ClientVersion,
        string? ClientMode);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>
            _responses = new();
        private readonly ConcurrentQueue<RequestObservation> _requests = new();

        public RequestObservation[] Requests => _requests.ToArray();

        public void EnqueueJson(string json) =>
            Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));

        public void EnqueueStatus(HttpStatusCode status, TimeSpan retryAfter)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            Enqueue(_ => Task.FromResult(response));
        }

        public void EnqueueRedirect()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/redirect");
            Enqueue(_ => Task.FromResult(response));
        }

        public void Enqueue(Func<CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue((_, cancellationToken) => response(cancellationToken));

        public void EnqueueRequest(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var userId = request.Headers.TryGetValues("x-userid", out var userIds) ? userIds.Single() : null;
            var tokenAuth = request.Headers.TryGetValues("X-XAI-Token-Auth", out var authValues)
                ? authValues.Single()
                : null;
            var version = request.Headers.TryGetValues("x-grok-client-version", out var versions)
                ? versions.Single()
                : null;
            var mode = request.Headers.TryGetValues("x-grok-client-mode", out var modes) ? modes.Single() : null;
            _requests.Enqueue(new RequestObservation(request.Method, request.RequestUri!,
                request.Headers.Authorization?.Scheme == "Bearer" &&
                    !string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter),
                userId, tokenAuth, version, mode));

            var response = _responses.TryDequeue(out var queued)
                ? await queued(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }

        public void Advance(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
            }
        }
    }
}
