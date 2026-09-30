#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Antigravity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Antigravity;

[TestClass]
public sealed class AntigravityQuotaAcquirerTests
{
    private const string Status = "{\"userStatus\":{\"email\":\"synthetic-A@example.invalid\"}}";
    private const string Summary = """
        {"response":{"groups":[{"buckets":[
          {"bucketId":"3p-weekly","remainingFraction":0.123456,"resetTime":"2030-01-01T00:00:00Z"},
          {"bucketId":"gemini-5h","remainingFraction":0,"resetTime":"2030-01-01T00:00:00Z"}
        ]}]}}
        """;
    private static readonly AntigravityProcessOwner Owner = new(42, "synthetic-start", "synthetic-csrf", [43123]);

    [TestMethod]
    public async Task RealAcquisitionUsesOnlyLoopbackPostAndRechecksOwnershipBeforeEverySend()
    {
        using var handler = new Handler(Json(Status), Json(Summary), Json(Status));
        using var client = new HttpClient(handler);
        var ownerChecks = 0;
        using var acquirer = new AntigravityQuotaAcquirer(client, TimeProvider.System, _ =>
        {
            ownerChecks++;
            return Task.FromResult(new[] { Owner });
        });

        var result = await acquirer.AcquireOnceAsync();
        Assert.IsNotNull(result.Reading);
        Assert.AreEqual(12.3456, result.Reading.Points[0].Value!.Value.AsDouble(), 0.0000001);
        Assert.AreEqual(0d, result.Reading.Points[3].Value!.Value.AsDouble());
        Assert.AreEqual(3, handler.Requests.Count);
        Assert.IsTrue(ownerChecks >= 6);
        CollectionAssert.AreEqual(new[] { "GetUserStatus", "RetrieveUserQuotaSummary", "GetUserStatus" },
            handler.Requests.Select(request => request.Uri.Segments.Last()).ToArray());
        Assert.IsTrue(handler.Requests.All(request => request.Method == HttpMethod.Post &&
            request.Uri.Scheme == "https" && request.Uri.Host == "127.0.0.1" && request.Uri.Port == 43123 &&
            request.Csrf == "synthetic-csrf" && request.ContentType == "application/json"));
        Assert.IsNull(result.ErrorCode);
    }

    [TestMethod]
    public async Task OwnerChangeBeforeCredentialPostSendsNothing()
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var checks = 0;
        using var acquirer = new AntigravityQuotaAcquirer(client, TimeProvider.System, _ =>
            Task.FromResult(new[] { ++checks == 1 ? Owner : Owner with { ProcessStartIdentity = "new-start" } }));
        var result = await acquirer.AcquireOnceAsync();
        Assert.AreEqual("identity_mismatch", result.ErrorCode);
        Assert.IsNull(result.Owner);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AccountMismatchAfterResponseDiscardsQuota()
    {
        using var handler = new Handler(Json(Status), Json(Summary),
            Json("{\"userStatus\":{\"email\":\"synthetic-B@example.invalid\"}}"));
        using var client = new HttpClient(handler);
        using var acquirer = Acquirer(client);
        var result = await acquirer.AcquireOnceAsync();
        Assert.AreEqual("identity_mismatch", result.ErrorCode);
        Assert.IsNull(result.Reading);
        Assert.IsNull(result.Owner);
    }

    [TestMethod]
    public async Task MissingOrAmbiguousOfficialOwnerDoesNotSendAuth()
    {
        foreach (var owners in new[] { Array.Empty<AntigravityProcessOwner>(), new[] { Owner, Owner } })
        {
            using var handler = new Handler();
            using var client = new HttpClient(handler);
            using var acquirer = new AntigravityQuotaAcquirer(client, TimeProvider.System,
                _ => Task.FromResult(owners));
            var result = await acquirer.AcquireOnceAsync();
            Assert.IsNull(result.Owner);
            Assert.IsNull(result.Reading);
            Assert.AreEqual(0, handler.Requests.Count);
        }
    }

    [TestMethod]
    [DataRow(401, "auth_required")]
    [DataRow(403, "auth_forbidden")]
    [DataRow(302, "redirect_rejected")]
    [DataRow(500, "upstream_unavailable")]
    public async Task HttpFailuresHaveSanitizedValuelessStates(int status, string code)
    {
        using var handler = new Handler(new HttpResponseMessage((HttpStatusCode)status));
        using var client = new HttpClient(handler);
        using var acquirer = Acquirer(client);
        var result = await acquirer.AcquireOnceAsync();
        Assert.AreEqual(code, result.ErrorCode);
        Assert.IsNull(result.Reading);
        Assert.IsNull(result.Owner);
    }

    [TestMethod]
    public async Task RateLimitPreservesLongRetryAfterWithVerifiedOwner()
    {
        var rateLimit = new HttpResponseMessage((HttpStatusCode)429);
        rateLimit.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        using var handler = new Handler(Json(Status), rateLimit, Json(Status));
        using var client = new HttpClient(handler);
        using var acquirer = Acquirer(client);
        var result = await acquirer.AcquireOnceAsync();
        Assert.AreEqual("rate_limited", result.ErrorCode);
        Assert.AreEqual(TimeSpan.FromHours(1), result.RetryAfter);
        Assert.IsNotNull(result.Owner);
        Assert.IsNull(result.Reading);
    }

    [TestMethod]
    public async Task OversizedResponseIsRejectedBeforeParsing()
    {
        using var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[AntigravityQuotaAcquirer.MaxResponseBytes + 1])
        });
        using var client = new HttpClient(handler);
        using var acquirer = Acquirer(client);
        var result = await acquirer.AcquireOnceAsync();
        Assert.AreEqual("response_too_large", result.ErrorCode);
        Assert.IsNull(result.Reading);
    }

    [TestMethod]
    public async Task OfflineAndCallerCancellationRemainDistinct()
    {
        using var handler = new Handler();
        handler.Respond = (_, _) => throw new HttpRequestException("synthetic private error");
        using var client = new HttpClient(handler);
        using var acquirer = Acquirer(client);
        Assert.AreEqual("offline", (await acquirer.AcquireOnceAsync()).ErrorCode);
        handler.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };
        using var stop = new CancellationTokenSource();
        var pending = acquirer.AcquireOnceAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [TestMethod]
    public async Task DeadlineCancelsHangingHttpWithoutAuthMutation()
    {
        using var handler = new Handler();
        handler.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };
        using var client = new HttpClient(handler);
        var time = new DeadlineTimeProvider();
        using var acquirer = new AntigravityQuotaAcquirer(client, time, _ => Task.FromResult(new[] { Owner }));
        var pending = acquirer.AcquireOnceAsync();
        Assert.IsFalse(pending.IsCompleted);
        time.FireDeadline();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("timeout", result.ErrorCode);
        Assert.IsNull(result.Owner);
        Assert.IsNull(result.Reading);
    }

    [TestMethod]
    public void ParserPreservesExactUnitsAndIndependentMissingInvalidFields()
    {
        var now = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var expectedReset = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var reading = AntigravityQuotaParser.Parse(Encoding.UTF8.GetBytes(Summary), now);
        Assert.AreEqual(12.3456, reading.Points[0].Value!.Value.AsDouble(), 0.0000001);
        Assert.AreEqual(expectedReset, reading.Points[4].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(MetricState.Unavailable, reading.Points[5].State);
        Assert.AreEqual(expectedReset, reading.Points[7].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(MetricState.Unavailable, reading.Points[1].State);
        var invalid = Parse("""
            {"response":{"groups":[{"buckets":[
              {"bucketId":"3p-weekly","remainingFraction":null,"resetTime":"2030-01-01"},
              {"bucketId":"3p-5h","remainingFraction":1.1,"resetTime":"1893456000"},
              {"bucketId":"gemini-weekly","remainingFraction":1,"resetTime":"2020-01-01T00:00:00Z"},
              {"bucketId":"gemini-5h","remainingFraction":1,"resetTime":1893456000}
            ]}]}}
            """, now);
        Assert.AreEqual(MetricState.Unavailable, invalid.Points[0].State);
        Assert.AreEqual("invalid_reset", invalid.Points[4].ErrorCode);
        Assert.AreEqual("invalid_reset", invalid.Points[5].ErrorCode);
        Assert.AreEqual("invalid_reset", invalid.Points[7].ErrorCode);
        Assert.AreEqual("invalid_quota", invalid.Points[1].ErrorCode);
        Assert.AreEqual("reset_elapsed", invalid.Points[2].ErrorCode);
        Assert.IsNull(invalid.Points[2].Value);
        Assert.IsTrue(Parse("[]", now).Points.All(point => point.ErrorCode == "schema_invalid"));
        Assert.IsTrue(Parse("{\"response\":{\"groups\":[{\"buckets\":[{\"bucketId\":\"unknown\"}]}]}}", now)
            .Points.All(point => point.ErrorCode == "schema_invalid"));
    }

    private static AntigravityQuotaReading Parse(string json, DateTimeOffset now) =>
        AntigravityQuotaParser.Parse(Encoding.UTF8.GetBytes(json), now);
    private static AntigravityQuotaAcquirer Acquirer(HttpClient client) =>
        new(client, TimeProvider.System, _ => Task.FromResult(new[] { Owner }));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new Request(request.Method, request.RequestUri!,
                request.Headers.GetValues("X-Codeium-Csrf-Token").Single(),
                request.Content!.Headers.ContentType!.MediaType!));
            if (Respond is not null)
                return Respond(request, token);
            var response = _responses.Dequeue();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed record Request(HttpMethod Method, Uri Uri, string Csrf, string ContentType);

    private sealed class DeadlineTimeProvider : TimeProvider
    {
        private Action? _fire;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(AntigravityQuotaAcquirer.RequestTimeout, dueTime);
            _fire = () => callback(state);
            return new DeadlineTimer();
        }
        public void FireDeadline() => _fire!();

        private sealed class DeadlineTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
