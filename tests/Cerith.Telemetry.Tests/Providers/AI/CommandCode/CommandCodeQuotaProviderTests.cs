#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Cerith.TelemetryService.Providers.AI.CommandCode;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.CommandCode;

[TestClass]
public sealed class CommandCodeQuotaProviderTests
{
    private const string UsedMetricId = "ai.commandcode.quota.5h.used_percent";
    private const string ResetMetricId = "ai.commandcode.quota.5h.reset";
    private const string StatusMetricId = "ai.commandcode.subscription.status";
    private const string PlanMetricId = "ai.commandcode.subscription.plan";
    private const string ApiKey = "synthetic-commandcode-key";

    [TestMethod]
    public async Task PollUsesFixedRoutesBearerAuthAndObservedOrgScope()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami") return Json(request, """{"data":{"org":{"id":"org / one"}}}""");
            if (path == "/alpha/billing/credits")
                return Json(request, $"{{\"windowLimits\":{{\"fiveHour\":{{\"used\":25,\"cap\":100,\"resetAt\":{reset}}},\"weekly\":{{\"used\":5,\"cap\":20}}}},\"credits\":{{\"balance\":{{\"amount\":99}}}}}}");
            if (path == "/alpha/billing/subscriptions")
                return Json(request, """{"data":{"planId":"individual-pro-v1","status":"active","accountEmail":"private@example.invalid"}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = Provider(registry, bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var used = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var resetReader = bus.Subscribe(new MetricId(ResetMetricId)).GetAsyncEnumerator();
        var status = bus.Subscribe(new MetricId(StatusMetricId)).GetAsyncEnumerator();
        var plan = bus.Subscribe(new MetricId(PlanMetricId)).GetAsyncEnumerator();

        var usedRead = used.MoveNextAsync().AsTask();
        var resetRead = resetReader.MoveNextAsync().AsTask();
        var statusRead = status.MoveNextAsync().AsTask();
        var planRead = plan.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await usedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await resetRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await statusRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await planRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(25d, used.Current.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(reset), resetReader.Current.Value!.Value.AsDateTimeOffset());
        Assert.AreEqual("active", status.Current.Value!.Value.AsString());
        Assert.AreEqual("Pro", plan.Current.Value!.Value.AsString());
        Assert.AreEqual("CommandCode.ObservedBillingApi.v1", used.Current.Source);

        Assert.AreEqual(3, handler.RequestUris.Count);
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsoluteUri ==
            "https://api.commandcode.ai/alpha/whoami?limits=1"));
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsolutePath == "/alpha/billing/credits" &&
            uri.Query.Contains("orgId=org%20%2F%20one", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsolutePath == "/alpha/billing/subscriptions" &&
            uri.Query.Contains("orgId=org%20%2F%20one", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(handler.AuthorizationHeaders.TrueForAll(value => value == "Bearer " + ApiKey));
        Assert.IsTrue(handler.RequestUris.TrueForAll(uri => uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host == "api.commandcode.ai"));
    }

    [TestMethod]
    public async Task InvalidQuotaAndSubscriptionFieldsPublishValuelessErrors()
    {
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => Json(request, "{}"),
            "/alpha/billing/credits" => Json(request,
                """{"windowLimits":{"fiveHour":{"used":-1,"cap":100},"weekly":{"used":2,"cap":0}}}"""),
            "/alpha/billing/subscriptions" => Json(request,
                """{"data":{"planId":"raw-private-plan","status":"active email@example.invalid"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var used = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var status = bus.Subscribe(new MetricId(StatusMetricId)).GetAsyncEnumerator();

        var usedRead = used.MoveNextAsync().AsTask();
        var statusRead = status.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await usedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await statusRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Error, used.Current.State);
        Assert.IsNull(used.Current.Value);
        Assert.AreEqual("invalid_quota", used.Current.ErrorCode);
        Assert.AreEqual(MetricState.Error, status.Current.State);
        Assert.IsNull(status.Current.Value);
        Assert.AreEqual("invalid_subscription_status", status.Current.ErrorCode);
    }

    [TestMethod]
    public async Task RejectedEnvironmentKeyDoesNotFallBackToCliFile()
    {
        var fileReads = 0;
        using var handler = new RecordingHandler(request =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                RequestMessage = request,
                Content = new StringContent("synthetic unauthorized response")
            });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        var authReader = new CommandCodeQuotaAuthReader(
            () => ApiKey,
            () => "C:\\Users\\test",
            () => null,
            _ => { fileReads++; return "{\"apiKey\":\"file-fallback-key\"}"; });
        using var provider = Provider(new MetricRegistry(), bus, client, authReader, TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Error, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("auth_expired", reader.Current.ErrorCode);
        Assert.AreEqual(1, handler.RequestUris.Count);
        Assert.AreEqual(0, fileReads);

        await provider.PollOnceAsync();
        Assert.AreEqual(1, handler.RequestUris.Count, "A rejected selected credential is not retried or replaced silently.");
    }

    [TestMethod]
    public async Task ResetExpiryCancelsPublicationBlockedByMetricBusBackpressure()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var resetAt = time.GetUtcNow().AddSeconds(5).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => Json(request, "{}"),
            "/alpha/billing/credits" => Json(request,
                $"{{\"windowLimits\":{{\"fiveHour\":{{\"used\":25,\"cap\":100,\"resetAt\":{resetAt}}},\"weekly\":{{\"used\":5,\"cap\":20}}}}}}"),
            "/alpha/billing/subscriptions" => Json(request, """{"data":{"planId":"individual-pro","status":"active"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(1);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var initialRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await provider.PublishCurrentAsync();
        var blockedPublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedPublication.IsCompleted, "A full capacity-one subscription must backpressure PublishAsync.");

        time.Advance(TimeSpan.FromSeconds(5));
        await provider.ExpireValidityOnceAsync();
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "A snapshot already buffered before the reset transition cannot be retracted.");
        var expiredRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await expiredRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task CredentialFingerprintChangeCancelsBlockedPublication()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var resetAt = time.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds();
        var selectedKey = "first-commandcode-key";
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => Json(request, "{}"),
            "/alpha/billing/credits" => Json(request,
                $"{{\"windowLimits\":{{\"fiveHour\":{{\"used\":25,\"cap\":100,\"resetAt\":{resetAt}}},\"weekly\":{{\"used\":5,\"cap\":20}}}}}}"),
            "/alpha/billing/subscriptions" => Json(request, """{"data":{"planId":"individual-pro","status":"active"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(1);
        var authReader = new CommandCodeQuotaAuthReader(() => selectedKey, () => null, () => null, _ => null);
        using var provider = Provider(new MetricRegistry(), bus, client, authReader, time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await provider.PublishCurrentAsync();
        var blockedPublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedPublication.IsCompleted);
        selectedKey = "second-commandcode-key";
        await provider.ObserveAuthOnceAsync();
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "An immutable snapshot already buffered before the credential change cannot be retracted.");
        var changedRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await changedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("account_changed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task RetryAfterLongerThanCacheLifetimeIsNotShortened()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var resetAt = time.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds();
        var pollNumber = 0;
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami")
            {
                Interlocked.Increment(ref pollNumber);
                return Json(request, "{}");
            }
            if (path == "/alpha/billing/credits" && Volatile.Read(ref pollNumber) == 2)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
                return limited;
            }
            if (path == "/alpha/billing/credits")
                return Json(request,
                    $"{{\"windowLimits\":{{\"fiveHour\":{{\"used\":25,\"cap\":100,\"resetAt\":{resetAt}}},\"weekly\":{{\"used\":5,\"cap\":20}}}}}}");
            if (path == "/alpha/billing/subscriptions")
                return Json(request, """{"data":{"planId":"individual-pro","status":"active"}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(1);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var freshRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await freshRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await provider.PollOnceAsync();
        Assert.AreEqual(6, handler.RequestUris.Count);
        time.Advance(TimeSpan.FromMinutes(16));
        await provider.ExpireValidityOnceAsync();
        var requestCount = handler.RequestUris.Count;
        await provider.PollOnceAsync();

        Assert.AreEqual(requestCount, handler.RequestUris.Count,
            "The valid 20-minute Retry-After must remain in force after the 15-minute cache expires.");
    }

    [TestMethod]
    public async Task RunCancellationPublishesStoppedWithoutBlockingShutdown()
    {
        using var handler = new RecordingHandler(request =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request });
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        using var provider = Provider(new MetricRegistry(), bus, client,
            FixedCredentialReader(null), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        using var stop = new CancellationTokenSource();

        var run = provider.RunAsync(stop.Token);
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
    }

    private static CommandCodeQuotaProvider Provider(
        MetricRegistry registry,
        MetricBus bus,
        HttpClient client,
        CommandCodeQuotaAuthReader authReader,
        TimeProvider time) => new(registry, bus, authReader, client, time);

    private static CommandCodeQuotaAuthReader FixedCredentialReader(string? apiKey) =>
        new(() => apiKey, () => null, () => null, _ => null);

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage Json(HttpRequestMessage request, string body) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Uri> RequestUris { get; } = [];
        internal List<string?> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestUris)
            {
                RequestUris.Add(request.RequestUri!);
                AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            }
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialTime;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        internal void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
