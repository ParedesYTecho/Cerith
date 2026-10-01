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
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Devin;

[TestClass]
public sealed class DevinQuotaProviderTests
{
    private static readonly DateTimeOffset InitialUtc = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string SyntheticApiKeyA = "synthetic-devin-key-a";
    private const string SyntheticApiKeyB = "synthetic-devin-key-b";
    private const string DailyRemainingId = "ai.devin.quota.daily.remaining_percent";
    private const string DailyResetId = "ai.devin.quota.daily.reset_at";
    private const string WeeklyRemainingId = "ai.devin.quota.weekly.remaining_percent";
    private const string WeeklyResetId = "ai.devin.quota.weekly.reset_at";
    private static readonly string[] MetricIds =
        [DailyRemainingId, DailyResetId, WeeklyRemainingId, WeeklyResetId];

    [TestMethod]
    public async Task FixedRequestPublishesQuotaAndWithholdsUnverifiedResetFields()
    {
        using var harness = new Harness();
        harness.WriteCredential(SyntheticApiKeyA);
        harness.Handler.EnqueueJson(Response(dailyRemaining: 40, weeklyRemaining: "75"));

        await harness.Provider.PollOnceAsync();
        var snapshots = await ReadCurrentAsync(harness);

        Assert.AreEqual(4, harness.Registry.Count);
        Assert.AreEqual(4, snapshots.Length);
        Assert.AreEqual(MetricState.Fresh, snapshots[0].State);
        Assert.AreEqual(40d, snapshots[0].Value!.Value.AsDouble());
        Assert.AreEqual(MetricState.Unavailable, snapshots[1].State);
        Assert.AreEqual("reset_semantics_unverified", snapshots[1].ErrorCode);
        Assert.IsNull(snapshots[1].Value);
        Assert.AreEqual(75d, snapshots[2].Value!.Value.AsDouble());
        Assert.AreEqual(MetricState.Unavailable, snapshots[3].State);
        Assert.AreEqual("reset_semantics_unverified", snapshots[3].ErrorCode);
        Assert.IsNull(snapshots[3].Value);
        Assert.AreEqual(MetricConfidence.Measured, snapshots[0].Confidence);
        Assert.AreEqual(MetricConfidence.Measured, snapshots[2].Confidence);
        Assert.AreEqual(MetricConfidence.Unknown, snapshots[1].Confidence);
        Assert.AreEqual(MetricConfidence.Unknown, snapshots[3].Confidence);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Source == "DevinCLI.GetPlanStatus.ReadOnly.v1"));
        Assert.IsFalse(harness.Registry.TryGet(new MetricId("ai.devin.plan.name"), out _));

        var request = harness.Handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(DevinQuotaProvider.PlanStatusUrl, request.Uri.ToString());
        Assert.AreEqual("{}", request.Body);
        Assert.AreEqual(SyntheticApiKeyA, request.ApiKey);
        Assert.AreEqual("1", request.ConnectProtocolVersion);
        Assert.AreEqual("application/json", request.ContentType);
        Assert.AreEqual("application/json", request.Accept);
    }

    [TestMethod]
    public void MissingPercentWithResetRemainsUnavailableInsteadOfBecomingZero()
    {
        using var document = JsonDocument.Parse(Response(omitDailyRemaining: true));
        var body = Encoding.UTF8.GetBytes(document.RootElement.GetRawText());

        var points = DevinQuotaParser.Parse(body);

        Assert.AreEqual(MetricState.Unavailable, points[DevinQuotaParser.DailyRemaining].State);
        Assert.IsNull(points[DevinQuotaParser.DailyRemaining].Value);
        Assert.AreEqual("remaining_missing", points[DevinQuotaParser.DailyRemaining].ErrorCode);
        Assert.AreEqual(MetricState.Unavailable, points[DevinQuotaParser.DailyReset].State);
        Assert.AreEqual("reset_semantics_unverified", points[DevinQuotaParser.DailyReset].ErrorCode);
        Assert.IsNull(points[DevinQuotaParser.DailyReset].Value);
        Assert.AreEqual(60d, points[DevinQuotaParser.WeeklyRemaining].Value!.Value.AsDouble());
    }

    [TestMethod]
    public void MissingBillingOrHideFlagsDoNotFabricateQuotaMetrics()
    {
        var missingBilling = DevinQuotaParser.Parse(Encoding.UTF8.GetBytes(Response(includeBillingStrategy: false)));
        Assert.IsTrue(missingBilling.All(point => point.State == MetricState.Unavailable));
        Assert.IsTrue(missingBilling.All(point => point.ErrorCode == "billing_strategy_missing"));

        var missingHideFlag = DevinQuotaParser.Parse(Encoding.UTF8.GetBytes(Response(hideDaily: null)));
        Assert.AreEqual(MetricState.Unavailable, missingHideFlag[DevinQuotaParser.DailyRemaining].State);
        Assert.AreEqual("hide_flag_missing", missingHideFlag[DevinQuotaParser.DailyRemaining].ErrorCode);
        Assert.AreEqual(MetricState.Fresh, missingHideFlag[DevinQuotaParser.WeeklyRemaining].State);

        var legacyAcu = DevinQuotaParser.Parse(Encoding.UTF8.GetBytes(
            Response(billingStrategy: "BILLING_STRATEGY_ACU")));
        Assert.IsTrue(legacyAcu.All(point => point.State == MetricState.Unavailable));
        Assert.IsTrue(legacyAcu.All(point => point.ErrorCode == "non_quota_billing"));
    }

    [TestMethod]
    public void MalformedAndOutOfRangePercentagesAreErrorsWithoutClamping()
    {
        var malformed = DevinQuotaParser.Parse(Encoding.UTF8.GetBytes(Response(dailyRemaining: "not-a-number")));
        Assert.AreEqual(MetricState.Error, malformed[DevinQuotaParser.DailyRemaining].State);
        Assert.IsNull(malformed[DevinQuotaParser.DailyRemaining].Value);

        var outOfRange = DevinQuotaParser.Parse(Encoding.UTF8.GetBytes(Response(dailyRemaining: 125)));
        Assert.AreEqual(MetricState.Error, outOfRange[DevinQuotaParser.DailyRemaining].State);
        Assert.AreEqual("remaining_out_of_range", outOfRange[DevinQuotaParser.DailyRemaining].ErrorCode);
        Assert.IsNull(outOfRange[DevinQuotaParser.DailyRemaining].Value);

        var malformedReset = DevinQuotaParser.Parse(Encoding.UTF8.GetBytes(Response(dailyReset: "not-a-time")));
        Assert.AreEqual(MetricState.Unavailable, malformedReset[DevinQuotaParser.DailyReset].State);
        Assert.AreEqual("reset_semantics_unverified", malformedReset[DevinQuotaParser.DailyReset].ErrorCode);
        Assert.IsNull(malformedReset[DevinQuotaParser.DailyReset].Value);
    }

    [TestMethod]
    public async Task MissingCredentialsAndUntrustedEndpointNeverSendARequest()
    {
        using (var unconfigured = new Harness())
        {
            await unconfigured.Provider.PollOnceAsync();
            var snapshots = await ReadCurrentAsync(unconfigured);
            Assert.AreEqual(0, unconfigured.Handler.Requests.Length);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable));
            Assert.IsTrue(snapshots.All(snapshot => snapshot.ErrorCode == "not_configured"));
        }

        using (var overridden = new Harness())
        {
            overridden.WriteCredential(SyntheticApiKeyA, "https://untrusted.example");
            await overridden.Provider.PollOnceAsync();
            var snapshots = await ReadCurrentAsync(overridden);
            Assert.AreEqual(0, overridden.Handler.Requests.Length);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error));
            Assert.IsTrue(snapshots.All(snapshot => snapshot.ErrorCode == "endpoint_override_unsupported"));
        }
    }

    [TestMethod]
    public async Task EnvironmentAuthOverrideFailsClosedRatherThanUsingStoredCredential()
    {
        foreach (var environmentKey in new[] { SyntheticApiKeyB, string.Empty })
        {
            using var harness = new Harness(() => environmentKey);
            harness.WriteCredential(SyntheticApiKeyA);

            await harness.Provider.PollOnceAsync();
            var snapshots = await ReadCurrentAsync(harness);

            Assert.AreEqual(0, harness.Handler.Requests.Length);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error));
            Assert.IsTrue(snapshots.All(snapshot => snapshot.ErrorCode == "environment_auth_unsupported"));
        }
    }

    [TestMethod]
    public async Task SourceSwitchToEnvironmentOverrideClearsStoredQuotaAndDoesNotFallback()
    {
        string? environmentKey = null;
        using var harness = new Harness(() => environmentKey);
        harness.WriteCredential(SyntheticApiKeyA);
        harness.Handler.EnqueueJson(Response(dailyRemaining: 40, weeklyRemaining: 60));

        await harness.Provider.PollOnceAsync();
        var original = await ReadCurrentAsync(harness);
        Assert.AreEqual(MetricState.Fresh, original[0].State);
        Assert.AreEqual(1, harness.Handler.Requests.Length);

        environmentKey = SyntheticApiKeyB;
        await harness.Provider.ObserveLocalStateAsync();
        var overridden = await ReadCurrentAsync(harness);
        await harness.Provider.PollOnceAsync();

        Assert.IsTrue(overridden.All(snapshot => snapshot.State == MetricState.Error));
        Assert.IsTrue(overridden.All(snapshot => snapshot.ErrorCode == "environment_auth_unsupported"));
        Assert.IsTrue(overridden.All(snapshot => snapshot.Value is null));
        Assert.AreEqual(1, harness.Handler.Requests.Length);

        environmentKey = null;
        harness.Handler.EnqueueJson(Response(dailyRemaining: 25, weeklyRemaining: 50));
        await harness.Provider.ObserveLocalStateAsync();
        await harness.Provider.PollOnceAsync();

        Assert.AreEqual(2, harness.Handler.Requests.Length);
        Assert.AreEqual(SyntheticApiKeyA, harness.Handler.Requests[1].ApiKey);
        var restored = await ReadCurrentAsync(harness);
        Assert.AreEqual(MetricState.Fresh, restored[0].State);
        Assert.AreEqual(25d, restored[0].Value!.Value.AsDouble());
    }

    [TestMethod]
    public async Task MalformedCredentialAndManagedEnterprisePolicyFailClosed()
    {
        using (var malformed = new Harness())
        {
            malformed.WriteCredentialText("windsurf_api_key = [invalid]\r\n");
            await malformed.Provider.PollOnceAsync();
            var snapshots = await ReadCurrentAsync(malformed);

            Assert.AreEqual(0, malformed.Handler.Requests.Length);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error));
            Assert.IsTrue(snapshots.All(snapshot => snapshot.ErrorCode == "credential_file_invalid"));
        }

        using (var managed = new Harness())
        {
            managed.WriteCredential(SyntheticApiKeyA);
            managed.WriteSystemPolicy("{\"enterprise_host\":\"enterprise.example\",\"account_id\":\"acct-synthetic\"}");
            await managed.Provider.PollOnceAsync();
            var snapshots = await ReadCurrentAsync(managed);

            Assert.AreEqual(0, managed.Handler.Requests.Length);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error));
            Assert.IsTrue(snapshots.All(snapshot => snapshot.ErrorCode == "managed_auth_policy_unsupported"));
        }
    }

    [TestMethod]
    public void RelativeCredentialRootIsRejected()
    {
        var reader = new DevinQuotaAuthReader("relative-app-data", () => null, Path.GetTempPath());

        var result = reader.Read();

        Assert.AreEqual(DevinAuthStatus.Error, result.Status);
        Assert.AreEqual("auth_path_unavailable", result.ErrorCode);
        Assert.IsNull(result.Credential);
    }

    [TestMethod]
    public async Task StaleValuesKeepTheirOriginalObservationTimeAndExpireAtMonotonicHardLimit()
    {
        using var harness = new Harness();
        harness.WriteCredential(SyntheticApiKeyA);
        harness.Handler.EnqueueJson(Response(dailyRemaining: 40, weeklyRemaining: 75));
        await harness.Provider.PollOnceAsync();
        var fresh = await ReadCurrentAsync(harness);

        harness.Time.Advance(DevinQuotaProvider.StaleAfter);
        await harness.Provider.ObserveLocalStateAsync();
        var stale = await ReadCurrentAsync(harness);
        foreach (var index in new[] { DevinQuotaParser.DailyRemaining, DevinQuotaParser.WeeklyRemaining })
        {
            Assert.AreEqual(MetricState.Stale, stale[index].State);
            Assert.AreEqual(fresh[index].ObservedAt, stale[index].ObservedAt);
        }
        foreach (var index in new[] { DevinQuotaParser.DailyReset, DevinQuotaParser.WeeklyReset })
        {
            Assert.AreEqual(MetricState.Unavailable, stale[index].State);
            Assert.AreEqual("reset_semantics_unverified", stale[index].ErrorCode);
        }

        harness.Time.Advance(DevinQuotaProvider.MaximumCacheAge - DevinQuotaProvider.StaleAfter);
        await harness.Provider.ObserveLocalStateAsync();
        var expired = await ReadCurrentAsync(harness);
        foreach (var index in new[] { DevinQuotaParser.DailyRemaining, DevinQuotaParser.WeeklyRemaining })
        {
            Assert.AreEqual(MetricState.Unavailable, expired[index].State);
            Assert.AreEqual("cache_expired", expired[index].ErrorCode);
        }
        foreach (var index in new[] { DevinQuotaParser.DailyReset, DevinQuotaParser.WeeklyReset })
        {
            Assert.AreEqual(MetricState.Unavailable, expired[index].State);
            Assert.AreEqual("reset_semantics_unverified", expired[index].ErrorCode);
        }
        Assert.IsTrue(expired.All(snapshot => snapshot.Value is null));
    }

    [TestMethod]
    public async Task RunAsyncBackpressureDoesNotBlockAcquisitionAndOwnerChangeCancelsPendingOldPublication()
    {
        using var harness = new Harness();
        harness.WriteCredential(SyntheticApiKeyA);
        var firstResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (request, cancellationToken) =>
        {
            var response = await firstResponse.Task.WaitAsync(cancellationToken);
            response.RequestMessage = request;
            return response;
        });
        harness.Handler.EnqueueJson(Response(dailyRemaining: 42, weeklyRemaining: 70));
        harness.Handler.EnqueueJson(Response(dailyRemaining: 75, weeklyRemaining: 80));

        await using var reader = harness.Bus.Subscribe(new MetricId(DailyRemainingId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        using var stop = new CancellationTokenSource();
        var run = harness.Provider.RunAsync(stop.Token);
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);

        await harness.Bus.PublishAsync(new MetricSnapshot(new MetricId(DailyRemainingId),
            MetricValue.From(99d), InitialUtc, MetricState.Fresh, MetricConfidence.Measured,
            source: "Test.Filler"));
        await WaitForRequestCountAsync(harness.Handler, 1);
        firstResponse.SetResult(JsonResponse(Response(dailyRemaining: 40, weeklyRemaining: 60)));
        await harness.Provider.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(3));

        harness.Time.Advance(DevinQuotaProvider.PollInterval);
        await WaitForRequestCountAsync(harness.Handler, 2);
        await harness.Provider.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(3));

        harness.WriteCredential(SyntheticApiKeyB);
        await harness.Provider.ObserveLocalStateAsync();
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("Test.Filler", reader.Current.Source);

        harness.Time.Advance(DevinQuotaProvider.CredentialCheckInterval);
        await WaitForRequestCountAsync(harness.Handler, 3);
        await harness.Provider.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(3));

        var sawNewOwnerValue = false;
        for (var i = 0; i < 4 && !sawNewOwnerValue; i++)
        {
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            var snapshot = reader.Current;
            if (snapshot.State is MetricState.Fresh or MetricState.Stale)
            {
                Assert.AreEqual(75d, snapshot.Value!.Value.AsDouble());
                sawNewOwnerValue = true;
            }
        }
        Assert.IsTrue(sawNewOwnerValue);
        Assert.AreEqual(3, harness.Handler.Requests.Length);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task RunAsyncWorkerFaultStillPublishesStopped()
    {
        using var harness = new Harness();
        await using var reader = harness.Bus.Subscribe(new MetricId(DailyRemainingId)).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        var run = harness.Provider.RunAsync(CancellationToken.None);

        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(3)));
        await WaitForTimerCountAsync(harness.Time, 2);
        harness.Time.FailNextTimestampRead();
        harness.Time.Advance(DevinQuotaProvider.CredentialCheckInterval);
        var stoppedRead = reader.MoveNextAsync().AsTask();
        Exception? failure = null;
        try { await run.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception exception) { failure = exception; }

        Assert.IsNotNull(failure);
        Assert.IsTrue(await stoppedRead.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
    }

    private static async Task<MetricSnapshot[]> ReadCurrentAsync(Harness harness)
    {
        var readers = MetricIds.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        try
        {
            var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            await harness.Provider.PublishCurrentAsync();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3));
            return readers.Select(reader => reader.Current).ToArray();
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    private static async Task WaitForRequestCountAsync(RecordingHandler handler, int expected)
    {
        for (var i = 0; i < 600 && handler.Requests.Length < expected; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        Assert.IsTrue(handler.Requests.Length >= expected, $"Expected {expected} request(s).");
    }

    private static async Task WaitForTimerCountAsync(ManualTimeProvider time, int expected)
    {
        for (var i = 0; i < 600 && time.TimerCount < expected; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        Assert.IsTrue(time.TimerCount >= expected, $"Expected {expected} initialized timer(s).");
    }

    private static string Response(object? dailyRemaining = null, object? weeklyRemaining = null,
        object? dailyReset = null, object? weeklyReset = null,
        string? billingStrategy = "BILLING_STRATEGY_QUOTA", bool? hideDaily = false,
        bool? hideWeekly = false, bool includeBillingStrategy = true,
        bool omitDailyRemaining = false, bool omitWeeklyRemaining = false)
    {
        var planInfo = new Dictionary<string, object?> { ["planName"] = "Synthetic Plan" };
        if (includeBillingStrategy && billingStrategy is not null) planInfo["billingStrategy"] = billingStrategy;
        if (hideDaily.HasValue) planInfo["hideDailyQuota"] = hideDaily.Value;
        if (hideWeekly.HasValue) planInfo["hideWeeklyQuota"] = hideWeekly.Value;

        var planStatus = new Dictionary<string, object?> { ["planInfo"] = planInfo };
        if (!omitDailyRemaining) planStatus["dailyQuotaRemainingPercent"] = dailyRemaining ?? 40;
        if (!omitWeeklyRemaining) planStatus["weeklyQuotaRemainingPercent"] = weeklyRemaining ?? 60;
        planStatus["dailyQuotaResetAtUnix"] = dailyReset ?? InitialUtc.AddHours(2).ToUnixTimeSeconds();
        planStatus["weeklyQuotaResetAtUnix"] = weeklyReset ?? InitialUtc.AddHours(4).ToUnixTimeSeconds();
        return JsonSerializer.Serialize(new { planStatus });
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Harness : IDisposable
    {
        public string ApplicationData { get; } = Path.Combine(Path.GetTempPath(), "cerith-devin-test-" + Guid.NewGuid().ToString("N"));
        public string CommonApplicationData { get; } = Path.Combine(Path.GetTempPath(), "cerith-devin-policy-test-" + Guid.NewGuid().ToString("N"));
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public ManualTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public DevinQuotaProvider Provider { get; }

        public Harness(Func<string?>? windsurfApiKeyReader = null)
        {
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Provider = new DevinQuotaProvider(Registry, Bus,
                new DevinQuotaAuthReader(ApplicationData, windsurfApiKeyReader, CommonApplicationData), Client, Time);
        }

        public void WriteCredential(string apiKey, string? apiServerUrl = "https://server.codeium.com")
        {
            var text = $"windsurf_api_key = \"{apiKey}\"\r\n";
            if (apiServerUrl is not null) text += $"api_server_url = \"{apiServerUrl}\"\r\n";
            WriteCredentialText(text);
        }

        public void WriteCredentialText(string text)
        {
            var directory = Path.Combine(ApplicationData, "devin");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "credentials.toml"), text, Encoding.UTF8);
        }

        public void WriteSystemPolicy(string json)
        {
            var directory = Path.Combine(CommonApplicationData, "Devin");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "system.json"), json, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            Provider.Dispose();
            Client.Dispose();
            Bus.Dispose();
            if (Directory.Exists(ApplicationData)) Directory.Delete(ApplicationData, recursive: true);
            if (Directory.Exists(CommonApplicationData)) Directory.Delete(CommonApplicationData, recursive: true);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
        private readonly ConcurrentQueue<RequestObservation> _requests = new();

        public RequestObservation[] Requests => _requests.ToArray();

        public void EnqueueJson(string json) => Enqueue((request, _) =>
        {
            var response = JsonResponse(json);
            response.RequestMessage = request;
            return Task.FromResult(response);
        });

        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("x-auth-token", out var tokenValues);
            request.Headers.TryGetValues("Connect-Protocol-Version", out var protocolValues);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Enqueue(new RequestObservation(request.Method, request.RequestUri!, body,
                tokenValues?.SingleOrDefault(), protocolValues?.SingleOrDefault(),
                request.Content?.Headers.ContentType?.MediaType, request.Headers.Accept.FirstOrDefault()?.MediaType));

            if (_responses.TryDequeue(out var response))
                return await response(request, cancellationToken);
            var fallback = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            fallback.RequestMessage = request;
            return fallback;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, string? Body, string? ApiKey,
        string? ConnectProtocolVersion, string? ContentType, string? Accept);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        private bool _throwOnNextTimestampRead;
        public int TimerCount { get { lock (_gate) return _timers.Count; } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp()
        {
            lock (_gate)
            {
                if (_throwOnNextTimestampRead)
                {
                    _throwOnNextTimestampRead = false;
                    throw new InvalidOperationException("synthetic timestamp failure");
                }
                return _timestamp;
            }
        }

        public void FailNextTimestampRead() { lock (_gate) _throwOnNextTimestampRead = true; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] timers;
            long timestamp;
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
                timestamp = _timestamp;
                timers = _timers.ToArray();
            }
            foreach (var timer in timers) timer.FireIfDue(timestamp);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
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

            public void FireIfDue(long timestamp)
            {
                lock (_gate)
                {
                    if (_disposed || _due > timestamp) return;
                    _due = _period == 0 ? long.MaxValue : timestamp + _period;
                }
                callback(state);
            }

            public void Dispose() { lock (_gate) _disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
