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

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class ZCodeQuotaProviderTests
{
    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Ids =
    [
        "ai.zcode.coding_plan.level",
        "ai.zcode.coding_plan.five_hour.remaining_percent",
        "ai.zcode.coding_plan.five_hour.reset_at",
        "ai.zcode.coding_plan.five_hour.bucket_metadata",
        "ai.zcode.coding_plan.weekly.remaining_percent",
        "ai.zcode.coding_plan.weekly.reset_at",
        "ai.zcode.coding_plan.weekly.bucket_metadata",
        "ai.zcode.coding_plan.tools.remaining_percent",
        "ai.zcode.coding_plan.tools.reset_at",
        "ai.zcode.coding_plan.tools.bucket_metadata"
    ];

    [TestMethod]
    public async Task SelectedFamilyAcquiresCodingPlanAndPreservesWindowModelMetadata()
    {
        using var harness = new Harness();
        var settingsPath = Path.Combine(harness.VersionDirectory, "setting.json");
        var configPath = Path.Combine(harness.VersionDirectory, "config.json");
        var settingsBefore = File.ReadAllText(settingsPath);
        var configBefore = File.ReadAllText(configPath);
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        var snapshots = await ReadAllAsync(harness);
        var request = harness.Handler.Requests.Single();

        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(ZCodeQuotaProvider.ZaiUsageEndpoint, request.Uri);
        Assert.AreEqual("synthetic-zai-key", request.Authorization);
        Assert.IsNull(request.Organization);
        Assert.AreEqual(10, snapshots.Length);
        Assert.AreEqual("zcode-coding-plan-pro", snapshots[0].Value!.Value.AsString());
        Assert.AreEqual(75d, snapshots[1].Value!.Value.AsDouble(), 0.001);
        Assert.AreEqual(80d, snapshots[4].Value!.Value.AsDouble(), 0.001);
        Assert.AreEqual(50d, snapshots[7].Value!.Value.AsDouble(), 0.001);
        Assert.IsTrue(snapshots[3].Value!.Value.AsString().Contains("glm-5", StringComparison.Ordinal));
        Assert.IsTrue(snapshots[3].Value!.Value.AsString().Contains("5h", StringComparison.Ordinal));
        using var weeklyMetadata = JsonDocument.Parse(snapshots[6].Value!.Value.AsString());
        Assert.AreEqual(6, weeklyMetadata.RootElement.GetProperty("Unit").GetInt32());
        Assert.AreEqual(JsonValueKind.Number, weeklyMetadata.RootElement.GetProperty("SourceNumber").ValueKind);
        Assert.AreEqual(7, weeklyMetadata.RootElement.GetProperty("SourceNumber").GetInt32());
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Fresh));
        Assert.IsTrue(snapshots.All(snapshot => snapshot.ObservedAt == InitialUtc));
        Assert.AreEqual(settingsBefore, File.ReadAllText(settingsPath));
        Assert.AreEqual(configBefore, File.ReadAllText(configPath));
        Assert.IsFalse(File.Exists(Path.Combine(harness.VersionDirectory, "credentials.json")));
    }

    [TestMethod]
    public async Task ExplicitTeamSelectionAddsOnlyItsScopeAndUsesItsSelectedFamilyHost()
    {
        using var harness = new Harness();
        harness.WriteLegacyTeamSelection("bigmodel", "synthetic-product", "synthetic-org", "synthetic-project");
        harness.WriteConfig("synthetic-bigmodel-key", "bigmodel");
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        var snapshots = await ReadAllAsync(harness);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Fresh));
        var request = harness.Handler.Requests.Single();

        Assert.AreEqual(ZCodeQuotaProvider.BigModelUsageEndpoint + "?type=2", request.Uri.ToString());
        Assert.AreEqual("synthetic-bigmodel-key", request.Authorization);
        Assert.AreEqual("synthetic-org", request.Organization);
        Assert.AreEqual("synthetic-project", request.Project);
    }

    [TestMethod]
    public async Task ConfiguredAlternateEndpointDoesNotReceiveSelectedCredential()
    {
        using var harness = new Harness();
        harness.WriteConfig("synthetic-zai-key", "zai", "https://route.invalid/api");
        await harness.Provider.PollOnceAsync();

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.AreEqual("route_unsupported", harness.AuthReader.Read().ErrorCode);
    }


    [TestMethod]
    public async Task SingleCredentialCandidateDoesNotProveTheSelectedAccount()
    {
        using var harness = new Harness(writeConfig: false);
        harness.WriteCredential("zai", "synthetic-account-A", "synthetic-account-key");
        await harness.Provider.PollOnceAsync();
        var read = harness.AuthReader.Read();

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.AreEqual(ZCodeAuthStatus.Unsupported, read.Status);
        Assert.AreEqual("account_selection_unproven", read.ErrorCode);
    }

    [TestMethod]
    public async Task MissingCredentialPublishesUnavailableWithoutRequest()
    {
        using var harness = new Harness(writeConfig: false);
        await harness.Provider.PollOnceAsync();
        var snapshots = await ReadAllAsync(harness);

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
    }

    [TestMethod]
    public async Task RejectedCredentialIsSuppressedUntilConfiguredKeyChanges()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        await harness.Provider.PollOnceAsync();
        var rejected = await ReadAllAsync(harness);
        Assert.IsTrue(rejected.All(snapshot => snapshot.State == MetricState.Error && snapshot.ErrorCode == "auth_required"));

        harness.Time.Advance(ZCodeQuotaProvider.PollInterval);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(1, harness.Handler.Requests.Length);

        harness.WriteConfig("synthetic-zai-key-replaced", "zai");
        await harness.Provider.ObserveLocalStateAsync();
        harness.Time.Advance(ZCodeQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        var recovered = await ReadAllAsync(harness);

        Assert.AreEqual(2, harness.Handler.Requests.Length);
        Assert.IsTrue(recovered.All(snapshot => snapshot.State == MetricState.Fresh));
    }

    [TestMethod]
    public async Task MultipleAccountsForOneSelectedFamilyFailClosedWithoutTryingEitherKey()
    {
        using var harness = new Harness(writeConfig: false);
        harness.WriteCredential("zai", "synthetic-account-A", "synthetic-key-A");
        harness.AddCredential("zai", "synthetic-account-B", "synthetic-key-B");
        await harness.Provider.PollOnceAsync();

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.AreEqual(ZCodeAuthStatus.Unsupported, harness.AuthReader.Read().Status);
        Assert.AreEqual("account_selection_unproven", harness.AuthReader.Read().ErrorCode);
    }

    [TestMethod]
    public async Task ConflictingCurrentAndLegacySelectorsFailClosed()
    {
        using var harness = new Harness();
        harness.WriteConflictingSettings();
        await harness.Provider.PollOnceAsync();
        var read = harness.AuthReader.Read();

        Assert.AreEqual(0, harness.Handler.Requests.Length);
        Assert.AreEqual(ZCodeAuthStatus.Unsupported, read.Status);
        Assert.AreEqual("provider_selection_ambiguous", read.ErrorCode);
    }

    [TestMethod]
    public void AuthIdentityAndReadToStringRedactCredentialAndPrivateIds()
    {
        string[] privateValues =
        [
            "synthetic-secret-api-key",
            "synthetic-credential-private",
            "synthetic-product-private",
            "synthetic-organization-private",
            "synthetic-project-private",
            "synthetic-stable-private"
        ];
        var identity = new ZCodeQuotaAuthIdentity(privateValues[0], "zai", privateValues[1], privateValues[2],
            privateValues[3], privateValues[4], privateValues[5]);
        var identityText = identity.ToString();
        var readText = ZCodeAuthRead.Ready(identity).ToString();

        foreach (var value in privateValues)
        {
            Assert.IsFalse(identityText.Contains(value, StringComparison.Ordinal));
            Assert.IsFalse(readText.Contains(value, StringComparison.Ordinal));
        }
        Assert.IsTrue(identityText.Contains("<redacted>", StringComparison.Ordinal));
        Assert.IsTrue(readText.Contains("<redacted>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingSelectedFamilyAndMalformedPayloadDoNotInventQuota()
    {
        using var harness = new Harness();
        harness.WriteSettings("", "individual-coding-plan");
        Assert.AreEqual(ZCodeAuthStatus.Unsupported, harness.AuthReader.Read().Status);

        var malformed = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes("{\"data\":").AsMemory());
        Assert.IsTrue(malformed.Points.All(point => point.State == MetricState.Error));
        var missingLimits = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes("{\"code\":0,\"data\":{\"level\":\"pro\"}}").AsMemory());
        Assert.AreEqual(MetricState.Fresh, missingLimits.Points[ZCodeQuotaParser.PlanId].State);
        Assert.IsTrue(missingLimits.Points.Skip(1).All(point => point.State == MetricState.Error));
    }

    [TestMethod]
    public void MissingPercentageIsUnavailableAndDuplicateWindowIsAnError()
    {
        var missingPercentage = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes(
            "{\"code\":0,\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"unit\":3,\"number\":5,\"nextResetTime\":1790000000}]}}").AsMemory());
        Assert.AreEqual(MetricState.Unavailable, missingPercentage.Points[ZCodeQuotaParser.FiveHourRemaining].State);
        Assert.AreEqual("percentage_missing", missingPercentage.Points[ZCodeQuotaParser.FiveHourRemaining].ErrorCode);
        Assert.AreEqual(MetricState.Fresh, missingPercentage.Points[ZCodeQuotaParser.FiveHourMetadata].State);

        var duplicate = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes(
            "{\"code\":0,\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"unit\":3,\"number\":5,\"percentage\":50},{\"type\":\"TOKENS_LIMIT\",\"unit\":3,\"number\":5,\"percentage\":60}]}}").AsMemory());
        Assert.AreEqual(MetricState.Error, duplicate.Points[ZCodeQuotaParser.FiveHourRemaining].State);
        Assert.AreEqual("bucket_ambiguous", duplicate.Points[ZCodeQuotaParser.FiveHourRemaining].ErrorCode);

        var duplicateWeeklyScopes = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes(
            "{\"code\":0,\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"unit\":6,\"number\":7,\"percentage\":50},{\"type\":\"TOKENS_LIMIT\",\"unit\":6,\"number\":8,\"percentage\":60}]}}").AsMemory());
        Assert.AreEqual(MetricState.Error, duplicateWeeklyScopes.Points[ZCodeQuotaParser.WeeklyRemaining].State);
        Assert.AreEqual("bucket_ambiguous", duplicateWeeklyScopes.Points[ZCodeQuotaParser.WeeklyRemaining].ErrorCode);
    }

    [TestMethod]
    public void NumericResetTimestampsRemainUnavailableWithoutVerifiedWireUnit()
    {
        foreach (var numericReset in new[] { "1790000000", "1790000000000" })
        {
            var parsed = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes(
                $"{{\"code\":0,\"data\":{{\"limits\":[{{\"type\":\"TOKENS_LIMIT\",\"unit\":3,\"number\":5,\"percentage\":50,\"nextResetTime\":{numericReset}}}]}}}}").AsMemory());
            var reset = parsed.Points[ZCodeQuotaParser.FiveHourReset];

            Assert.AreEqual(MetricState.Unavailable, reset.State);
            Assert.IsNull(reset.Value);
            Assert.AreEqual("reset_unit_unverified", reset.ErrorCode);
        }
    }

    [TestMethod]
    public void SourceNumberMetadataPreservesOnlyBoundedIntegralScalars()
    {
        foreach (var sourceNumber in new[]
        {
            "\"synthetic-private-number\"",
            "{\"private\":\"synthetic-private-marker\"}",
            "1.5",
            "9223372036854775808",
            "-1"
        })
        {
            var body = Encoding.UTF8.GetBytes(
                $"{{\"code\":0,\"data\":{{\"limits\":[{{\"type\":\"TOKENS_LIMIT\",\"unit\":6,\"number\":{sourceNumber},\"percentage\":25}}]}}}}");
            var parsed = ZCodeQuotaParser.Parse(body.AsMemory());
            var metadata = parsed.Points[ZCodeQuotaParser.WeeklyMetadata];

            Assert.AreEqual(MetricState.Error, metadata.State);
            Assert.IsNull(metadata.Value);
            Assert.AreEqual("invalid_bucket_number", metadata.ErrorCode);
        }

        var zero = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes(
            "{\"code\":0,\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"unit\":6,\"number\":0}]}}").AsMemory());
        using var zeroMetadata = JsonDocument.Parse(zero.Points[ZCodeQuotaParser.WeeklyMetadata].Value!.Value.AsString());
        Assert.AreEqual(0, zeroMetadata.RootElement.GetProperty("SourceNumber").GetInt64());

        var missing = ZCodeQuotaParser.Parse(Encoding.UTF8.GetBytes(
            "{\"code\":0,\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"unit\":6}]}}").AsMemory());
        using var missingMetadata = JsonDocument.Parse(missing.Points[ZCodeQuotaParser.WeeklyMetadata].Value!.Value.AsString());
        Assert.AreEqual(JsonValueKind.Null, missingMetadata.RootElement.GetProperty("SourceNumber").ValueKind);
    }

    [TestMethod]
    public async Task RequestDeadlineCancelsSlowAcquisitionAndPublishesTimeout()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(harness.Fixture);
        });
        var poll = harness.Provider.PollOnceAsync();
        await entered.Task;
        harness.Time.Advance(ZCodeQuotaProvider.RequestTimeout + TimeSpan.FromSeconds(1));
        await poll;
        var snapshots = await ReadAllAsync(harness);

        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error && snapshot.ErrorCode == "timeout"));
    }

    [TestMethod]
    public async Task OfflineAcquisitionPublishesUnavailableWithoutInventingValues()
    {
        using var harness = new Harness();
        harness.Handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic offline")));
        await harness.Provider.PollOnceAsync();
        var snapshots = await ReadAllAsync(harness);

        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable && snapshot.Value is null));
    }

    [TestMethod]
    public async Task OversizedResponseIsRejectedBeforeParsing()
    {
        using var harness = new Harness();
        harness.Handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ZCodeQuotaProvider.MaxResponseBytes + 1])
        }));
        await harness.Provider.PollOnceAsync();
        var snapshots = await ReadAllAsync(harness);

        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error && snapshot.ErrorCode == "response_too_large"));
    }

    [TestMethod]
    public async Task OwnerChangeCancelsInFlightAcquisitionAndDiscardsLateResponse()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return JsonResponse(harness.Fixture);
        });
        var poll = harness.Provider.PollOnceAsync();
        await entered.Task;
        harness.WriteConfig("synthetic-zai-key-B", "zai");
        await harness.Provider.ObserveLocalStateAsync();
        release.TrySetResult();
        await poll;
        var snapshots = await ReadAllAsync(harness);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Value is null && snapshot.State == MetricState.Unavailable));
    }

    [TestMethod]
    public async Task MonotonicCacheBecomesStaleThenExpiresAtResetWithoutChangingOriginalObservedAt()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(121));
        await harness.Provider.ObserveLocalStateAsync();
        var stale = await ReadAllAsync(harness);
        Assert.AreEqual(MetricState.Stale, stale[1].State);
        Assert.AreEqual(InitialUtc, stale[1].ObservedAt);

        harness.Time.Advance(TimeSpan.FromSeconds(3600));
        await harness.Provider.ObserveLocalStateAsync();
        var expired = await ReadAllAsync(harness);
        Assert.IsNull(expired[1].Value);
        Assert.AreEqual(MetricState.Unavailable, expired[1].State);
        Assert.AreEqual("reset_elapsed", expired[1].ErrorCode);
    }
    [TestMethod]
    public async Task OwnerChangeCancelsPendingBusPublicationAndNextSnapshotIsValueless()
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
        await Task.Delay(25);
        Assert.IsFalse(blockedPublish.IsCompleted);

        harness.WriteConfig("synthetic-zai-key-B", "zai");
        await harness.Provider.ObserveLocalStateAsync();
        await blockedPublish.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        var next = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await next);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
    }

    [TestMethod]
    public async Task ResetCrossingCancelsPendingValuePublication()
    {
        using var harness = new Harness();
        await using var reader = harness.Bus.Subscribe(new MetricId(Ids[1])).GetAsyncEnumerator();
        var initial = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await initial);

        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        await harness.Provider.PublishCurrentAsync();
        var pendingPublication = harness.Provider.PublishCurrentAsync().AsTask();
        await Task.Delay(25);
        Assert.IsFalse(pendingPublication.IsCompleted);

        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Provider.ObserveLocalStateAsync();
        await pendingPublication.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        var next = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await next);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task LongRetryAfterDoesNotExtendCacheLifetimeOrTriggerEarlyRequest()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(harness.Fixture);
        await harness.Provider.PollOnceAsync();
        await ReadAllAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(60));
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        await harness.Provider.ObserveLocalStateAsync();
        var expired = await ReadAllAsync(harness);
        Assert.AreEqual(MetricState.Unavailable, expired[1].State);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(2, harness.Handler.Requests.Length);
    }


    [TestMethod]
    public async Task RunPublishesStoppedMetricsAfterCancellation()
    {
        using var harness = new Harness();
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        try
        {
            var initial = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Handler.Enqueue(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return JsonResponse(harness.Fixture);
            });
            using var stop = new CancellationTokenSource();
            var run = harness.Provider.RunAsync(stop.Token);
            await Task.WhenAll(initial);
            await entered.Task;
            var stopped = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
            Assert.IsTrue((await Task.WhenAll(stopped)).All(result => result));
            Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Stopped && reader.Current.Value is null));
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RunPublishesStoppedMetricsAfterWorkerDrainFaults()
    {
        using var harness = new Harness();
        harness.Time.ThrowOnTimerCreation = true;
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        try
        {
            var initial = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            var run = harness.Provider.RunAsync(CancellationToken.None);
            await Task.WhenAll(initial);
            var stopped = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();

            Exception? failure = null;
            try { await run; }
            catch (Exception exception) { failure = exception; }

            Assert.IsNotNull(failure);
            var propagated = failure! is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.Any(exception =>
                    exception is InvalidOperationException && exception.Message == "synthetic timer failure")
                : failure is InvalidOperationException && failure.Message == "synthetic timer failure";
            Assert.IsTrue(propagated);
            Assert.IsTrue((await Task.WhenAll(stopped)).All(result => result));
            Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Stopped && reader.Current.Value is null));
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    private static async Task<MetricSnapshot[]> ReadAllAsync(Harness harness)
    {
        var readers = Ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        try
        {
            var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            await harness.Provider.PublishCurrentAsync();
            await Task.WhenAll(pending);
            return readers.Select(reader => reader.Current).ToArray();
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Harness : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "cerith-zcode-test-" + Guid.NewGuid().ToString("N"));
        public string VersionDirectory => Path.Combine(Home, ".zcode", "v2");
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public TestTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public ZCodeQuotaAuthReader AuthReader { get; }
        public ZCodeQuotaProvider Provider { get; }
        public string Fixture => BuildFixture();

        public Harness(bool writeConfig = true)
        {
            Directory.CreateDirectory(VersionDirectory);
            WriteSettings("zai", "individual-coding-plan");
            if (writeConfig) WriteConfig("synthetic-zai-key", "zai");
            AuthReader = new ZCodeQuotaAuthReader(Home);
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Provider = new ZCodeQuotaProvider(Registry, Bus, AuthReader, Client, Time);
        }

        public void WriteSettings(string family, string kind)
        {
            var selections = string.IsNullOrEmpty(family)
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { [family] = new { kind } };
            File.WriteAllText(Path.Combine(VersionDirectory, "setting.json"), JsonSerializer.Serialize(new
            {
                providerFamilyDomain = family,
                providerFamilyConnectionSelections = selections
            }));
        }

        public void WriteLegacyTeamSelection(string family, string product, string organization, string project)
        {
            var selected = $"team-plan:builtin:{family}-coding-plan:{product}:{organization}:{project}";
            File.WriteAllText(Path.Combine(VersionDirectory, "setting.json"), JsonSerializer.Serialize(new
            {
                providerFamilyDomain = family,
                modelProviderFamilySelectedKeys = new Dictionary<string, string> { [family] = selected }
            }));
        }

        public void WriteConflictingSettings()
        {
            File.WriteAllText(Path.Combine(VersionDirectory, "setting.json"), JsonSerializer.Serialize(new
            {
                providerFamilyDomain = "zai",
                providerFamilyConnectionSelections = new { zai = new { kind = "individual-coding-plan" } },
                modelProviderFamilySelectedKeys = new Dictionary<string, string>
                {
                    ["zai"] = "team-plan:builtin:zai-coding-plan:synthetic-product:synthetic-org:synthetic-project"
                }
            }));
        }

        public void WriteConfig(string apiKey, string family, string? baseUrl = null)
        {
            var selectedProvider = $"builtin:{family}-coding-plan";
            var otherFamily = family == "zai" ? "bigmodel" : "zai";
            var selectedOptions = new Dictionary<string, object> { ["apiKey"] = apiKey };
            if (baseUrl is not null) selectedOptions["baseURL"] = baseUrl;
            var providers = new Dictionary<string, object>
            {
                [selectedProvider] = new { enabled = true, options = selectedOptions },
                [$"builtin:{otherFamily}-coding-plan"] = new { enabled = true, options = new { apiKey = "synthetic-other-family-key" } }
            };
            File.WriteAllText(Path.Combine(VersionDirectory, "config.json"), JsonSerializer.Serialize(new { provider = providers }));
        }

        public void WriteCredential(string family, string account, string apiKey)
        {
            var name = family == "zai"
                ? $"account-provider:coding-plan:account:zai-individual-coding-plan:account:{account}:api-key"
                : $"account-provider:coding-plan:account:bigmodel-individual-coding-plan:account:{account}:api-key";
            AddCredential(name, apiKey);
        }

        public void AddCredential(string family, string account, string apiKey)
        {
            var name = family == "zai"
                ? $"account-provider:coding-plan:account:zai-individual-coding-plan:account:{account}:api-key"
                : $"account-provider:coding-plan:account:bigmodel-individual-coding-plan:account:{account}:api-key";
            AddCredential(name, apiKey);
        }

        private void AddCredential(string name, string value)
        {
            var path = Path.Combine(VersionDirectory, "credentials.json");
            var credentials = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new Dictionary<string, string>();
            credentials[name] = value;
            File.WriteAllText(path, JsonSerializer.Serialize(credentials));
        }

        private string BuildFixture()
        {
            var reset = InitialUtc.AddHours(1).ToString("O");
            return JsonSerializer.Serialize(new { code = 0, success = true, data = new { level = "zcode-coding-plan-pro", limits = new object[] { new { type = "TOKENS_LIMIT", unit = 3, number = 5, usage = 50, percentage = 25, remaining = 150, nextResetTime = reset, usageDetails = new[] { new { displayName = "Coding", modelCode = "glm-5" } } }, new { type = "TOKENS_LIMIT", unit = 6, number = 7, usage = 20, percentage = 20, nextResetTime = reset, usageDetails = new[] { new { displayName = "Weekly", modelCode = "glm-5" } } }, new { type = "TIME_LIMIT", unit = 5, number = 1, usage = 100, percentage = 50, nextResetTime = reset, usageDetails = new[] { new { displayName = "Tools", modelCode = "mcp-tool" } } } } } });
        }

        public void Dispose()
        {
            Provider.Dispose();
            Client.Dispose();
            Bus.Dispose();
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
        private readonly ConcurrentQueue<RequestObservation> _requests = new();
        public RequestObservation[] Requests => _requests.ToArray();

        public void EnqueueJson(string json) => Enqueue((_, _) => Task.FromResult(JsonResponse(json)));
        public void EnqueueStatus(HttpStatusCode status, TimeSpan? retryAfter = null) => Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter.HasValue) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
            return Task.FromResult(response);
        });
        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("Authorization", out var authorizationValues);
            request.Headers.TryGetValues("bigmodel-organization", out var organizationValues);
            request.Headers.TryGetValues("bigmodel-project", out var projectValues);
            _requests.Enqueue(new RequestObservation(request.Method, request.RequestUri!, authorizationValues?.SingleOrDefault(),
                organizationValues?.SingleOrDefault(), projectValues?.SingleOrDefault()));
            var response = _responses.TryDequeue(out var queued)
                ? await queued(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, string? Authorization,
        string? Organization, string? Project);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly ConcurrentBag<ManualTimer> _timers = new();
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public bool ThrowOnTimerCreation { get; set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (ThrowOnTimerCreation) throw new InvalidOperationException("synthetic timer failure");
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
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
            foreach (var timer in timers) timer.FireDue(timestamp);
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

            public void FireDue(long timestamp)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed || _due > timestamp) return;
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
