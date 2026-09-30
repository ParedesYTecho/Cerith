#nullable enable

using System;
using System.Collections.Concurrent;
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
using Cerith.TelemetryService.Providers.AI.GeminiCli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.GeminiCli;

[TestClass]
public sealed class GeminiCliQuotaProviderTests
{
    private const string ModelId = "gemini-2.5-pro";
    private const string RemainingId = "ai.gemini_cli.code_assist.model.gemini-2.5-pro.remaining";
    private const string ResetId = "ai.gemini_cli.code_assist.model.gemini-2.5-pro.reset";
    private const string ProjectA = "synthetic-project-a";
    private const string ProjectB = "synthetic-project-b";
    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string LoadResponse = """
        {"currentTier":{"id":"standard-tier"},"cloudaicompanionProject":"synthetic-project-a"}
        """;
    private const string QuotaResponse = """
        {"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":0.625,"remainingAmount":"250","tokenType":"REQUESTS","resetTime":"2026-09-30T12:00:00Z"}]}
        """;

    [TestMethod]
    public void ParserMapsOnlySourceFractionsAndResetInstants()
    {
        var reading = Parse("""
            {"buckets":[
              {"modelId":"gemini-2.5-pro","remainingFraction":0.625,"remainingAmount":"250","tokenType":"REQUESTS","resetTime":"2026-09-30T12:00:00Z"},
              {"modelId":"gemini-2.5-flash","remainingFraction":1.0,"remainingAmount":"1000"},
              {"modelId":"gemini-2.5-flash-lite","resetTime":"2026-09-30T13:00:00+01:00"}
            ]}
            """);

        Assert.IsTrue(reading.IsValid);
        Assert.AreEqual(62.5d, reading.Models["gemini-2.5-pro"].Remaining.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-30T12:00:00Z"),
            reading.Models["gemini-2.5-pro"].Reset.Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(100d, reading.Models["gemini-2.5-flash"].Remaining.Value!.Value.AsDouble());
        Assert.AreEqual(MetricState.Unavailable, reading.Models["gemini-2.5-flash-lite"].Remaining.State);
        Assert.AreEqual(MetricState.Fresh, reading.Models["gemini-2.5-flash-lite"].Reset.State);
        Assert.AreEqual(3, reading.Models.Count);
    }

    [TestMethod]
    public void ParserRejectsRepeatedModelIdWithDifferentTokenTypesAsAmbiguous()
    {
        var ambiguous = Parse("""{"buckets":[{"modelId":"gemini-2.5-pro","tokenType":"synthetic-dimension-a","remainingFraction":0.8},{"modelId":"gemini-2.5-pro","tokenType":"synthetic-dimension-b","remainingFraction":0.2}]}""");
        Assert.IsFalse(ambiguous.IsValid);
        Assert.AreEqual("ambiguous_bucket_identity", ambiguous.ErrorCode);
    }
    [TestMethod]
    public void ParserKeepsMissingInvalidDuplicateAndElapsedValuesDistinct()
    {
        Assert.AreEqual("schema_invalid", Parse("{").ErrorCode);
        Assert.AreEqual("schema_invalid", Parse("{\"buckets\":{}}").ErrorCode);
        var missing = Parse("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\"}]}");
        Assert.AreEqual(MetricState.Unavailable, missing.Models[ModelId].Remaining.State);
        Assert.IsNull(missing.Models[ModelId].Remaining.Value);
        var amountOnly = Parse("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingAmount\":\"250\",\"tokenType\":\"REQUESTS\"}]}");
        Assert.AreEqual(MetricState.Unavailable, amountOnly.Models[ModelId].Remaining.State);
        Assert.IsNull(amountOnly.Models[ModelId].Remaining.Value);

        var invalid = Parse("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":1.01}]}");
        Assert.AreEqual(MetricState.Error, invalid.Models[ModelId].Remaining.State);
        Assert.AreEqual("invalid_quota", invalid.Models[ModelId].Remaining.ErrorCode);

        var elapsed = Parse("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.5,\"resetTime\":\"2026-09-29T11:59:59Z\"}]}");
        Assert.AreEqual(MetricState.Unavailable, elapsed.Models[ModelId].Remaining.State);
        Assert.AreEqual("reset_elapsed", elapsed.Models[ModelId].Remaining.ErrorCode);
        Assert.AreEqual(MetricState.Unavailable, elapsed.Models[ModelId].Reset.State);

        var duplicate = Parse("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.8},{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.2}]}");
        Assert.IsFalse(duplicate.IsValid);
        Assert.AreEqual("ambiguous_bucket_identity", duplicate.ErrorCode);

        var noBuckets = Parse("{\"buckets\":[]}");
        Assert.IsTrue(noBuckets.IsValid);
        Assert.AreEqual(0, noBuckets.Models.Count);
    }

    [TestMethod]
    public void AuthDirectoryAndSelectedProjectFollowOfficialResolution()
    {
        var cliHome = Path.Combine(Path.GetTempPath(), "synthetic-gemini-cli-home");
        var userHome = Path.Combine(Path.GetTempPath(), "synthetic-user-home");
        var tempPath = Path.Combine(Path.GetTempPath(), "synthetic-fallback-home");
        Assert.AreEqual(Path.Combine(cliHome, ".gemini"),
            GeminiCliQuotaAuthReader.ResolveGlobalGeminiDirectory(cliHome, userHome, tempPath));
        Assert.AreEqual(Path.Combine(userHome, ".gemini"),
            GeminiCliQuotaAuthReader.ResolveGlobalGeminiDirectory(null, userHome, tempPath));
        Assert.AreEqual(Path.Combine(tempPath, ".gemini"),
            GeminiCliQuotaAuthReader.ResolveGlobalGeminiDirectory(null, null, tempPath));

        Assert.AreEqual(ProjectA, GeminiCliQuotaProvider.ResolveSelectedProjectId(ProjectA, ProjectB));
        Assert.AreEqual(ProjectB, GeminiCliQuotaProvider.ResolveSelectedProjectId("", ProjectB));
        Assert.IsNull(GeminiCliQuotaProvider.ResolveSelectedProjectId(null, null));
        Assert.Throws<InvalidDataException>(() =>
            GeminiCliQuotaAuthReader.ResolveGlobalGeminiDirectory(" ", userHome, tempPath));
        Assert.Throws<InvalidDataException>(() =>
            GeminiCliQuotaAuthReader.ResolveGlobalGeminiDirectory("relative-home", userHome, tempPath));
    }

    [TestMethod]
    public void AuthReaderRequiresExplicitSelectedTypeDespiteEnvironmentDefault()
    {
        using var harness = new Harness();
        File.WriteAllText(Path.Combine(harness.GeminiDirectory, "settings.json"), "{}");
        var reader = new GeminiCliQuotaAuthReader(harness.Home, harness.Time,
            name => name == "GEMINI_DEFAULT_AUTH_TYPE" ? "oauth-personal" : null);

        var result = reader.Read();

        Assert.AreEqual(GeminiCliAuthStatus.Unsupported, result.Status);
        Assert.AreEqual("auth_mode_unselected", result.ErrorCode);
    }

    [TestMethod]
    public void AuthReaderRejectsApplicationDefaultCredentialsAlongsideCachedOAuth()
    {
        using var harness = new Harness();
        var reader = new GeminiCliQuotaAuthReader(harness.Home, harness.Time,
            name => name == "GOOGLE_APPLICATION_CREDENTIALS" ? "synthetic-adc.json" : null);

        var result = reader.Read();

        Assert.AreEqual(GeminiCliAuthStatus.Unsupported, result.Status);
        Assert.AreEqual("auth_mode_unsupported", result.ErrorCode);
    }
    [TestMethod]
    public void AuthReaderAcceptsOnlyFreshLocalOAuthAndNeverWritesCredentials()
    {
        using var harness = new Harness();
        var path = Path.Combine(harness.GeminiDirectory, "oauth_creds.json");
        var before = File.ReadAllBytes(path);
        var reader = new GeminiCliQuotaAuthReader(harness.Home, harness.Time, _ => null);
        var ready = reader.Read();
        var after = File.ReadAllBytes(path);

        Assert.AreEqual(GeminiCliAuthStatus.Ready, ready.Status);
        Assert.AreEqual(InitialUtc.AddHours(1), ready.Identity!.ExpiresAt);
        CollectionAssert.AreEqual(before, after);

        harness.WriteAuth(expiry: InitialUtc.AddSeconds(30));
        var expiring = reader.Read();
        Assert.AreEqual(GeminiCliAuthStatus.Unavailable, expiring.Status);
        Assert.AreEqual("access_token_expired", expiring.ErrorCode);

        harness.WriteAuth(selectedType: "api-key");
        Assert.AreEqual(GeminiCliAuthStatus.Unsupported, reader.Read().Status);

        File.Delete(path);
        harness.WriteSettings("oauth-personal");
        Assert.AreEqual(GeminiCliAuthStatus.Unavailable, reader.Read().Status);
    }

    [TestMethod]
    public async Task AcquiresQuotaOnlyAfterExistingProjectAndPublishesExactModelMetrics()
    {
        using var harness = new Harness();
        var authBytes = File.ReadAllBytes(Path.Combine(harness.GeminiDirectory, "oauth_creds.json"));
        harness.Handler.EnqueueJson(LoadResponse);
        harness.Handler.EnqueueJson(QuotaResponse);
        await harness.Provider.PollOnceAsync();

        var snapshots = await ReadAllAsync(harness);
        var remaining = snapshots.Single(snapshot => snapshot.MetricId.Value == RemainingId);
        var reset = snapshots.Single(snapshot => snapshot.MetricId.Value == ResetId);
        var requests = harness.Handler.Requests;
        var afterAuthBytes = File.ReadAllBytes(Path.Combine(harness.GeminiDirectory, "oauth_creds.json"));

        Assert.AreEqual(2, requests.Length);
        Assert.AreEqual(HttpMethod.Post, requests[0].Method);
        Assert.AreEqual(GeminiCliQuotaProvider.LoadCodeAssistEndpoint, requests[0].Uri);
        Assert.AreEqual(GeminiCliQuotaProvider.RetrieveUserQuotaEndpoint, requests[1].Uri);
        Assert.IsTrue(requests.All(request => request.AuthorizationScheme == "Bearer"));
        Assert.IsTrue(requests.All(request => request.AccessToken == "synthetic-access-A"));
        using (var metadata = JsonDocument.Parse(requests[0].Body!))
        {
            Assert.AreEqual(ProjectA, metadata.RootElement.GetProperty("cloudaicompanionProject").GetString());
            var values = metadata.RootElement.GetProperty("metadata");
            Assert.AreEqual("IDE_UNSPECIFIED", values.GetProperty("ideType").GetString());
            Assert.AreEqual("PLATFORM_UNSPECIFIED", values.GetProperty("platform").GetString());
            Assert.AreEqual("GEMINI", values.GetProperty("pluginType").GetString());
            Assert.AreEqual(ProjectA, values.GetProperty("duetProject").GetString());
        }
        using (var project = JsonDocument.Parse(requests[1].Body!))
            Assert.AreEqual(ProjectA, project.RootElement.GetProperty("project").GetString());
        Assert.AreEqual(MetricState.Fresh, remaining.State);
        Assert.AreEqual(62.5d, remaining.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(MetricConfidence.Measured, remaining.Confidence);
        Assert.AreEqual("%", harness.Registry.GetSnapshot().Single(item => item.Id.Value == RemainingId).Unit);
        Assert.AreEqual(MetricValueKind.DateTimeOffset, reset.Value!.Value.Kind);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-30T12:00:00Z"), reset.Value.Value.AsDateTimeOffset());
        Assert.AreEqual(GeminiCliQuotaProvider.SnapshotSource, remaining.Source);
        CollectionAssert.AreEqual(authBytes, afterAuthBytes);
    }

    [TestMethod]
    public async Task MissingCodeAssistProjectDoesNotCallQuotaOrOnboarding()
    {
        using var harness = new Harness();
        harness.SelectedProject = null;
        harness.Handler.EnqueueJson("{\"currentTier\":{\"id\":\"standard-tier\"}}");
        await harness.Provider.PollOnceAsync();

        Assert.AreEqual(1, harness.Handler.Requests.Length);
        Assert.AreEqual(GeminiCliQuotaProvider.LoadCodeAssistEndpoint, harness.Handler.Requests[0].Uri);
        Assert.IsFalse(harness.Handler.Requests.Any(request => request.Uri.AbsolutePath.Contains("onboardUser", StringComparison.Ordinal)));
        Assert.IsFalse(harness.Handler.Requests.Any(request => request.Uri == GeminiCliQuotaProvider.RetrieveUserQuotaEndpoint));
    }

    [TestMethod]
    public async Task SelectedProjectFallbackRequiresExistingTierAndNeverChangesProjectSilently()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson("{\"currentTier\":{\"id\":\"standard-tier\"}}");
        harness.Handler.EnqueueJson(QuotaResponse);
        await harness.Provider.PollOnceAsync();
        using (var body = JsonDocument.Parse(harness.Handler.Requests[^1].Body!))
            Assert.AreEqual(ProjectA, body.RootElement.GetProperty("project").GetString());

        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.SelectedProject = ProjectB;
        harness.Handler.EnqueueJson(LoadResponse);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(3, harness.Handler.Requests.Length);
        var mismatch = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Error, mismatch.State);
        Assert.AreEqual("project_mismatch", mismatch.ErrorCode);
        Assert.IsNull(mismatch.Value);

        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.SelectedProject = "invalid/project";
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(3, harness.Handler.Requests.Length);
        Assert.AreEqual("project_invalid", (await ReadOneAsync(harness, RemainingId)).ErrorCode);

        Assert.AreEqual("project_unavailable", GeminiCliQuotaParser.ParseProject(
            Encoding.UTF8.GetBytes("{\"cloudaicompanionProject\":\"synthetic-project-a\"}"), ProjectA).ErrorCode);
    }

    [TestMethod]
    public async Task ExpiredRenewableAccessTokenWaitsForOfficialOwnerAndRecoversByRereading()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        harness.WriteAuth(expiry: InitialUtc.AddSeconds(30));
        var expired = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.AreEqual("access_token_expired", expired.ErrorCode);
        Assert.IsNull(expired.Value);
        Assert.AreEqual(2, harness.Handler.Requests.Length);

        harness.WriteAuth(token: "synthetic-official-owner-renewed-token");
        var authPath = Path.Combine(harness.GeminiDirectory, "oauth_creds.json");
        var before = File.ReadAllBytes(authPath);
        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        await harness.LoadQuotaAsync();
        Assert.AreEqual(MetricState.Fresh, (await ReadOneAsync(harness, RemainingId)).State);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(authPath));
    }

    [TestMethod]
    public async Task AbsentAuthMakesNoRequestAndUnsupportedOfficialBackendsFailClosed()
    {
        using var harness = new Harness();
        var encrypted = new GeminiCliQuotaAuthReader(harness.Home, harness.Time,
            name => name == "GEMINI_FORCE_ENCRYPTED_FILE_STORAGE" ? "true" : null);
        Assert.AreEqual("auth_storage_unsupported", encrypted.Read().ErrorCode);
        var gcaOverride = new GeminiCliQuotaAuthReader(harness.Home, harness.Time,
            name => name is "GOOGLE_GENAI_USE_GCA" or "GOOGLE_CLOUD_ACCESS_TOKEN" ? "synthetic-override" : null);
        Assert.AreEqual(GeminiCliAuthStatus.Unsupported, gcaOverride.Read().Status);

        File.Delete(Path.Combine(harness.GeminiDirectory, "oauth_creds.json"));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(0, harness.Handler.Requests.Length);
        var adc = new GeminiCliQuotaAuthReader(harness.Home, harness.Time,
            name => name == "GOOGLE_APPLICATION_CREDENTIALS" ? "synthetic-adc-path" : null);
        Assert.AreEqual(GeminiCliAuthStatus.Unsupported, adc.Read().Status);
    }

    [TestMethod]
    public async Task ResponseBodyBoundRejectsBothAdvertisedAndStreamingOverflow()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        foreach (var advertisedLength in new[] { GeminiCliQuotaProvider.MaxResponseBytes + 1, 1 })
        {
            harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
            harness.Handler.EnqueueJson(LoadResponse);
            harness.Handler.Enqueue((_, _) =>
            {
                var response = JsonResponse(new string(' ', GeminiCliQuotaProvider.MaxResponseBytes + 1));
                response.Content.Headers.ContentLength = advertisedLength;
                return Task.FromResult(response);
            });
            await harness.Provider.PollOnceAsync();
            var snapshot = await ReadOneAsync(harness, RemainingId);
            Assert.AreEqual(MetricState.Error, snapshot.State);
            Assert.AreEqual("response_too_large", snapshot.ErrorCode);
            Assert.IsNull(snapshot.Value);
        }
    }

    [TestMethod]
    public async Task OfflineFailureStalesCacheAndSuccessfulMissingFractionEvictsIt()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        var original = await ReadOneAsync(harness, RemainingId);

        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueException(new HttpRequestException());
        await harness.Provider.PollOnceAsync();
        var stale = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual("offline", stale.ErrorCode);
        Assert.AreEqual(original.ObservedAt, stale.ObservedAt);
        Assert.AreEqual(original.Value, stale.Value);

        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(LoadResponse);
        harness.Handler.EnqueueJson("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"resetTime\":\"2026-09-30T12:00:00Z\"}]}");
        await harness.Provider.PollOnceAsync();
        var missing = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Unavailable, missing.State);
        Assert.IsNull(missing.Value);
        Assert.IsNull(missing.ErrorCode);
    }

    [TestMethod]
    public async Task RequestDeadlineAndCacheAgeAreBounded()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(LoadResponse);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        var pending = harness.Provider.PollOnceAsync();
        await entered.Task;
        harness.Time.Advance(GeminiCliQuotaProvider.RequestTimeout);
        await pending;

        var timedOut = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Stale, timedOut.State);
        Assert.AreEqual("timeout", timedOut.ErrorCode);

        harness.Time.Advance(GeminiCliQuotaProvider.MaximumCacheAge);
        var expired = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.IsNull(expired.Value);
        Assert.AreEqual("cache_expired", expired.ErrorCode);
    }

    [TestMethod]
    public async Task OwnerChangeCancelsLateQuotaAndProjectChangeReplacesCachedReading()
    {
        using (var harness = new Harness())
        {
            await harness.LoadQuotaAsync();
            harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
            harness.Handler.EnqueueJson(LoadResponse);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Handler.Enqueue(async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonResponse(QuotaResponse);
            });
            var pending = harness.Provider.PollOnceAsync();
            await entered.Task;
            harness.WriteAuth(token: "synthetic-access-B");
            await harness.Provider.PublishCurrentAsync();
            await pending;
            var snapshot = await ReadOneAsync(harness, RemainingId);
            Assert.AreEqual(MetricState.Unavailable, snapshot.State);
            Assert.IsNull(snapshot.Value);
        }

        using (var harness = new Harness())
        {
            harness.SelectedProject = null;
            await harness.LoadQuotaAsync();
            harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
            harness.Handler.EnqueueJson("{\"currentTier\":{\"id\":\"standard-tier\"},\"cloudaicompanionProject\":\"synthetic-project-b\"}");
            harness.Handler.EnqueueJson("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.25}]}");
            await harness.Provider.PollOnceAsync();
            var snapshot = await ReadOneAsync(harness, RemainingId);
            Assert.AreEqual(25d, snapshot.Value!.Value.AsDouble(), 0.000001);
            using var request = JsonDocument.Parse(harness.Handler.Requests[^1].Body!);
            Assert.AreEqual("synthetic-project-b", request.RootElement.GetProperty("project").GetString());
        }
    }

    [TestMethod]
    public async Task PendingBackpressuredPublishIsCanceledWhenCredentialOwnerChanges()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        await AssertPendingPublicationInvalidatedAsync(harness, () =>
        {
            harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
            harness.WriteAuth(token: "synthetic-access-B");
        }, LoadResponse, expectedState: MetricState.Unavailable, expectedError: null);
    }

    [TestMethod]
    public async Task PendingBackpressuredPublishIsCanceledWhenSelectedProjectChanges()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        var loadResponse = "{\"currentTier\":{\"id\":\"standard-tier\"},\"cloudaicompanionProject\":\"" +
            ProjectB + "\"}";
        await AssertPendingPublicationInvalidatedAsync(harness, () =>
        {
            harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
            harness.SelectedProject = ProjectB;
        }, loadResponse, expectedState: MetricState.Unavailable, expectedError: null);

        var latestLoad = harness.Handler.Requests.Last(request => request.Uri == GeminiCliQuotaProvider.LoadCodeAssistEndpoint);
        using var body = JsonDocument.Parse(latestLoad.Body!);
        Assert.AreEqual(ProjectB, body.RootElement.GetProperty("cloudaicompanionProject").GetString());
        Assert.AreEqual(ProjectB, body.RootElement.GetProperty("metadata").GetProperty("duetProject").GetString());
    }

    [TestMethod]
    public async Task PendingBackpressuredFreshPublishIsCanceledWhenCacheExpiresOrResetPasses()
    {
        using (var harness = new Harness())
        {
            await harness.LoadQuotaAsync();
            await AssertPendingPublicationInvalidatedAsync(harness,
                () => harness.Time.Advance(GeminiCliQuotaProvider.MaximumCacheAge),
                LoadResponse, MetricState.Unavailable, "cache_expired");
        }

        using (var harness = new Harness())
        {
            var resetAt = InitialUtc.AddMinutes(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            await harness.LoadQuotaAsync("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.625,\"resetTime\":\"" +
                resetAt + "\"}]}");
            await AssertPendingPublicationInvalidatedAsync(harness,
                () => harness.Time.Advance(TimeSpan.FromMinutes(5)),
                LoadResponse, MetricState.Unavailable, "reset_elapsed");
        }
    }

    [TestMethod]
    public async Task LongRetryAfterOutlivesCacheWithoutAnEarlyRequest()
    {
        using var harness = new Harness();
        harness.WriteAuth(expiry: InitialUtc.AddDays(7));
        await harness.LoadQuotaAsync();
        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(LoadResponse);
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests, TimeSpan.FromDays(2));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(4, harness.Handler.Requests.Length);

        harness.Time.Advance(GeminiCliQuotaProvider.MaximumCacheAge + GeminiCliQuotaProvider.PollInterval);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(4, harness.Handler.Requests.Length,
            "The provider must not retry before the server's two-day Retry-After deadline.");
        var expired = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.AreEqual("cache_expired", expired.ErrorCode);

        harness.Handler.EnqueueJson(LoadResponse);
        harness.Handler.EnqueueJson(QuotaResponse);
        harness.Time.Advance(TimeSpan.FromDays(2) - GeminiCliQuotaProvider.MaximumCacheAge -
            GeminiCliQuotaProvider.PollInterval + TimeSpan.FromSeconds(1));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(6, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task ModelMetricRegistrationHasLifetimeCeiling()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        var additions = Enumerable.Range(0, GeminiCliQuotaProvider.MaxRegisteredModels - 1)
            .Select(index => "{\"modelId\":\"synthetic-model-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "\",\"remainingFraction\":0.5}");
        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(LoadResponse);
        harness.Handler.EnqueueJson("{\"buckets\":[" + string.Join(',', additions) + "]}");
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(GeminiCliQuotaProvider.MaxRegisteredModels * 2, harness.Registry.GetSnapshot().Count);

        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueJson(LoadResponse);
        harness.Handler.EnqueueJson("{\"buckets\":[{\"modelId\":\"synthetic-overflow-model\",\"remainingFraction\":0.5}]}");
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(GeminiCliQuotaProvider.MaxRegisteredModels * 2, harness.Registry.GetSnapshot().Count);
        var snapshot = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Error, snapshot.State);
        Assert.AreEqual("model_limit_exceeded", snapshot.ErrorCode);
    }

    [TestMethod]
    public async Task RedirectAndUnauthorizedResponsesFailClosed()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueRedirect();
        await harness.Provider.PollOnceAsync();
        var redirected = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Error, redirected.State);
        Assert.AreEqual("redirect_rejected", redirected.ErrorCode);

        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        harness.Handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        await harness.Provider.PollOnceAsync();
        var unauthorized = await ReadOneAsync(harness, RemainingId);
        Assert.AreEqual(MetricState.Error, unauthorized.State);
        Assert.AreEqual("auth_required", unauthorized.ErrorCode);
        Assert.IsNull(unauthorized.Value);

        harness.Time.Advance(GeminiCliQuotaProvider.PollInterval);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(4, harness.Handler.Requests.Length,
            "The same rejected access token must not be sent again until the local CLI rotates it.");
    }

    [TestMethod]
    public async Task RunPublishesStoppedMetricsOnShutdown()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueJson(LoadResponse);
        harness.Handler.EnqueueJson(QuotaResponse);
        await using var reader = harness.Bus.Subscribe(new MetricId(RemainingId)).GetAsyncEnumerator();
        using var stop = new CancellationTokenSource();
        var firstSnapshot = reader.MoveNextAsync().AsTask();
        var run = harness.Provider.RunAsync(stop.Token);
        Assert.IsTrue(await firstSnapshot.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(MetricConfidence.Unknown, reader.Current.Confidence);
    }

    [TestMethod]
    public async Task RunMonitorCancelsBlockedPublisherBeforeNextNetworkPoll()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        await using var reader = harness.Bus.Subscribe(new MetricId(RemainingId)).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        using var stop = new CancellationTokenSource();
        var run = harness.Provider.RunAsync(stop.Token);
        try
        {
            Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            await harness.Provider.PublishCurrentAsync();
            var blocked = harness.Provider.PublishCurrentAsync().AsTask();
            Assert.IsFalse(blocked.IsCompleted);
            harness.WriteAuth(token: "synthetic-access-B");
            harness.Time.Advance(TimeSpan.FromSeconds(1));
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual(MetricState.Fresh, reader.Current.State);
            Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
            Assert.IsNull(reader.Current.Value);
            Assert.AreEqual(2, harness.Handler.Requests.Length);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        }
    }

    [TestMethod]
    public async Task RunStillPublishesStoppedWhenWorkerDrainFaultsAndPreservesFault()
    {
        using var harness = new Harness();
        await harness.LoadQuotaAsync();
        await using var reader = harness.Bus.Subscribe(new MetricId(RemainingId)).GetAsyncEnumerator();
        var stopped = reader.MoveNextAsync().AsTask();
        harness.FailProjectRead = true;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Provider.RunAsync(default));
        Assert.AreEqual("synthetic_owner_failure", failure.Message);
        Assert.IsTrue(await stopped.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
    }

    private static GeminiCliQuotaReading Parse(string json) =>
        GeminiCliQuotaParser.Parse(Encoding.UTF8.GetBytes(json), InitialUtc);

    private static async Task AssertPendingPublicationInvalidatedAsync(Harness harness, Action transition,
        string loadResponse, MetricState expectedState, string? expectedError)
    {
        await using var reader = harness.Bus.Subscribe(new MetricId(RemainingId)).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        await harness.Provider.PublishCurrentAsync();
        var blocked = harness.Provider.PublishCurrentAsync().AsTask();
        Assert.IsFalse(blocked.IsCompleted, "A full subscriber channel must block the pending publication.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return JsonResponse(loadResponse);
        });
        harness.Handler.EnqueueJson(QuotaResponse);
        transition();
        var poll = harness.Provider.PollOnceAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual(MetricState.Fresh, reader.Current.State,
                "A snapshot buffered before the transition remains observable by contract.");
            var next = reader.MoveNextAsync().AsTask();
            await harness.Provider.PublishCurrentAsync();
            Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(expectedState, reader.Current.State);
            Assert.AreEqual(expectedError, reader.Current.ErrorCode);
            Assert.IsNull(reader.Current.Value, "The blocked old value must not escape after the transition.");
        }
        finally
        {
            release.TrySetResult();
            await poll.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<MetricSnapshot[]> ReadAllAsync(Harness harness)
    {
        var ids = new[] { RemainingId, ResetId };
        var readers = ids.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        await harness.Provider.PublishCurrentAsync();
        await Task.WhenAll(pending);
        var snapshots = readers.Select(reader => reader.Current).ToArray();
        foreach (var reader in readers) await reader.DisposeAsync();
        return snapshots;
    }

    private static async Task<MetricSnapshot> ReadOneAsync(Harness harness, string id)
    {
        await using var reader = harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        return reader.Current;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Harness : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "cerith-gemini-test-" + Guid.NewGuid().ToString("N"));
        public string GeminiDirectory => Path.Combine(Home, ".gemini");
        public string? SelectedProject { get; set; } = ProjectA;
        public bool FailProjectRead { get; set; }
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public TestTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public GeminiCliQuotaProvider Provider { get; }

        public Harness()
        {
            Directory.CreateDirectory(GeminiDirectory);
            WriteSettings("oauth-personal");
            WriteAuth();
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Provider = new GeminiCliQuotaProvider(Registry, Bus,
                new GeminiCliQuotaAuthReader(Home, Time, _ => null), Client, Time,
                () => FailProjectRead ? throw new InvalidOperationException("synthetic_owner_failure") : SelectedProject);
        }

        public void WriteSettings(string selectedType) =>
            File.WriteAllText(Path.Combine(GeminiDirectory, "settings.json"),
                JsonSerializer.Serialize(new { security = new { auth = new { selectedType } } }));

        public void WriteAuth(string token = "synthetic-access-A", DateTimeOffset? expiry = null,
            string selectedType = "oauth-personal")
        {
            WriteSettings(selectedType);
            File.WriteAllText(Path.Combine(GeminiDirectory, "oauth_creds.json"), JsonSerializer.Serialize(new
            {
                access_token = token,
                expiry_date = (expiry ?? InitialUtc.AddHours(1)).ToUnixTimeMilliseconds(),
                refresh_token = "synthetic-refresh-never-used"
            }));
        }

        public async Task LoadQuotaAsync(string quotaResponse = QuotaResponse)
        {
            Handler.EnqueueJson(LoadResponse);
            Handler.EnqueueJson(quotaResponse);
            await Provider.PollOnceAsync();
        }

        public void Dispose()
        {
            Provider.Dispose();
            Client.Dispose();
            Bus.Dispose();
            try { Directory.Delete(Home, recursive: true); }
            catch (DirectoryNotFoundException) { }
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
        public void EnqueueRedirect() => Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/redirect");
            return Task.FromResult(response);
        });
        public void EnqueueException(Exception exception) => Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));
        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var observation = new RequestObservation(request.Method, request.RequestUri!,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.MediaType);
            _requests.Enqueue(observation);
            var response = _responses.TryDequeue(out var queued)
                ? await queued(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, string? AuthorizationScheme,
        string? AccessToken, string? Body, string? ContentType);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly ConcurrentBag<ManualTimer> _timers = new();
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
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
                    _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : now + Math.Max(0, dueTime.Ticks);
                    _period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
                    return true;
                }
            }

            public void FireDue(long now)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed || _due > now) return;
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
