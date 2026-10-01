#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Antigravity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Antigravity;

[TestClass]
public sealed class AntigravityQuotaProviderTests
{
    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string MetricId = "ai.antigravity.quota.3p-weekly.remaining";
    private const string StatusA = "{\"userStatus\":{\"email\":\"synthetic-A@example.invalid\"}}";
    private const string StatusB = "{\"userStatus\":{\"email\":\"synthetic-B@example.invalid\"}}";
    private const string SummaryPayload = """
        {"response":{"groups":[{"buckets":[
          {"bucketId":"3p-weekly","remainingFraction":0.123456,"resetTime":"2030-01-01T00:00:00Z"}
        ]}]}}
        """;

    [TestMethod]
    [DataRow("account")]
    [DataRow("process")]
    [DataRow("session-generation")]
    public async Task PendingPublicationIsCanceledWhenOwnerIdentityChanges(string change)
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        var oldOwner = Owner();
        var newOwner = change switch
        {
            "account" => Owner(email: "synthetic-account-B"),
            "process" => Owner(processId: 77, start: "synthetic-start-B"),
            _ => Owner(sessionGeneration: "synthetic-session-generation-B")
        };
        var acquisitions = new Queue<AntigravityQuotaAcquisition>([
            Acquisition(oldOwner, time.GetUtcNow(), 0.25, time.GetUtcNow().AddDays(2)),
            Acquisition(newOwner, time.GetUtcNow(), 0.75, time.GetUtcNow().AddDays(2))
        ]);
        using var provider = Provider(bus, time, _ => Task.FromResult(acquisitions.Dequeue()));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blockedOldPublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedOldPublication.IsCompleted, "The capacity-one subscriber should backpressure the second publication.");

        time.Advance(AntigravityQuotaProvider.PollInterval);
        await provider.PollOnceAsync();
        await blockedOldPublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(25d, reader.Current.Value!.Value.AsDouble(), 0.000001,
            "A snapshot already buffered before the transition cannot be retracted.");
        var next = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(75d, reader.Current.Value!.Value.AsDouble(), 0.000001,
            "No blocked publication from the previous ownership may be released after the transition.");
    }

    [TestMethod]
    public async Task ResetCrossingCancelsBlockedValuePublicationAndInvalidatesItsBucket()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        using var provider = Provider(bus, time, _ => Task.FromResult(
            Acquisition(Owner(), time.GetUtcNow(), 0.4, time.GetUtcNow().AddSeconds(10))));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blockedValuePublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedValuePublication.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(10));
        await provider.ExpireValidityOnceAsync();
        await blockedValuePublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Stale, reader.Current.State);
        var next = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task MonotonicCacheExpirySurvivesBackwardWallClockAdjustmentAndCancelsPendingValue()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        using var provider = Provider(bus, time, _ => Task.FromResult(
            Acquisition(Owner(), time.GetUtcNow(), 0.5, time.GetUtcNow().AddDays(2))));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blockedValuePublication = provider.PublishCurrentAsync();
        Assert.IsFalse(blockedValuePublication.IsCompleted);

        time.ShiftUtcOnly(TimeSpan.FromDays(-1));
        time.Advance(AntigravityQuotaProvider.MaximumCacheAge);
        await provider.ExpireValidityOnceAsync();
        await blockedValuePublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        var next = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("cache_expired", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task RetryAfterBeyondCacheLifetimeInvalidatesCacheAndDoesNotRetryEarly()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        var owner = Owner();
        var acquisitionCount = 0;
        var sequence = new Queue<AntigravityQuotaAcquisition>([
            Acquisition(owner, time.GetUtcNow(), 0.6, time.GetUtcNow().AddDays(2)),
            AntigravityQuotaAcquisition.Failure("rate_limited", MetricState.Unavailable, owner,
                TimeSpan.FromMinutes(20)),
            Acquisition(owner, time.GetUtcNow(), 0.7, time.GetUtcNow().AddDays(2))
        ]);
        using var sequencedProvider = Provider(bus, time, _ =>
        {
            acquisitionCount++;
            return Task.FromResult(sequence.Dequeue());
        });

        await sequencedProvider.PollOnceAsync();
        time.Advance(AntigravityQuotaProvider.PollInterval);
        await sequencedProvider.PollOnceAsync();
        var unavailable = await PublishOneAsync(sequencedProvider, bus, MetricId);
        Assert.AreEqual(MetricState.Unavailable, unavailable.State);
        Assert.IsNull(unavailable.Value);
        Assert.AreEqual("rate_limited", unavailable.ErrorCode);

        time.Advance(TimeSpan.FromMinutes(15));
        await sequencedProvider.ExpireValidityOnceAsync();
        await sequencedProvider.PollOnceAsync();
        Assert.AreEqual(2, acquisitionCount, "The expired cache must not shorten the server's 20-minute retry delay.");

        time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));
        await sequencedProvider.PollOnceAsync();
        Assert.AreEqual(2, acquisitionCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await sequencedProvider.PollOnceAsync();
        Assert.AreEqual(3, acquisitionCount);
    }

    [TestMethod]
    public async Task SummaryRetryAfterSuppressesOwnerVerificationAndPollingUntilItExpires()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var handler = new ScriptedHandler((index, _) => index switch
        {
            1 or 3 or 4 or 6 or 7 or 9 => JsonResponse(StatusA),
            2 or 8 => JsonResponse(SummaryPayload),
            5 => RateLimited(TimeSpan.FromMinutes(20)),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });
        using var client = new HttpClient(handler);
        using var acquirer = new AntigravityQuotaAcquirer(client, TimeProvider.System,
            _ => Task.FromResult(new[] { ProcessOwner() }));
        using var bus = new MetricBus(1);
        using var provider = new AntigravityQuotaProvider(new MetricRegistry(), bus,
            acquirer.AcquireOnceAsync, acquirer.VerifyOwnerAsync, time);

        await provider.PollOnceAsync();
        Assert.AreEqual(3, handler.Requests.Count);
        time.Advance(AntigravityQuotaProvider.PollInterval);
        await provider.PollOnceAsync();
        Assert.AreEqual(6, handler.Requests.Count);

        await provider.VerifyOwnerOnceAsync();
        await provider.PollOnceAsync();
        Assert.AreEqual(6, handler.Requests.Count,
            "A summary Retry-After must suppress both profile verification and quota acquisition.");

        time.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        await provider.VerifyOwnerOnceAsync();
        await provider.PollOnceAsync();
        Assert.AreEqual(6, handler.Requests.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        await provider.PollOnceAsync();
        Assert.AreEqual(9, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SummaryRetryAfterDoesNotPublishCachedQuotaAfterSessionChanges()
    {
        var time = new ManualTimeProvider(InitialUtc);
        var sessionChanged = false;
        var sourceOwner = ProcessOwner();
        using var handler = new ScriptedHandler((index, request) =>
        {
            if (request.RequestUri!.Segments.Last() == "GetUserStatus")
                return JsonResponse(sessionChanged ? StatusB : StatusA);
            return index == 5
                ? RateLimited(TimeSpan.FromMinutes(20))
                : JsonResponse(SummaryPayload);
        });
        using var client = new HttpClient(handler);
        using var acquirer = new AntigravityQuotaAcquirer(client, TimeProvider.System, _ =>
            Task.FromResult(new[]
            {
                sessionChanged ? sourceOwner with { CsrfToken = "synthetic-session-B" } : sourceOwner
            }));
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        using var provider = new AntigravityQuotaProvider(new MetricRegistry(), bus,
            acquirer.AcquireOnceAsync, acquirer.VerifyOwnerAsync, time);

        await provider.PollOnceAsync();
        Assert.AreEqual(3, handler.Requests.Count);
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(12.3456, reader.Current.Value!.Value.AsDouble(), 0.0000001);
        await provider.PublishCurrentAsync();
        var pendingOldPublication = provider.PublishCurrentAsync();
        Assert.IsFalse(pendingOldPublication.IsCompleted);

        Assert.IsTrue(AntigravityQuotaProvider.PollInterval < AntigravityQuotaProvider.MaximumCacheAge);
        time.Advance(AntigravityQuotaProvider.PollInterval);
        await provider.PollOnceAsync();
        Assert.AreEqual(6, handler.Requests.Count);
        await pendingOldPublication.WaitAsync(TimeSpan.FromSeconds(1));

        sessionChanged = true;
        await provider.VerifyOwnerOnceAsync();
        await provider.PollOnceAsync();
        Assert.AreEqual(6, handler.Requests.Count);

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(12.3456, reader.Current.Value!.Value.AsDouble(), 0.0000001,
            "This value was buffered before the rate limit and cannot be retracted.");
        var afterBackoffStart = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await afterBackoffStart.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("rate_limited", reader.Current.ErrorCode);
        Assert.AreEqual(6, handler.Requests.Count);

        time.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        await provider.VerifyOwnerOnceAsync();
        await provider.PollOnceAsync();
        Assert.AreEqual(6, handler.Requests.Count);
    }

    [TestMethod]
    [DataRow(429, 20, "rate_limited")]
    [DataRow(503, 5, "upstream_unavailable")]
    public async Task UnverifiableProfileClearsValueAndHonorsRetryDelay(int statusCode, int retryMinutes,
        string expectedError)
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var handler = new ScriptedHandler((index, _) => index switch
        {
            1 or 3 or 5 or 7 => JsonResponse(StatusA),
            2 or 6 => JsonResponse(SummaryPayload),
            4 => HttpFailure(statusCode, retryMinutes),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });
        using var client = new HttpClient(handler);
        using var acquirer = new AntigravityQuotaAcquirer(client, TimeProvider.System,
            _ => Task.FromResult(new[] { ProcessOwner() }));
        using var bus = new MetricBus(1);
        using var provider = new AntigravityQuotaProvider(new MetricRegistry(), bus,
            acquirer.AcquireOnceAsync, acquirer.VerifyOwnerAsync, time);

        await provider.PollOnceAsync();
        await provider.VerifyOwnerOnceAsync();
        Assert.AreEqual(4, handler.Requests.Count);

        var invalidated = await PublishOneAsync(provider, bus, MetricId);
        Assert.IsNull(invalidated.Value);
        Assert.AreEqual(MetricState.Unavailable, invalidated.State);
        Assert.AreEqual(expectedError, invalidated.ErrorCode);

        await provider.VerifyOwnerOnceAsync();
        await provider.PollOnceAsync();
        Assert.AreEqual(4, handler.Requests.Count);

        var retryDelay = statusCode == 429
            ? TimeSpan.FromMinutes(retryMinutes)
            : AntigravityQuotaProvider.PollInterval;
        time.Advance(retryDelay - TimeSpan.FromSeconds(1));
        await provider.VerifyOwnerOnceAsync();
        await provider.PollOnceAsync();
        Assert.AreEqual(4, handler.Requests.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        await provider.PollOnceAsync();
        Assert.AreEqual(7, handler.Requests.Count);
        var recovered = await PublishOneAsync(provider, bus, MetricId);
        Assert.AreEqual(12.3456, recovered.Value!.Value.AsDouble(), 0.0000001);
    }

    [TestMethod]
    [DataRow("account")]
    [DataRow("session")]
    public async Task PositiveAccountOrSessionTransitionRemainsImmediatelyPollable(string transition)
    {
        var time = new ManualTimeProvider(InitialUtc);
        var sessionChanged = false;
        var sourceOwner = ProcessOwner();
        using var handler = new ScriptedHandler((index, request) =>
        {
            if (request.RequestUri!.Segments.Last() == "GetUserStatus")
                return JsonResponse(transition == "account" && index >= 4 ? StatusB : StatusA);
            return request.RequestUri.Segments.Last() == "RetrieveUserQuotaSummary"
                ? JsonResponse(SummaryPayload)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        using var acquirer = new AntigravityQuotaAcquirer(client, TimeProvider.System,
            _ => Task.FromResult(new[]
            {
                sessionChanged ? sourceOwner with { CsrfToken = "synthetic-next-session" } : sourceOwner
            }));
        using var bus = new MetricBus(1);
        using var provider = new AntigravityQuotaProvider(new MetricRegistry(), bus,
            acquirer.AcquireOnceAsync, acquirer.VerifyOwnerAsync, time);

        await provider.PollOnceAsync();
        Assert.AreEqual(3, handler.Requests.Count);
        if (transition == "session")
            sessionChanged = true;

        await provider.VerifyOwnerOnceAsync();
        Assert.AreEqual(transition == "account" ? 4 : 3, handler.Requests.Count);
        await provider.PollOnceAsync();
        Assert.AreEqual(transition == "account" ? 7 : 6, handler.Requests.Count);
        Assert.IsTrue(handler.Requests.Skip(transition == "account" ? 4 : 3)
            .All(request => request.Csrf ==
                (transition == "session" ? "synthetic-next-session" : "synthetic-csrf")));

        var recovered = await PublishOneAsync(provider, bus, MetricId);
        Assert.AreEqual(12.3456, recovered.Value!.Value.AsDouble(), 0.0000001);
    }

    [TestMethod]
    public async Task AuthRejectionAndMissingOwnershipClearPreviouslyValidCache()
    {
        foreach (var failure in new[] {
            AntigravityQuotaAcquisition.Failure("auth_required", MetricState.Error, Owner()),
            AntigravityQuotaAcquisition.Failure("identity_unavailable", MetricState.Unavailable)
        })
        {
            var time = new ManualTimeProvider(InitialUtc);
            using var bus = new MetricBus(1);
            var sequence = new Queue<AntigravityQuotaAcquisition>([
                Acquisition(Owner(), InitialUtc, 0.6, InitialUtc.AddDays(2)), failure
            ]);
            using var provider = Provider(bus, time, _ => Task.FromResult(sequence.Dequeue()));
            await provider.PollOnceAsync();
            var first = await PublishOneAsync(provider, bus, MetricId);
            Assert.AreEqual(MetricState.Stale, first.State);
            Assert.AreEqual("source_freshness_unknown", first.ErrorCode);
            time.Advance(AntigravityQuotaProvider.PollInterval);
            await provider.PollOnceAsync();
            var rejected = await PublishOneAsync(provider, bus, MetricId);
            Assert.IsNull(rejected.Value);
            Assert.AreEqual(failure.FailureState, rejected.State);
            Assert.AreEqual(failure.ErrorCode, rejected.ErrorCode);
        }
    }

    [TestMethod]
    public async Task OwnerMonitorRejectsLateOldOwnerAcquisition()
    {
        var time = new ManualTimeProvider(InitialUtc);
        using var bus = new MetricBus(1);
        var completion = new TaskCompletionSource<AntigravityQuotaAcquisition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var provider = new AntigravityQuotaProvider(new MetricRegistry(), bus,
            _ => ++count == 1 ? Task.FromResult(Acquisition(Owner(), InitialUtc, 0.6, InitialUtc.AddDays(2)))
                : completion.Task,
            static (_, _) => Task.FromResult(new AntigravityOwnerVerification(
                AntigravityOwnerVerificationState.Changed, null)), time);
        await provider.PollOnceAsync();
        time.Advance(AntigravityQuotaProvider.PollInterval);
        var pending = provider.PollOnceAsync();
        Assert.IsFalse(pending.IsCompleted);
        await provider.VerifyOwnerOnceAsync();
        completion.SetResult(Acquisition(Owner(), InitialUtc, 0.9, InitialUtc.AddDays(2)));
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await PublishOneAsync(provider, bus, MetricId);
        Assert.IsNull(result.Value);
        Assert.AreEqual("identity_unavailable", result.ErrorCode);
    }

    [TestMethod]
    public async Task ShutdownPublicationIsBoundedWhenSubscriberBackpressurePersists()
    {
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        using var provider = Provider(bus, TimeProvider.System, _ => Task.FromResult(
            Acquisition(Owner(), DateTimeOffset.UtcNow, 0.3, DateTimeOffset.UtcNow.AddDays(1))));

        await provider.PollOnceAsync();
        var initial = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);
        await provider.PublishCurrentAsync();
        var blocked = provider.PublishCurrentAsync();
        Assert.IsFalse(blocked.IsCompleted);

        using var stop = new CancellationTokenSource();
        var run = provider.RunAsync(stop.Token);
        var timer = Stopwatch.StartNew();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(2));
        await blocked.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task WorkerFaultStillPublishesStoppedAndReachesSupervisor()
    {
        using var bus = new MetricBus(1);
        await using var reader = bus.Subscribe(new MetricId(MetricId)).GetAsyncEnumerator();
        var stopped = reader.MoveNextAsync().AsTask();
        using var provider = Provider(bus, new ThrowingTimerTimeProvider(), _ => Task.FromResult(
            Acquisition(Owner(), DateTimeOffset.UtcNow, 0.3, DateTimeOffset.UtcNow.AddDays(1))));

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => provider.RunAsync(CancellationToken.None));
        Assert.AreEqual("synthetic timer creation failure", failure.Message);
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
    }

    internal static async Task PublishSyntheticQuotaAsync(MetricRegistry registry, MetricBus bus)
    {
        using var provider = CreateSyntheticProvider(registry, bus);
        await provider.PollOnceAsync();
        await provider.PublishCurrentAsync();
    }

    internal static AntigravityQuotaProvider CreateSyntheticProvider(MetricRegistry registry, MetricBus bus)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var reset = observedAt.AddDays(1).ToString("O");
        var payload = JsonSerializer.Serialize(new
        {
            response = new { groups = new[] { new { buckets = new[]
            {
                new { bucketId = "3p-weekly", remainingFraction = 0.75, resetTime = reset },
                new { bucketId = "3p-5h", remainingFraction = 0.5, resetTime = reset },
                new { bucketId = "gemini-weekly", remainingFraction = 0.25, resetTime = reset },
                new { bucketId = "gemini-5h", remainingFraction = 1d, resetTime = reset }
            } } } }
        });
        var acquisition = new AntigravityQuotaAcquisition(Owner(),
            AntigravityQuotaParser.Parse(Encoding.UTF8.GetBytes(payload), observedAt), MetricState.Error, null);
        return new AntigravityQuotaProvider(registry, bus,
            _ => Task.FromResult(acquisition),
            static (owner, _) => Task.FromResult(new AntigravityOwnerVerification(
                AntigravityOwnerVerificationState.Verified, owner)), TimeProvider.System);
    }

    private static async Task<MetricSnapshot> PublishOneAsync(AntigravityQuotaProvider provider, MetricBus bus,
        string metricId)
    {
        await using var reader = bus.Subscribe(new MetricId(metricId)).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await provider.PublishCurrentAsync();
        Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(1)));
        return reader.Current;
    }

    private static AntigravityQuotaProvider Provider(MetricBus bus, TimeProvider time,
        Func<CancellationToken, Task<AntigravityQuotaAcquisition>> acquire) =>
        new(new MetricRegistry(), bus, acquire,
            static (owner, _) => Task.FromResult(new AntigravityOwnerVerification(
                AntigravityOwnerVerificationState.Verified, owner)), time);

    private static AntigravityOwnerKey Owner(string email = "synthetic-account-A", int processId = 42,
        string start = "synthetic-start-A", string sessionGeneration = "synthetic-session-generation-A",
        int listenerPort = 43123) =>
        new(email, processId, start, sessionGeneration, listenerPort);

    private static AntigravityProcessOwner ProcessOwner(string csrfToken = "synthetic-csrf") =>
        new(42, "synthetic-start-A", csrfToken, [43123]);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage RateLimited(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static HttpResponseMessage HttpFailure(int statusCode, int retryMinutes)
    {
        var response = new HttpResponseMessage((HttpStatusCode)statusCode);
        if (statusCode == 429)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(retryMinutes));
        return response;
    }

    private static AntigravityQuotaAcquisition Acquisition(AntigravityOwnerKey owner, DateTimeOffset observedAt,
        double fraction, DateTimeOffset resetAt)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            response = new { groups = new[] { new { buckets = new[] {
                new { bucketId = "3p-weekly", remainingFraction = fraction, resetTime = resetAt.ToString("O") }
            } } } }
        });
        return new AntigravityQuotaAcquisition(owner, AntigravityQuotaParser.Parse(payload, observedAt),
            MetricState.Error, null);
    }

    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestObservation> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Requests.Count + 1;
            Requests.Add(new RequestObservation(request.RequestUri!,
                request.Headers.GetValues("X-Codeium-Csrf-Token").Single()));
            var response = respond(index, request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed record RequestObservation(Uri Uri, string Csrf);

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }

        public void ShiftUtcOnly(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class ThrowingTimerTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("synthetic timer creation failure");
    }
}
