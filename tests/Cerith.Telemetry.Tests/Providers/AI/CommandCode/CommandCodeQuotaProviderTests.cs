#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

        Assert.AreEqual(4, handler.RequestUris.Count);
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsoluteUri ==
            "https://api.commandcode.ai/alpha/whoami?limits=1"));
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsolutePath == "/alpha/billing/credits" &&
            uri.Query.Contains("orgId=org%20%2F%20one", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsolutePath == "/alpha/billing/subscriptions" &&
            uri.Query.Contains("orgId=org%20%2F%20one", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(handler.AuthorizationHeaders.TrueForAll(value => value == "Bearer " + ApiKey));
        Assert.IsTrue(handler.UserAgentHeaders.TrueForAll(value => value == "Cerith"),
            "Each request must expose only the Cerith product token.");
        Assert.IsTrue(handler.Methods.TrueForAll(method => method == HttpMethod.Get),
            "Quota acquisition must use GET only.");
        Assert.IsTrue(handler.CookieHeaders.TrueForAll(value => value is null),
            "Requests must not forward cookies.");
        Assert.IsTrue(handler.AcceptHeaders.TrueForAll(value => value == "application/json"));
        Assert.IsTrue(handler.RequestUris.TrueForAll(uri => uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host == "api.commandcode.ai"));
    }

    [TestMethod]
    public void ProductionHandlerDisablesAmbientProxyCookiesAndRedirects()
    {
        using var handler = CommandCodeQuotaProvider.CreateHttpHandler();

        Assert.IsFalse(handler.UseProxy);
        Assert.IsNull(handler.Proxy);
        Assert.IsFalse(handler.UseCookies);
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.IsNull(handler.Credentials);
        Assert.AreEqual(64, handler.MaxResponseHeadersLength,
            "The provider bounds response headers to 64 KiB.");
        Assert.AreEqual(CommandCodeQuotaProvider.RequestTimeout, handler.ConnectTimeout);
    }

    [TestMethod]
    public async Task OversizedChunkedResponseIsRejectedWithoutRetainingItsBody()
    {
        using var handler = new RecordingHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new OversizedStream(CommandCodeQuotaProvider.MaxResponseBytes + 1))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            Assert.IsNull(response.Content.Headers.ContentLength,
                "The test must exercise the streamed bound rather than the Content-Length fast path.");
            return response;
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Error, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("response_too_large", reader.Current.ErrorCode);
        Assert.AreEqual(1, handler.RequestUris.Count);
        Assert.IsFalse(reader.Current.ErrorCode!.Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OfflineWithoutCacheIsUnavailableAndTimeoutIsAnError()
    {
        var offline = true;
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var offlineHandler = new RecordingHandler(request =>
        {
            if (offline) throw new HttpRequestException(ApiKey);
            return request.RequestUri!.AbsolutePath switch
            {
                "/alpha/whoami" => WhoAmIResponse(request, null),
                "/alpha/billing/credits" => QuotaResponse(request, 25, resetAt),
                "/alpha/billing/subscriptions" => SubscriptionResponse(request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
            };
        });
        using var offlineClient = Client(offlineHandler);
        using var offlineBus = new MetricBus(4);
        using var offlineProvider = Provider(new MetricRegistry(), offlineBus, offlineClient,
            FixedCredentialReader(ApiKey), TimeProvider.System);
        var offlineReader = offlineBus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var offlineRead = offlineReader.MoveNextAsync().AsTask();

        await offlineProvider.PollOnceAsync();
        await offlineProvider.PublishCurrentAsync();

        Assert.IsTrue(await offlineRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, offlineReader.Current.State);
        Assert.IsNull(offlineReader.Current.Value);
        Assert.AreEqual("offline", offlineReader.Current.ErrorCode);
        Assert.IsFalse(offlineReader.Current.ErrorCode!.Contains(ApiKey, StringComparison.Ordinal));

        offline = false;
        var freshRead = offlineReader.MoveNextAsync().AsTask();
        await offlineProvider.PollOnceAsync();
        await offlineProvider.PublishCurrentAsync();
        Assert.IsTrue(await freshRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, offlineReader.Current.State);
        var sourceObservedAt = offlineReader.Current.ObservedAt;

        offline = true;
        var staleRead = offlineReader.MoveNextAsync().AsTask();
        await offlineProvider.PollOnceAsync();
        await offlineProvider.PublishCurrentAsync();
        Assert.IsTrue(await staleRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stale, offlineReader.Current.State);
        Assert.AreEqual("offline", offlineReader.Current.ErrorCode);
        Assert.AreEqual(sourceObservedAt, offlineReader.Current.ObservedAt);

        using var timeoutHandler = new RecordingHandler(async (request, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });
        using var timeoutClient = Client(timeoutHandler);
        using var timeoutBus = new MetricBus(4);
        using var timeoutProvider = Provider(new MetricRegistry(), timeoutBus, timeoutClient,
            FixedCredentialReader(ApiKey), TimeProvider.System, TimeSpan.FromMilliseconds(30));
        var timeoutReader = timeoutBus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var timeoutRead = timeoutReader.MoveNextAsync().AsTask();

        await timeoutProvider.PollOnceAsync();
        await timeoutProvider.PublishCurrentAsync();

        Assert.IsTrue(await timeoutRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Error, timeoutReader.Current.State);
        Assert.IsNull(timeoutReader.Current.Value);
        Assert.AreEqual("request_timeout", timeoutReader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task BodyReadIOExceptionMapsOfflineAndNextAcquisitionRecovers()
    {
        var failQuotaBody = true;
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami") return WhoAmIResponse(request, null);
            if (path == "/alpha/billing/credits" && failQuotaBody)
            {
                failQuotaBody = false;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new FailingReadStream(new IOException("synthetic stream reset")))
                };
            }
            if (path == "/alpha/billing/credits") return QuotaResponse(request, 25, resetAt);
            if (path == "/alpha/billing/subscriptions") return SubscriptionResponse(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var offlineRead = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await offlineRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("offline", reader.Current.ErrorCode);

        var recoveredRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await recoveredRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());
    }

    [TestMethod]
    public async Task BodyReadHttpRequestExceptionKeepsCachedValueAndRunRecovers()
    {
        var failQuotaBody = false;
        var bodyFailureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami") return WhoAmIResponse(request, "org-a");
            if (path == "/alpha/billing/credits" && failQuotaBody)
            {
                failQuotaBody = false;
                bodyFailureEntered.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new FailingReadStream(
                        new HttpRequestException("synthetic body disconnect")))
                };
            }
            if (path == "/alpha/billing/credits") return QuotaResponse(request, 25, resetAt);
            if (path == "/alpha/billing/subscriptions") return SubscriptionResponse(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        await using var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        var initialRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());

        failQuotaBody = true;
        using var stop = new CancellationTokenSource();
        var run = provider.RunAsync(stop.Token);
        await bodyFailureEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stale, reader.Current.State);
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());
        Assert.AreEqual("offline", reader.Current.ErrorCode);
        Assert.IsFalse(run.IsCompleted, "A body transport interruption must not stop the provider loop.");

        var recoveredRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await recoveredRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task InvalidEnvironmentKeyKeepsRunAliveAndRecoversWithoutLeakingCredential()
    {
        const string invalidApiKey = "synthetic-commandcode-key\r\nInjected: value";
        var credentialCorrected = false;
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => WhoAmIResponse(request, "org-a"),
            "/alpha/billing/credits" => QuotaResponse(request, 25, resetAt),
            "/alpha/billing/subscriptions" => SubscriptionResponse(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        var authReader = new CommandCodeQuotaAuthReader(
            () => credentialCorrected ? ApiKey : invalidApiKey,
            () => null,
            () => null,
            _ => null);
        using var provider = Provider(new MetricRegistry(), bus, client, authReader, TimeProvider.System);
        await using var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        using var stop = new CancellationTokenSource();

        var invalidRead = reader.MoveNextAsync().AsTask();
        var run = provider.RunAsync(stop.Token);
        Assert.IsTrue(await invalidRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Error, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("auth_invalid", reader.Current.ErrorCode);
        Assert.IsFalse(reader.Current.ErrorCode!.Contains("synthetic-commandcode-key", StringComparison.Ordinal));
        Assert.AreEqual(0, handler.RequestUris.Count, "Invalid credentials must be rejected before any HTTP request.");
        Assert.IsFalse(run.IsCompleted, "Invalid credentials must not stop the provider lifecycle.");

        credentialCorrected = true;
        await provider.PollOnceAsync();
        var recovered = false;
        for (var attempt = 0; attempt < 5 && !recovered; attempt++)
        {
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            recovered = reader.Current.State == MetricState.Fresh;
        }

        Assert.IsTrue(recovered, "A corrected credential must restore quota publication without restarting RunAsync.");
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());
        Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual(4, handler.RequestUris.Count);
        Assert.IsTrue(handler.AuthorizationHeaders.TrueForAll(value => value == "Bearer " + ApiKey));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(4));
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
                Content = new StringContent(ApiKey)
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
        Assert.IsFalse(reader.Current.ErrorCode!.Contains(ApiKey, StringComparison.Ordinal));
        Assert.AreEqual(1, handler.RequestUris.Count);
        Assert.IsTrue(handler.Methods.TrueForAll(method => method == HttpMethod.Get));
        Assert.AreEqual(0, fileReads);

        await provider.PollOnceAsync();
        Assert.AreEqual(1, handler.RequestUris.Count, "A rejected selected credential is not retried or replaced silently.");
    }

    [TestMethod]
    public async Task ForbiddenAuthResponseIsValuelessAndSuppressesTheRejectedCredential()
    {
        using var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            RequestMessage = request,
            Content = new StringContent(ApiKey)
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Error, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("auth_expired", reader.Current.ErrorCode);

        await provider.PollOnceAsync();
        Assert.AreEqual(1, handler.RequestUris.Count,
            "Forbidden credentials remain suspended until the selected credential changes.");
        Assert.IsTrue(handler.Methods.TrueForAll(method => method == HttpMethod.Get));
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
    public async Task ResetDeadlineExpiresByMonotonicLifetimeAfterWallClockMovesBackward()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var resetAt = start.AddSeconds(5).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => WhoAmIResponse(request, null),
            "/alpha/billing/credits" => QuotaResponse(request, 25, resetAt),
            "/alpha/billing/subscriptions" => SubscriptionResponse(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var freshRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await freshRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        time.SetUtcNow(start.AddMinutes(-1));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        var expiredRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await expiredRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task PublishCurrentExpiresResetBeforeObserverRuns()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var resetAt = start.AddSeconds(5).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => WhoAmIResponse(request, null),
            "/alpha/billing/credits" => QuotaResponse(request, 25, resetAt),
            "/alpha/billing/subscriptions" => SubscriptionResponse(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var freshRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await freshRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        time.Advance(TimeSpan.FromSeconds(5));
        var expiredRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await expiredRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task RunValidityObserverExpiresResetDuringBackpressuredPublicationAndPendingAcquisition()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var resetAt = start.AddSeconds(10).ToUnixTimeMilliseconds();
        var secondWhoAmIEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponseReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? pendingRequest = null;
        var whoAmICount = 0;
        using var handler = new RecordingHandler(async (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami" && Interlocked.Increment(ref whoAmICount) == 3)
            {
                pendingRequest = request;
                secondWhoAmIEntered.TrySetResult(true);
                var response = await pendingResponse.Task.ConfigureAwait(false);
                pendingResponseReleased.TrySetResult(true);
                return response;
            }

            if (path == "/alpha/whoami") return WhoAmIResponse(request, null);
            if (path == "/alpha/billing/credits") return QuotaResponse(request, 25, resetAt);
            if (path == "/alpha/billing/subscriptions") return SubscriptionResponse(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(1);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        var initialRead = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        await bus.PublishAsync(reader.Current);

        using var stop = new CancellationTokenSource();
        var run = provider.RunAsync(stop.Token);
        await secondWhoAmIEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(pendingResponseReleased.Task.IsCompleted,
            "The RunAsync acquisition remains pending while validity is checked.");

        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        Assert.IsFalse(pendingResponseReleased.Task.IsCompleted,
            "The independent validity timer must run while acquisition is still pending.");

        var bufferedRead = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(bufferedRead);
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "A previously buffered snapshot remains immutable.");
        var expiredRead = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.IsTrue(expiredRead);
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
        Assert.IsFalse(pendingResponseReleased.Task.IsCompleted,
            "The validity observer must expire the reset without waiting for acquisition.");

        var stoppedRead = ReadUntilStoppedAsync(reader);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await stoppedRead.WaitAsync(TimeSpan.FromSeconds(2)));

        pendingResponse.SetResult(WhoAmIResponse(pendingRequest!, null));
        await pendingResponseReleased.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task PublishCurrentUsesMonotonicAgeForStaleAndHardExpiryWithoutChangingObservedAt()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var resetAt = start.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => WhoAmIResponse(request, null),
            "/alpha/billing/credits" => QuotaResponse(request, 25, resetAt),
            "/alpha/billing/subscriptions" => SubscriptionResponse(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var freshRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await freshRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(start, reader.Current.ObservedAt);

        time.SetUtcNow(start.AddDays(-10));
        time.AdvanceMonotonic(CommandCodeQuotaProvider.StaleAfter);
        var staleRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await staleRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stale, reader.Current.State);
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());
        Assert.AreEqual(start, reader.Current.ObservedAt,
            "Age uses monotonic elapsed time and Stale retains the source observation time.");

        time.AdvanceMonotonic(CommandCodeQuotaProvider.MaximumCacheAge - CommandCodeQuotaProvider.StaleAfter);
        var expiredRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await expiredRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("cache_expired", reader.Current.ErrorCode);
        Assert.AreEqual(4, handler.RequestUris.Count,
            "Freshness aging and expiry must not require another acquisition.");
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
    public async Task SelectedAuthFileChangeWithSameKeyCancelsBlockedPublicationAndClearsCache()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var resetAt = time.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds();
        var selectedHome = @"C:\synthetic\command-code-home-a";
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
        var authReader = new CommandCodeQuotaAuthReader(
            () => null,
            () => selectedHome,
            () => null,
            _ => """{"apiKey":"same-synthetic-commandcode-key"}""");
        using var provider = Provider(new MetricRegistry(), bus, client, authReader, time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await provider.PublishCurrentAsync();
        var blockedPublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedPublication.IsCompleted,
            "A full capacity-one subscription must backpressure PublishAsync.");

        selectedHome = @"C:\synthetic\command-code-home-b";
        await provider.ObserveAuthOnceAsync();
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "An immutable snapshot already buffered before the selected owner change cannot be retracted.");
        var changedRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await changedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("account_changed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task AuthSourceAndSelectedFilePathChangesClearSameKeyCache()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var resetAt = time.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds();
        var useFile = false;
        var selectedHome = @"C:\synthetic\command-code-home-a";
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => Json(request, "{}"),
            "/alpha/billing/credits" => QuotaResponse(request, 25, resetAt),
            "/alpha/billing/subscriptions" => SubscriptionResponse(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        var authReader = new CommandCodeQuotaAuthReader(
            () => useFile ? null : ApiKey,
            () => useFile ? selectedHome : null,
            () => null,
            _ => "{\"apiKey\":\"" + ApiKey + "\"}");
        using var provider = Provider(new MetricRegistry(), bus, client, authReader, time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var initialRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        useFile = true;
        await provider.ObserveAuthOnceAsync();
        var sourceChangedRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await sourceChangedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("account_changed", reader.Current.ErrorCode);
        Assert.IsNull(reader.Current.Value);

        selectedHome = @"C:\synthetic\command-code-home-b";
        await provider.ObserveAuthOnceAsync();
        var pathChangedRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await pathChangedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("account_changed", reader.Current.ErrorCode);
        Assert.IsNull(reader.Current.Value);
    }

    [TestMethod]
    public async Task OrgChangeClearsOldCacheBeforeNewOrgResponseCompletes()
    {
        var selectedOrg = "org-a";
        var creditsRequestCount = 0;
        HttpRequestMessage? pendingCreditsRequest = null;
        var secondCreditsEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCreditsResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami") return Task.FromResult(WhoAmIResponse(request, selectedOrg));
            if (path == "/alpha/billing/credits")
            {
                if (Interlocked.Increment(ref creditsRequestCount) == 2)
                {
                    pendingCreditsRequest = request;
                    secondCreditsEntered.TrySetResult(true);
                    return secondCreditsResponse.Task;
                }
                return Task.FromResult(QuotaResponse(request, 25, resetAt));
            }
            if (path == "/alpha/billing/subscriptions") return Task.FromResult(SubscriptionResponse(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        });
        using var client = Client(handler);
        using var bus = new MetricBus(2);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var initialRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());

        selectedOrg = "org-b";
        var secondPoll = provider.PollOnceAsync();
        await secondCreditsEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var changedRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await changedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("account_changed", reader.Current.ErrorCode);
        Assert.IsNull(reader.Current.Value);

        secondCreditsResponse.SetResult(QuotaResponse(pendingCreditsRequest!, 75, resetAt));
        await secondPoll.WaitAsync(TimeSpan.FromSeconds(2));
        var newOrgRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await newOrgRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(75d, reader.Current.Value!.Value.AsDouble());
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsolutePath == "/alpha/billing/credits" &&
            uri.Query.Contains("orgId=org-b", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OrgChangeDuringBillingDiscardsOldScopeAndKeepsNewScopeUnavailable()
    {
        var selectedOrg = "org-a";
        var billingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCredits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami") return WhoAmIResponse(request, selectedOrg);
            if (path == "/alpha/billing/credits")
            {
                billingEntered.TrySetResult();
                await releaseCredits.Task.WaitAsync(cancellationToken);
                return QuotaResponse(request, 75, resetAt);
            }
            if (path == "/alpha/billing/subscriptions") return SubscriptionResponse(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(2);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var changedRead = reader.MoveNextAsync().AsTask();

        var poll = provider.PollOnceAsync();
        await billingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        selectedOrg = "org-b";
        releaseCredits.TrySetResult();
        await poll.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await changedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("account_changed", reader.Current.ErrorCode);
        Assert.AreEqual(4, handler.RequestUris.Count);
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsolutePath == "/alpha/billing/credits" &&
            uri.Query.Contains("orgId=org-a", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OrgChangeDuringBillingCancelsBlockedOldScopePublication()
    {
        var selectedOrg = "org-a";
        var creditsRequestCount = 0;
        var secondCreditsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondCredits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami") return WhoAmIResponse(request, selectedOrg);
            if (path == "/alpha/billing/credits")
            {
                if (Interlocked.Increment(ref creditsRequestCount) == 2)
                {
                    secondCreditsEntered.TrySetResult();
                    await releaseSecondCredits.Task.WaitAsync(cancellationToken);
                }
                return QuotaResponse(request, 25, resetAt);
            }
            if (path == "/alpha/billing/subscriptions") return SubscriptionResponse(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(1);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        await using var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();

        await provider.PollOnceAsync();
        var initialRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble());

        await provider.PublishCurrentAsync();
        var blockedOldPublication = provider.PublishCurrentAsync();
        await Task.Delay(25);
        Assert.IsFalse(blockedOldPublication.IsCompleted,
            "The accepted A sample fills the only slot and blocks the next A publication.");

        var secondPoll = provider.PollOnceAsync();
        await secondCreditsEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        selectedOrg = "org-b";
        releaseSecondCredits.TrySetResult();
        await secondPoll.WaitAsync(TimeSpan.FromSeconds(2));
        await blockedOldPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble(),
            "This A sample was queued before the authenticated organization change was observed.");

        var changedRead = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await changedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("account_changed", reader.Current.ErrorCode);
        Assert.AreEqual(8, handler.RequestUris.Count);
        Assert.IsTrue(handler.RequestUris.Exists(uri => uri.AbsolutePath == "/alpha/billing/credits" &&
            uri.Query.Contains("orgId=org-a", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BillingResponseOrganizationScopeMustMatchAuthenticatedWhoAmI()
    {
        var resetAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/alpha/whoami" => WhoAmIResponse(request, "org-a"),
            "/alpha/billing/credits" => Json(request,
                $"{{\"org\":{{\"id\":\"org-b\"}},\"windowLimits\":{{\"fiveHour\":{{\"used\":25,\"cap\":100,\"resetAt\":{resetAt}}}}}}}"),
            "/alpha/billing/subscriptions" => SubscriptionResponse(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();

        Assert.IsTrue(await read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Error, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("schema_invalid", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task CredentialChangeCancelsBlockedPublicationAndDiscardsLateWhoAmIResponse()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var resetAt = time.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds();
        var selectedKey = "first-commandcode-key";
        var whoAmICount = 0;
        HttpRequestMessage? pendingWhoAmIRequest = null;
        var secondWhoAmIEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWhoAmIResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami")
            {
                if (Interlocked.Increment(ref whoAmICount) == 3)
                {
                    pendingWhoAmIRequest = request;
                    secondWhoAmIEntered.TrySetResult(true);
                    return secondWhoAmIResponse.Task;
                }
                return Task.FromResult(WhoAmIResponse(request, "org-a"));
            }
            if (path == "/alpha/billing/credits") return Task.FromResult(QuotaResponse(request, 25, resetAt));
            if (path == "/alpha/billing/subscriptions") return Task.FromResult(SubscriptionResponse(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
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
        Assert.IsFalse(blockedPublication.IsCompleted,
            "The capacity-one subscription must keep the second publication blocked.");

        var latePoll = provider.PollOnceAsync();
        await secondWhoAmIEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        selectedKey = "second-commandcode-key";
        await provider.ObserveAuthOnceAsync();
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));
        secondWhoAmIResponse.SetResult(WhoAmIResponse(pendingWhoAmIRequest!, "org-a"));
        await latePoll.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(5, handler.RequestUris.Count,
            "The canceled old-owner whoami response must not start billing requests.");
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State,
            "A snapshot buffered before the observed owner transition cannot be retracted.");
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
            if (path == "/alpha/whoami") return Json(request, "{}");
            if (path == "/alpha/billing/credits" && Interlocked.Increment(ref pollNumber) == 2)
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
        Assert.AreEqual(7, handler.RequestUris.Count);
        time.Advance(TimeSpan.FromMinutes(16));
        await provider.ExpireValidityOnceAsync();
        var requestCount = handler.RequestUris.Count;
        await provider.PollOnceAsync();

        Assert.AreEqual(requestCount, handler.RequestUris.Count,
            "The valid 20-minute Retry-After must remain in force after the 15-minute cache expires.");
    }

    [TestMethod]
    public async Task RetryAfterHttpDateUsesMonotonicDeadlineAfterWallClockJump()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var retryAt = start.AddMinutes(20);
        var resetAt = start.AddDays(30).ToUnixTimeMilliseconds();
        var pollNumber = 0;
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/alpha/whoami") return WhoAmIResponse(request, null);
            if (path == "/alpha/billing/credits" && Interlocked.Increment(ref pollNumber) == 2)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
                return limited;
            }
            if (path == "/alpha/billing/credits") return QuotaResponse(request, 25, resetAt);
            if (path == "/alpha/billing/subscriptions") return SubscriptionResponse(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var client = Client(handler);
        using var bus = new MetricBus(4);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);

        await provider.PollOnceAsync();
        await provider.PollOnceAsync();
        Assert.AreEqual(7, handler.RequestUris.Count);

        time.SetUtcNow(start.AddDays(2));
        time.AdvanceMonotonic(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        await provider.PollOnceAsync();
        Assert.AreEqual(7, handler.RequestUris.Count,
            "A later wall-clock correction must not shorten the parsed Retry-After deadline.");

        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        await provider.PollOnceAsync();
        Assert.AreEqual(11, handler.RequestUris.Count);
    }

    [TestMethod]
    public async Task RunShutdownIsBoundedWhenRequestHandlerIgnoresCancellation()
    {
        var requestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? pendingRequest = null;
        using var handler = new RecordingHandler(async (request, _) =>
        {
            pendingRequest = request;
            requestEntered.TrySetResult(true);
            var response = await pendingResponse.Task.ConfigureAwait(false);
            responseReleased.TrySetResult(true);
            return response;
        });
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        using var stop = new CancellationTokenSource();
        var run = provider.RunAsync(stop.Token);
        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var timer = Stopwatch.StartNew();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5),
            "A noncooperative request must not hold provider shutdown open indefinitely.");

        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        var sawStopped = reader.Current.State == MetricState.Stopped;
        for (var attempt = 0; attempt < 4 && !sawStopped; attempt++)
        {
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            sawStopped = reader.Current.State == MetricState.Stopped;
        }
        Assert.IsTrue(sawStopped, "Shutdown must attempt the Stopped publication before returning.");

        provider.Dispose();
        pendingResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = pendingRequest,
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        await responseReleased.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RunRethrowsUnexpectedWorkerFaultAfterStoppedPublicationAttempt()
    {
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("synthetic unexpected transport fault"));
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();

        InvalidOperationException? observedFault = null;
        try
        {
            await provider.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException exception)
        {
            observedFault = exception;
        }

        Assert.IsNotNull(observedFault);
        Assert.AreEqual("synthetic unexpected transport fault", observedFault.Message);
        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        var sawStopped = reader.Current.State == MetricState.Stopped;
        for (var attempt = 0; attempt < 4 && !sawStopped; attempt++)
        {
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            sawStopped = reader.Current.State == MetricState.Stopped;
        }
        Assert.IsTrue(sawStopped,
            "RunAsync must make its bounded Stopped publication attempt before rethrowing an unexpected worker fault.");
    }

    [TestMethod]
    public async Task RunCancellationRethrowsWorkerFaultCapturedDuringDrain()
    {
        var requestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorkerFault = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler(async (_, _) =>
        {
            requestEntered.TrySetResult(true);
            await releaseWorkerFault.Task.ConfigureAwait(false);
            throw new InvalidOperationException("synthetic worker fault during drain");
        });
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), TimeProvider.System);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        using var stop = new CancellationTokenSource();
        var run = provider.RunAsync(stop.Token);

        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        releaseWorkerFault.TrySetResult(true);

        InvalidOperationException? observedFault = null;
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException exception) { observedFault = exception; }

        Assert.IsNotNull(observedFault);
        Assert.AreEqual("synthetic worker fault during drain", observedFault.Message);
        Assert.IsTrue(await ReadUntilStoppedAsync(reader).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task CancellationCallbackFailureDoesNotSkipStoppedPublication()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var requestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration callbackRegistration = default;
        HttpRequestMessage? pendingRequest = null;
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            pendingRequest = request;
            callbackRegistration = cancellationToken.Register(() =>
            {
                callbackInvoked.TrySetResult(true);
                throw new InvalidOperationException("synthetic cancellation callback fault");
            });
            requestEntered.TrySetResult(true);
            var response = await pendingResponse.Task.ConfigureAwait(false);
            responseReleased.TrySetResult(true);
            return response;
        });
        using var client = Client(handler);
        using var bus = new MetricBus(8);
        using var provider = Provider(new MetricRegistry(), bus, client, FixedCredentialReader(ApiKey), time);
        var reader = bus.Subscribe(new MetricId(UsedMetricId)).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        var run = provider.RunAsync(CancellationToken.None);

        Assert.IsTrue(await initialRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stoppedRead = ReadUntilStoppedAsync(reader);
        time.ThrowOnNextUtcRead();

        Exception? observedFault = null;
        try { await run.WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (Exception exception) { observedFault = exception; }

        Assert.IsNotNull(observedFault);
        Assert.IsTrue(callbackInvoked.Task.IsCompleted,
            "RunAsync shutdown must exercise the throwing callback on its active request token.");
        Assert.IsTrue(ContainsExceptionMessage(observedFault, "synthetic validity worker fault"));
        Assert.IsTrue(ContainsExceptionMessage(observedFault, "synthetic cancellation callback fault"));
        Assert.IsTrue(await stoppedRead.WaitAsync(TimeSpan.FromSeconds(2)));

        pendingResponse.SetResult(WhoAmIResponse(pendingRequest!, null));
        await responseReleased.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callbackRegistration.Dispose();
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
        TimeProvider time,
        TimeSpan? requestTimeout = null) => new(registry, bus, authReader, client, time, requestTimeout);

    private static async Task<bool> ReadUntilStoppedAsync(IAsyncEnumerator<MetricSnapshot> reader)
    {
        while (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            if (reader.Current.State == MetricState.Stopped) return true;
        return false;
    }

    private static bool ContainsExceptionMessage(Exception exception, string message)
    {
        if (string.Equals(exception.Message, message, StringComparison.Ordinal)) return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                if (ContainsExceptionMessage(inner, message)) return true;
        }
        return exception.InnerException is { } nested && ContainsExceptionMessage(nested, message);
    }

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

    private static HttpResponseMessage WhoAmIResponse(HttpRequestMessage request, string? orgId) =>
        Json(request, string.IsNullOrEmpty(orgId)
            ? "{}"
            : $"{{\"data\":{{\"org\":{{\"id\":\"{orgId}\"}}}}}}");

    private static HttpResponseMessage QuotaResponse(HttpRequestMessage request, int fiveHourUsed, long resetAt)
    {
        var body = string.Concat(
            "{\"windowLimits\":{\"fiveHour\":{\"used\":",
            fiveHourUsed.ToString(CultureInfo.InvariantCulture),
            ",\"cap\":100,\"resetAt\":",
            resetAt.ToString(CultureInfo.InvariantCulture),
            "},\"weekly\":{\"used\":5,\"cap\":20,\"resetAt\":",
            resetAt.ToString(CultureInfo.InvariantCulture),
            "}}}");
        return Json(request, body);
    }

    private static HttpResponseMessage SubscriptionResponse(HttpRequestMessage request) =>
        Json(request, "{\"data\":{\"planId\":\"individual-pro\",\"status\":\"active\"}}");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        internal RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        internal RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        internal List<Uri> RequestUris { get; } = [];
        internal List<HttpMethod> Methods { get; } = [];
        internal List<string?> AuthorizationHeaders { get; } = [];
        internal List<string> UserAgentHeaders { get; } = [];
        internal List<string?> CookieHeaders { get; } = [];
        internal List<string> AcceptHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestUris)
            {
                RequestUris.Add(request.RequestUri!);
                Methods.Add(request.Method);
                AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
                UserAgentHeaders.Add(request.Headers.UserAgent.ToString());
                CookieHeaders.Add(request.Headers.Contains("Cookie") ? string.Join(";", request.Headers.GetValues("Cookie")) : null);
                AcceptHeaders.Add(request.Headers.Accept.ToString());
            }
            return await _respond(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = initialTime;
        private long _timestamp;
        private int _throwNextUtcRead;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _throwNextUtcRead, 0) != 0)
                throw new InvalidOperationException("synthetic validity worker fault");
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        internal void Advance(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
            }
        }

        internal void AdvanceMonotonic(TimeSpan elapsed)
        {
            lock (_gate) _timestamp += elapsed.Ticks;
        }

        internal void SetUtcNow(DateTimeOffset utcNow)
        {
            lock (_gate) _utcNow = utcNow;
        }

        internal void ThrowOnNextUtcRead() => Interlocked.Exchange(ref _throwNextUtcRead, 1);
    }

    private sealed class OversizedStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0 || buffer.Length == 0) return 0;
            var read = (int)Math.Min(buffer.Length, _remaining);
            buffer[..read].Fill((byte)'x');
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer.AsSpan(offset, count)));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingReadStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override int Read(Span<byte> buffer) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(failure);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
