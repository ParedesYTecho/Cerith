#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    public void ReadsTheXdgSelectedStoreAndTracksStoreChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerith-opencode-go-xdg-" + Guid.NewGuid().ToString("N"));
        var oldDataHome = Path.Combine(root, "old-data");
        var selectedDataHome = Path.Combine(root, "selected-data");
        var environment = new Dictionary<string, string?>
        {
            ["XDG_DATA_HOME"] = oldDataHome,
            ["OPENCODE_AUTH_CONTENT"] = null
        };

        try
        {
            WriteXdgAuthFile(oldDataHome, "synthetic-old-owner-key");
            WriteXdgAuthFile(selectedDataHome, "synthetic-selected-owner-key");
            var reader = new OpenCodeGoAuthReader(getEnvironmentVariable: name =>
                environment.TryGetValue(name, out var value) ? value : null);

            var oldOwner = reader.Read();
            Assert.AreEqual(OpenCodeGoAuthStatus.Ready, oldOwner.Status);
            Assert.AreEqual("synthetic-old-owner-key", oldOwner.Credential!.ApiKey);

            environment["XDG_DATA_HOME"] = selectedDataHome;
            var selectedOwner = reader.Read();
            Assert.AreEqual(OpenCodeGoAuthStatus.Ready, selectedOwner.Status);
            Assert.AreEqual("synthetic-selected-owner-key", selectedOwner.Credential!.ApiKey);
            Assert.AreNotEqual(oldOwner.SourceFingerprint, selectedOwner.SourceFingerprint);

            environment["XDG_DATA_HOME"] = "relative-selected-data";
            var unsupported = reader.Read();
            Assert.AreEqual(OpenCodeGoAuthStatus.Error, unsupported.Status);
            Assert.AreEqual("auth_path_unsupported", unsupported.ErrorCode);
            Assert.IsNull(unsupported.Credential);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void MalformedAuthContentOverrideFailsClosedWithoutReadingSelectedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerith-opencode-go-auth-content-" + Guid.NewGuid().ToString("N"));
        var dataHome = Path.Combine(root, "data");
        var environment = new Dictionary<string, string?>
        {
            ["XDG_DATA_HOME"] = dataHome,
            ["OPENCODE_AUTH_CONTENT"] = "{malformed"
        };

        try
        {
            WriteXdgAuthFile(dataHome, "synthetic-disk-owner-key");
            var reader = new OpenCodeGoAuthReader(getEnvironmentVariable: name =>
                environment.TryGetValue(name, out var value) ? value : null);

            var malformed = reader.Read();
            Assert.AreEqual(OpenCodeGoAuthStatus.Error, malformed.Status);
            Assert.AreEqual("auth_file_invalid", malformed.ErrorCode);
            Assert.IsNull(malformed.Credential);

            environment["OPENCODE_AUTH_CONTENT"] = AuthJson("synthetic-selected-content-key");
            var selected = reader.Read();
            Assert.AreEqual(OpenCodeGoAuthStatus.Ready, selected.Status);
            Assert.AreEqual("synthetic-selected-content-key", selected.Credential!.ApiKey);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
        var releaseResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue((_, _) =>
        {
            requestStarted.TrySetResult();
            return releaseResponse.Task;
        });

        var poll = harness.Provider.PollOnceAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.WriteAuth("synthetic-replacement-key");
        await harness.Provider.ObserveLocalStateAsync();
        releaseResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(UsageJson, Encoding.UTF8, "application/json")
        });
        await poll.WaitAsync(TimeSpan.FromSeconds(2));

        var current = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(current.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        Assert.AreEqual(1, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task CacheTurnsStaleAtTwoMinutesAndExpiresAtFifteenMinutesWithoutChangingObservedAt()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();

        harness.Time.Advance(OpenCodeGoQuotaProvider.StaleAfter - TimeSpan.FromSeconds(1));
        await harness.Provider.ObserveLocalStateAsync();
        var beforeStale = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(beforeStale.All(snapshot => snapshot.State == MetricState.Fresh));

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Provider.ObserveLocalStateAsync();
        var stale = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(stale.All(snapshot => snapshot.State == MetricState.Stale && snapshot.Value.HasValue));
        Assert.IsTrue(stale.All(snapshot => snapshot.ObservedAt == InitialUtc));

        harness.Time.Advance(OpenCodeGoQuotaProvider.MaximumCacheAge - OpenCodeGoQuotaProvider.StaleAfter);
        await harness.Provider.ObserveLocalStateAsync();
        var expired = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(expired.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        Assert.IsTrue(expired.All(snapshot => snapshot.ErrorCode == "cache_expired"));
        Assert.AreEqual(1, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task PollCadenceUsesMonotonicSixtySecondIntervals()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        harness.Handler.EnqueueJson(UsageJson);

        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(1, harness.Handler.Requests.Length);
        harness.Time.Advance(TimeSpan.FromSeconds(59));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(1, harness.Handler.Requests.Length);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task ResetCrossingInvalidatesBucketAndCancelsPendingPublication()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJsonWithRollingReset(InitialUtc.AddMinutes(1)));
        await harness.Provider.PollOnceAsync();
        var blocked = await BeginBlockedPublicationAsync(harness, MetricIds[0]);

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.Provider.ObserveLocalStateAsync();
        await AssertCanceledAsync(blocked.Pending);
        Assert.IsTrue(await blocked.Reader.MoveNextAsync());
        await blocked.Reader.DisposeAsync();

        var current = await PublishAndReadAllAsync(harness);
        Assert.AreEqual(MetricState.Unavailable, current[0].State);
        Assert.AreEqual("reset_elapsed", current[0].ErrorCode);
        Assert.IsNull(current[0].Value);
        Assert.AreEqual(MetricState.Unavailable, current[3].State);
        Assert.AreEqual("reset_elapsed", current[3].ErrorCode);
        Assert.IsTrue(current.Skip(1).Where((_, index) => index != 2)
            .All(snapshot => snapshot.State == MetricState.Fresh));
    }

    [TestMethod]
    public async Task HardCacheExpiryCancelsPendingValuePublication()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        var blocked = await BeginBlockedPublicationAsync(harness, MetricIds[0]);

        harness.Time.Advance(OpenCodeGoQuotaProvider.MaximumCacheAge);
        await harness.Provider.ObserveLocalStateAsync();
        await AssertCanceledAsync(blocked.Pending);
        Assert.IsTrue(await blocked.Reader.MoveNextAsync());
        await blocked.Reader.DisposeAsync();

        var current = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(current.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        Assert.IsTrue(current.All(snapshot => snapshot.ErrorCode == "cache_expired"));
    }

    [TestMethod]
    public async Task AuthIdentityChangeCancelsPendingPublicationAndClearsPriorValues()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        var blocked = await BeginBlockedPublicationAsync(harness, MetricIds[0]);

        harness.WriteAuth("synthetic-replacement-key");
        await harness.Provider.ObserveLocalStateAsync();
        await AssertCanceledAsync(blocked.Pending);
        Assert.IsTrue(await blocked.Reader.MoveNextAsync());
        await blocked.Reader.DisposeAsync();

        var current = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(current.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        Assert.AreEqual(1, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task RunAuthObserverCancelsBackpressuredPublicationWhenSelectedStoreChanges()
    {
        var environment = new ConcurrentDictionary<string, string>();
        var authReader = new OpenCodeGoAuthReader(getEnvironmentVariable: name =>
            environment.TryGetValue(name, out var value) ? value : null);
        using var harness = new Harness(writeAuth: false, authReader: authReader);
        var oldDataHome = Path.Combine(harness.DirectoryPath, "old-data");
        var selectedDataHome = Path.Combine(harness.DirectoryPath, "selected-data");
        WriteXdgAuthFile(oldDataHome, "synthetic-same-key");
        WriteXdgAuthFile(selectedDataHome, "synthetic-same-key");
        environment["XDG_DATA_HOME"] = oldDataHome;
        harness.Handler.EnqueueJson(UsageJson);

        var reader = harness.Bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        var run = harness.Provider.RunAsync(cancellation.Token);
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await harness.Bus.PublishAsync(DummySnapshot(MetricIds[0]));
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        environment["XDG_DATA_HOME"] = selectedDataHome;
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1d, reader.Current.Value!.Value.AsDouble());
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(1, harness.Handler.Requests.Length);

        var stopped = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        await reader.DisposeAsync();
    }

    [TestMethod]
    public async Task RepeatedMissingAuthObservationDoesNotCancelUnchangedPublication()
    {
        using var harness = new Harness(writeAuth: false);
        var blocked = await BeginBlockedPublicationAsync(harness, MetricIds[0]);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Provider.ObserveLocalStateAsync();
        Assert.IsFalse(blocked.Pending.IsCompleted);

        Assert.IsTrue(await blocked.Reader.MoveNextAsync());
        await blocked.Pending.WaitAsync(TimeSpan.FromSeconds(2));
        await blocked.Reader.DisposeAsync();
    }

    [TestMethod]
    public async Task TransientFailuresUseSameKeyCacheButFreshMissingFieldsEvictIt()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();

        harness.Time.Advance(OpenCodeGoQuotaProvider.PollInterval);
        harness.Handler.Enqueue(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic offline")));
        await harness.Provider.PollOnceAsync();
        var offline = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(offline.All(snapshot => snapshot.State == MetricState.Stale && snapshot.ErrorCode == "offline"));
        Assert.IsTrue(offline.All(snapshot => snapshot.ObservedAt == InitialUtc));

        harness.Time.Advance(OpenCodeGoQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson("""
            {"usage":{
              "rolling":{"resetsAt":"2026-10-01T09:00:00Z"},
              "weekly":{"percent":40,"resetsAt":"2026-10-08T08:00:00Z"},
              "monthly":{"percent":10,"resetsAt":"2026-11-01T08:00:00Z"}
            }}
            """);
        await harness.Provider.PollOnceAsync();
        var missing = await PublishAndReadAllAsync(harness);
        Assert.AreEqual(MetricState.Unavailable, missing[0].State);
        Assert.IsNull(missing[0].Value);

        harness.Time.Advance(OpenCodeGoQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        await harness.Provider.PollOnceAsync();
        var failedAfterMissing = await PublishAndReadAllAsync(harness);
        Assert.AreEqual(MetricState.Error, failedAfterMissing[0].State);
        Assert.AreEqual("upstream_unavailable", failedAfterMissing[0].ErrorCode);
        Assert.IsNull(failedAfterMissing[0].Value);
        Assert.IsTrue(failedAfterMissing.Skip(1).All(snapshot => snapshot.State == MetricState.Stale));

        harness.Time.Advance(OpenCodeGoQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        var recovered = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(recovered.All(snapshot => snapshot.State == MetricState.Fresh));
        Assert.IsTrue(recovered.All(snapshot => snapshot.ObservedAt == InitialUtc.AddMinutes(4)));
    }

    [TestMethod]
    public Task UnauthorizedClearsCacheSuppressesSameKeyAndResumesAfterKeyChange() =>
        AssertAuthRejectionRecoveryAsync(HttpStatusCode.Unauthorized, "auth_required");

    [TestMethod]
    public Task ForbiddenClearsCacheSuppressesSameKeyAndResumesAfterKeyChange() =>
        AssertAuthRejectionRecoveryAsync(HttpStatusCode.Forbidden, "auth_forbidden");

    private async Task AssertAuthRejectionRecoveryAsync(HttpStatusCode status, string errorCode)
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(OpenCodeGoQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(status);
        await harness.Provider.PollOnceAsync();

        var rejected = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(rejected.All(snapshot => snapshot.State == MetricState.Error && snapshot.Value is null));
        Assert.IsTrue(rejected.All(snapshot => snapshot.ErrorCode == errorCode));
        harness.Time.Advance(OpenCodeGoQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);

        harness.WriteAuth("synthetic-new-account-key");
        await harness.Provider.ObserveLocalStateAsync();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        var recovered = await PublishAndReadAllAsync(harness);
        Assert.AreEqual(3, harness.Handler.Requests.Length);
        Assert.IsTrue(recovered.All(snapshot => snapshot.State == MetricState.Fresh));
        Assert.AreEqual("synthetic-new-account-key", harness.Handler.Requests[^1].AuthorizationParameter);
    }

    [TestMethod]
    public async Task RetryAfterDeltaSurvivesCacheExpiryAndStillGatesRequests()
    {
        using var harness = new Harness(jitter: static _ => 0);
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(OpenCodeGoQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests,
            new RetryConditionHeaderValue(TimeSpan.FromSeconds(1200)));
        await harness.Provider.PollOnceAsync();

        var stale = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(stale.All(snapshot => snapshot.State == MetricState.Stale && snapshot.ObservedAt == InitialUtc));
        harness.Time.Advance(TimeSpan.FromMinutes(14));
        await harness.Provider.ObserveLocalStateAsync();
        var expired = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(expired.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.ErrorCode == "cache_expired"));

        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);
        harness.Time.Advance(TimeSpan.FromSeconds(359));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(3, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task RetryAfterDateIsHonoredAndMissingHeaderUsesDeterministicExponentialBackoff()
    {
        using (var dated = new Harness(jitter: static _ => 0))
        {
            dated.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests,
                new RetryConditionHeaderValue(InitialUtc.AddMinutes(10)));
            await dated.Provider.PollOnceAsync();
            dated.Time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
            dated.Handler.EnqueueJson(UsageJson);
            await dated.Provider.PollOnceAsync();
            Assert.AreEqual(1, dated.Handler.Requests.Length);
            dated.Time.Advance(TimeSpan.FromSeconds(1));
            await dated.Provider.PollOnceAsync();
            Assert.AreEqual(2, dated.Handler.Requests.Length);
        }

        using var backoff = new Harness(jitter: static _ => 0);
        backoff.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests);
        await backoff.Provider.PollOnceAsync();
        backoff.Time.Advance(TimeSpan.FromSeconds(59));
        backoff.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests);
        await backoff.Provider.PollOnceAsync();
        Assert.AreEqual(1, backoff.Handler.Requests.Length);
        backoff.Time.Advance(TimeSpan.FromSeconds(1));
        await backoff.Provider.PollOnceAsync();
        Assert.AreEqual(2, backoff.Handler.Requests.Length);
        backoff.Time.Advance(TimeSpan.FromSeconds(119));
        backoff.Handler.EnqueueJson(UsageJson);
        await backoff.Provider.PollOnceAsync();
        Assert.AreEqual(2, backoff.Handler.Requests.Length);
        backoff.Time.Advance(TimeSpan.FromSeconds(1));
        await backoff.Provider.PollOnceAsync();
        Assert.AreEqual(3, backoff.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task DeadlineBoundsRequestAndOfflineWithoutCacheHasNoValue()
    {
        using (var timeout = new Harness())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timeout.Handler.Enqueue(async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var poll = timeout.Provider.PollOnceAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            timeout.Time.Advance(OpenCodeGoQuotaProvider.RequestTimeout);
            await poll.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await PublishAndReadAllAsync(timeout);
            Assert.IsTrue(result.All(snapshot => snapshot.State == MetricState.Error && snapshot.ErrorCode == "timeout"));
            Assert.IsTrue(result.All(snapshot => snapshot.Value is null));
        }

        using var offline = new Harness();
        offline.Handler.Enqueue(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic offline")));
        await offline.Provider.PollOnceAsync();
        var unavailable = await PublishAndReadAllAsync(offline);
        Assert.IsTrue(unavailable.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.ErrorCode == "offline"));
    }

    [TestMethod]
    public async Task RedirectAndKnownOrChunkedOversizedBodiesFailClosed()
    {
        using (var redirect = new Harness())
        {
            redirect.Handler.EnqueueStatus(HttpStatusCode.Redirect);
            await redirect.Provider.PollOnceAsync();
            var result = await PublishAndReadAllAsync(redirect);
            Assert.IsTrue(result.All(snapshot => snapshot.State == MetricState.Error && snapshot.ErrorCode == "redirect_rejected"));
            Assert.AreEqual(1, redirect.Handler.Requests.Length);
        }

        using (var knownLength = new Harness())
        {
            knownLength.Handler.EnqueueContent(new ByteArrayContent(new byte[OpenCodeGoQuotaProvider.MaxResponseBytes + 1]));
            await knownLength.Provider.PollOnceAsync();
            var result = await PublishAndReadAllAsync(knownLength);
            Assert.IsTrue(result.All(snapshot => snapshot.State == MetricState.Error && snapshot.ErrorCode == "response_too_large"));
        }

        using var chunked = new Harness();
        chunked.Handler.EnqueueContent(new StreamContent(new OversizedStream(OpenCodeGoQuotaProvider.MaxResponseBytes + 1)));
        await chunked.Provider.PollOnceAsync();
        var chunkedResult = await PublishAndReadAllAsync(chunked);
        Assert.IsTrue(chunkedResult.All(snapshot => snapshot.State == MetricState.Error && snapshot.ErrorCode == "response_too_large"));
    }

    [TestMethod]
    public async Task WallClockJumpInvalidatesCacheWithoutRewritingItAsFresh()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        harness.Time.AdjustUtc(TimeSpan.FromHours(1));
        await harness.Provider.ObserveLocalStateAsync();

        var result = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(result.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
        Assert.IsTrue(result.All(snapshot => snapshot.ErrorCode == "clock_changed"));
    }

    [TestMethod]
    public async Task BackwardWallClockJumpInvalidatesCache()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(UsageJson);
        await harness.Provider.PollOnceAsync();
        harness.Time.AdjustUtc(TimeSpan.FromSeconds(-3));
        await harness.Provider.ObserveLocalStateAsync();

        var result = await PublishAndReadAllAsync(harness);
        Assert.IsTrue(result.All(snapshot => snapshot.State == MetricState.Unavailable &&
            snapshot.ErrorCode == "clock_changed" && snapshot.Value is null));
    }

    [TestMethod]
    public async Task RunPublishesStoppedAfterBoundedDrainOfCancellationIgnoringRequest()
    {
        using var harness = new Harness();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue((_, _) =>
        {
            requestStarted.TrySetResult();
            return releaseResponse.Task;
        });

        var reader = harness.Bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        var run = harness.Provider.RunAsync(cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        var stopped = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);

        releaseResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(UsageJson, Encoding.UTF8, "application/json")
        });
        await reader.DisposeAsync();
        await Task.Delay(50);
    }

    [TestMethod]
    public async Task RunAttemptsStoppedPublicationAndPreservesWorkerFault()
    {
        using var harness = new Harness();
        harness.Handler.Enqueue(_ => Task.FromException<HttpResponseMessage>(
            new InvalidOperationException("synthetic handler failure")));
        var reader = harness.Bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        Exception? failure = null;

        try
        {
            await harness.Provider.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        Assert.IsNotNull(failure);
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(2)));
        if (reader.Current.State != MetricState.Stopped)
        {
            next = reader.MoveNextAsync().AsTask();
            Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        await reader.DisposeAsync();
    }

    private static async Task<(IAsyncEnumerator<MetricSnapshot> Reader, Task Pending)> BeginBlockedPublicationAsync(
        Harness harness, string metricId)
    {
        var reader = harness.Bus.Subscribe(new MetricId(metricId)).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        await harness.Bus.PublishAsync(DummySnapshot(metricId));
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        await harness.Bus.PublishAsync(DummySnapshot(metricId));

        var pending = harness.Provider.PublishCurrentAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(pending.IsCompleted, "The full subscriber buffer should hold publication pending.");
        return (reader, pending);
    }

    private static async Task AssertCanceledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Assert.Fail("Expected the pending publication to be canceled.");
    }

    private static MetricSnapshot DummySnapshot(string metricId) => new(new MetricId(metricId),
        MetricValue.From(1d), InitialUtc, MetricState.Fresh, MetricConfidence.Measured);

    private static void WriteXdgAuthFile(string dataHome, string key) =>
        WriteAuthFile(Path.Combine(dataHome, "opencode"), key);

    private static void WriteAuthFile(string directory, string key, string type = "api")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "auth.json"), AuthJson(key, type));
    }

    private static string AuthJson(string key, string type = "api")
    {
        var document = new Dictionary<string, object>
        {
            ["opencode-go"] = new Dictionary<string, object> { ["type"] = type, ["key"] = key },
            ["synthetic-unrelated-provider"] = new { type = "api", key = "synthetic-other-provider-key" }
        };
        return JsonSerializer.Serialize(document);
    }

    private static string UsageJsonWithRollingReset(DateTimeOffset resetAt) => """
        {"usage":{
          "rolling":{"percent":100,"resetsAt":"$RESET$"},
          "weekly":{"percent":37.5,"resetsAt":"2026-10-08T08:00:00Z"},
          "monthly":{"percent":0,"resetsAt":"2026-11-01T08:00:00Z"}
        }}
        """.Replace("$RESET$", resetAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

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

        public Harness(bool writeAuth = true, Func<int, int>? jitter = null,
            OpenCodeGoAuthReader? authReader = null)
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            AuthReader = authReader ?? new OpenCodeGoAuthReader(DirectoryPath);
            if (writeAuth) WriteAuth("synthetic-opencode-go-key");
            Provider = new OpenCodeGoQuotaProvider(Registry, Bus, AuthReader, Handler, Time, jitter);
        }

        public void WriteAuth(string key, string type = "api")
        {
            WriteAuthFile(DirectoryPath, key, type);
        }

        public void Dispose()
        {
            Provider.Dispose();
            Bus.Dispose();
            try { System.IO.Directory.Delete(DirectoryPath, recursive: true); }
            catch (DirectoryNotFoundException)
            {
                return;
            }
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

        public void EnqueueStatus(HttpStatusCode status, RetryConditionHeaderValue? retryAfter = null) =>
            _responses.Enqueue((_, _) =>
            {
                var response = new HttpResponseMessage(status);
                if (retryAfter is not null) response.Headers.RetryAfter = retryAfter;
                return Task.FromResult(response);
            });

        public void EnqueueContent(HttpContent content) => _responses.Enqueue((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        public void Enqueue(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue((request, _) => response(request));

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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;
        private long _timestamp = -TimeSpan.TicksPerDay;

        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
                foreach (var timer in _timers)
                {
                    if (timer.IsDisposed || timer.DueTimestamp is not long due || due > _timestamp) continue;
                    callbacks.Add((timer.Callback, timer.State));
                    timer.AdvanceDueTimestamp(_timestamp);
                }
            }
            foreach (var (callback, state) in callbacks) callback(state);
        }

        public void AdjustUtc(TimeSpan adjustment)
        {
            lock (_gate) _utcNow += adjustment;
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (timer.IsDisposed) return false;
                if (dueTime != Timeout.InfiniteTimeSpan && dueTime < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(dueTime));
                if (period != Timeout.InfiniteTimeSpan && period < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(period));
                timer.Period = period == Timeout.InfiniteTimeSpan ? null : period.Ticks;
                timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : _timestamp + dueTime.Ticks;
                return true;
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                timer.IsDisposed = true;
                timer.DueTimestamp = null;
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long? DueTimestamp { get; set; }
            public long? Period { get; set; }
            public bool IsDisposed { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);

            public void AdvanceDueTimestamp(long now)
            {
                DueTimestamp = Period is long period && period > 0 ? now + period : null;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class OversizedStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(_remaining, count);
            Array.Clear(buffer, offset, read);
            _remaining -= read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(_remaining, buffer.Length);
            buffer.Span[..read].Clear();
            _remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
