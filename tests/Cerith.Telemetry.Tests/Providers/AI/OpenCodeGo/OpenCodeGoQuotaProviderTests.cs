#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class OpenCodeGoQuotaProviderTests
{
    private static readonly string[] MetricIds =
    [
        "ai.opencode-go.quota.rolling.used",
        "ai.opencode-go.quota.weekly.used",
        "ai.opencode-go.quota.monthly.used",
        "ai.opencode-go.quota.rolling.reset",
        "ai.opencode-go.quota.weekly.reset",
        "ai.opencode-go.quota.monthly.reset"
    ];

    private static readonly DateTimeOffset InitialUtc = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RegistersOnlyUsedPercentAndSourceResetMetrics()
    {
        using var harness = new Harness(writeAuth: false);
        var descriptors = harness.Registry.GetSnapshot().OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(MetricIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            descriptors.Select(item => item.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[]
            {
                MetricValueKind.DateTimeOffset, MetricValueKind.Double,
                MetricValueKind.DateTimeOffset, MetricValueKind.Double,
                MetricValueKind.DateTimeOffset, MetricValueKind.Double
            }, descriptors.Select(item => item.ValueKind).ToArray());
        Assert.IsTrue(descriptors.Where(item => item.SemanticType == "quota_used")
            .All(item => item.Unit == "%"));
        Assert.IsTrue(descriptors.Where(item => item.SemanticType == "timestamp")
            .All(item => item.Unit is null));
    }

    [TestMethod]
    public void ParsesExactUsageWindowsWithoutTurningBalanceOrStatusIntoMetrics()
    {
        var reading = Parse("""
            {"balance":250,"usage":{
              "rolling":{"status":"rate-limited","percent":100,"resetsAt":"2026-10-01T09:00:00.000Z"},
              "weekly":{"status":"ok","percent":37.5,"resetsAt":"2026-10-08T08:00:00+00:00"},
              "monthly":{"status":"ok","percent":0,"resetsAt":"2026-11-01T08:00:00Z"}
            }}
            """);

        Assert.AreEqual(100d, reading.Points[OpenCodeGoQuotaParser.RollingUsed].Value!.Value.AsDouble());
        Assert.AreEqual(37.5d, reading.Points[OpenCodeGoQuotaParser.WeeklyUsed].Value!.Value.AsDouble());
        Assert.AreEqual(0d, reading.Points[OpenCodeGoQuotaParser.MonthlyUsed].Value!.Value.AsDouble());
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-08T08:00:00Z"),
            reading.Points[OpenCodeGoQuotaParser.WeeklyReset].Value!.Value.AsDateTimeOffset());
        Assert.IsTrue(reading.Points.All(point => point.State == MetricState.Fresh));
    }

    [TestMethod]
    public void MissingBucketsAndFieldsStayUnavailableAndMalformedFieldsFailIndependently()
    {
        var missing = Parse("""
            {"usage":{"rolling":{"percent":12},"weekly":{"resetsAt":"2026-10-08T08:00:00Z"},"monthly":null}}
            """);
        Assert.AreEqual(MetricState.Fresh, missing.Points[OpenCodeGoQuotaParser.RollingUsed].State);
        Assert.AreEqual(MetricState.Unavailable, missing.Points[OpenCodeGoQuotaParser.RollingReset].State);
        Assert.AreEqual(MetricState.Unavailable, missing.Points[OpenCodeGoQuotaParser.WeeklyUsed].State);
        Assert.AreEqual(MetricState.Fresh, missing.Points[OpenCodeGoQuotaParser.WeeklyReset].State);
        Assert.IsTrue(new[] { 2, 5 }.All(index => missing.Points[index].State == MetricState.Unavailable));

        var malformed = Parse("""
            {"usage":{"rolling":{"percent":101,"resetsAt":"not-a-timestamp"},"weekly":{"percent":100,"resetsAt":"2026-10-01T07:59:59Z"},"monthly":{"percent":-1}}}
            """);
        Assert.AreEqual("invalid_quota", malformed.Points[0].ErrorCode);
        Assert.AreEqual("invalid_reset", malformed.Points[3].ErrorCode);
        Assert.AreEqual(MetricState.Unavailable, malformed.Points[1].State,
            "An already-crossed reset makes that bucket's reported usage obsolete.");
        Assert.AreEqual("reset_elapsed", malformed.Points[4].ErrorCode);
        Assert.AreEqual("invalid_quota", malformed.Points[2].ErrorCode);
        Assert.AreEqual(MetricState.Unavailable, malformed.Points[5].State);

        var invalidDocument = OpenCodeGoQuotaParser.Parse(Encoding.UTF8.GetBytes("{"), InitialUtc);
        Assert.IsTrue(invalidDocument.Points.All(point => point.State == MetricState.Error &&
            point.ErrorCode == "schema_invalid"));
        Assert.IsTrue(Parse("{}").Points.All(point => point.State == MetricState.Unavailable));
    }

    [TestMethod]
    public void ReadsOnlyTheOfficialGoApiCredentialAndDoesNotTreatOauthAsAnApiKey()
    {
        using var harness = new Harness(writeAuth: false);
        harness.WriteAuth("synthetic-opencode-go-key");
        var ready = harness.AuthReader.Read();

        Assert.AreEqual(OpenCodeGoAuthStatus.Ready, ready.Status);
        Assert.AreEqual("synthetic-opencode-go-key", ready.Credential!.ApiKey);
        Assert.AreEqual(64, ready.Credential.Fingerprint.Length);
        Assert.AreNotEqual(ready.Credential.ApiKey, ready.Credential.Fingerprint);

        harness.WriteAuth("synthetic-oauth-value", "oauth");
        Assert.AreEqual(OpenCodeGoAuthStatus.Unsupported, harness.AuthReader.Read().Status);
    }

    [TestMethod]
    public async Task UsesFixedBearerGetAndPublishesTheSixDirectSourceValues()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        var snapshots = await PollAndReadAllAsync(harness);

        var request = harness.Handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(OpenCodeGoQuotaProvider.UsageEndpoint, request.Uri);
        Assert.AreEqual("Bearer", request.AuthorizationScheme);
        Assert.AreEqual("synthetic-opencode-go-key", request.AuthorizationParameter);
        Assert.IsNull(request.ContentType);
        Assert.AreEqual(100d, snapshots[0].Value!.Value.AsDouble());
        Assert.AreEqual(37.5d, snapshots[1].Value!.Value.AsDouble());
        Assert.AreEqual(0d, snapshots[2].Value!.Value.AsDouble());
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-01T09:00:00Z"), snapshots[3].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(MetricState.Fresh, snapshots[0].State);
        Assert.AreEqual(MetricConfidence.Measured, snapshots[0].Confidence);
        Assert.AreEqual("OpenCodeGo.Usage.ReadOnly.v1", snapshots[0].Source);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.ObservedAt == InitialUtc));
    }

    [TestMethod]
    public async Task MissingAuthMakesNoRequestAndLateResponseIsDiscardedAfterKeyChange()
    {
        using (var noAuth = new Harness(writeAuth: false))
        {
            await noAuth.Provider.PollOnceAsync();
            Assert.AreEqual(0, noAuth.Handler.Requests.Length);
        }

        using var harness = new Harness();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var poll = harness.Provider.PollOnceAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.WriteAuth("synthetic-replacement-key");
        await harness.Provider.ObserveLocalStateAsync();
        await poll.WaitAsync(TimeSpan.FromSeconds(2));

        var current = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(current.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        Assert.AreEqual(1, harness.Handler.Requests.Length);
    }

    private static string UsageJson => """
        {"usage":{
          "rolling":{"status":"ok","percent":100,"resetsAt":"2026-10-01T09:00:00Z"},
          "weekly":{"status":"ok","percent":37.5,"resetsAt":"2026-10-08T08:00:00Z"},
          "monthly":{"status":"ok","percent":0,"resetsAt":"2026-11-01T08:00:00Z"}
        }}
        """;

    private static OpenCodeGoQuotaReading Parse(string json) =>
        OpenCodeGoQuotaParser.Parse(Encoding.UTF8.GetBytes(json), InitialUtc);

    private static async Task<MetricSnapshot[]> PollAndReadAllAsync(Harness harness)
    {
        var readers = StartReaders(harness.Bus);
        try
        {
            await harness.Provider.PollOnceAsync();
            await harness.Provider.PublishCurrentAsync();
            await Task.WhenAll(readers.Pending);
            return readers.Enumerators.Select(enumerator => enumerator.Current).ToArray();
        }
        finally
        {
            foreach (var enumerator in readers.Enumerators) await enumerator.DisposeAsync();
        }
    }

    private static async Task<MetricSnapshot[]> PublishAndReadAllAsync(Harness harness)
    {
        var readers = StartReaders(harness.Bus);
        try
        {
            await harness.Provider.PublishCurrentAsync();
            await Task.WhenAll(readers.Pending);
            return readers.Enumerators.Select(enumerator => enumerator.Current).ToArray();
        }
        finally
        {
            foreach (var enumerator in readers.Enumerators) await enumerator.DisposeAsync();
        }
    }

    private static (IAsyncEnumerator<MetricSnapshot>[] Enumerators, Task<bool>[] Pending) StartReaders(MetricBus bus)
    {
        var enumerators = MetricIds.Select(id => bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = enumerators.Select(enumerator => enumerator.MoveNextAsync().AsTask()).ToArray();
        return (enumerators, pending);
    }

    private sealed class Harness : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "cerith-opencode-go-" + Guid.NewGuid().ToString("N"));
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public ManualTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public OpenCodeGoAuthReader AuthReader { get; }
        public OpenCodeGoQuotaProvider Provider { get; }

        public Harness(bool writeAuth = true)
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            AuthReader = new OpenCodeGoAuthReader(DirectoryPath);
            if (writeAuth) WriteAuth("synthetic-opencode-go-key");
            Provider = new OpenCodeGoQuotaProvider(Registry, Bus, AuthReader, Handler, Time);
        }

        public void WriteAuth(string key, string type = "api")
        {
            var entry = new Dictionary<string, object> { ["type"] = type, ["key"] = key };
            var document = new Dictionary<string, object>
            {
                ["opencode-go"] = entry,
                ["synthetic-unrelated-provider"] = new { type = "api", key = "synthetic-other-provider-key" }
            };
            File.WriteAllText(Path.Combine(DirectoryPath, "auth.json"), JsonSerializer.Serialize(document));
        }

        public void Dispose()
        {
            Provider.Dispose();
            Bus.Dispose();
            try { System.IO.Directory.Delete(DirectoryPath, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
        private readonly ConcurrentQueue<RequestObservation> _requests = new();

        public RequestObservation[] Requests => _requests.ToArray();

        public void EnqueueJson(string json) => _responses.Enqueue((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));

        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var authorization = request.Headers.Authorization;
            _requests.Enqueue(new RequestObservation(request.Method, request.RequestUri!, authorization?.Scheme,
                authorization?.Parameter, request.Content?.Headers.ContentType?.MediaType));
            var response = _responses.TryDequeue(out var next)
                ? await next(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, string? AuthorizationScheme,
        string? AuthorizationParameter, string? ContentType);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
