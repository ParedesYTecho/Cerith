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
using Cerith.TelemetryService.Providers.AI.Claude;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Claude;

[TestClass]
public sealed class ClaudeQuotaProviderTests
{
    private const string SessionRemaining = "ai.claude.session.remaining";
    private const string SessionReset = "ai.claude.session.reset";
    private const string WeeklyRemaining = "ai.claude.weekly.remaining";
    private const string WeeklyReset = "ai.claude.weekly.reset";
    private const string OpusRemaining = "ai.claude.weekly.model.opus.remaining";
    private const string OpusReset = "ai.claude.weekly.model.opus.reset";
    private const string SonnetRemaining = "ai.claude.weekly.model.sonnet.remaining";
    private const string SonnetReset = "ai.claude.weekly.model.sonnet.reset";
    private const string AccountA = "11111111-2222-4333-8444-555555555555";
    private const string AccountB = "66666666-7777-4888-8999-aaaaaaaaaaaa";
    private const string OrganizationA = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff";
    private const string OrganizationB = "01234567-89ab-4cde-8fab-0123456789ab";
    private const string TokenA = "synthetic-oauth-access-a";
    private const string TokenB = "synthetic-oauth-access-b";
    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ParserMapsReportedUtilizationAndLegacyModelWindowsAndIgnoresUnverifiedScopes()
    {
        var reading = Parse(UsageJson());

        AssertPoint(reading, SessionRemaining, MetricState.Fresh, 62.75);
        AssertPoint(reading, WeeklyRemaining, MetricState.Fresh, 48.5);
        AssertPoint(reading, OpusRemaining, MetricState.Fresh, 44.5);
        AssertPoint(reading, SonnetRemaining, MetricState.Fresh, 20.25);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-01T11:30:00+02:00"),
            Point(reading, SessionReset).Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-12T10:00:00Z"),
            Point(reading, OpusReset).Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-13T10:00:00Z"),
            Point(reading, SonnetReset).Value!.Value.AsDateTimeOffset());
        CollectionAssert.AreEquivalent(new[]
        {
            SessionRemaining, SessionReset, WeeklyRemaining, WeeklyReset,
            OpusRemaining, OpusReset, SonnetRemaining, SonnetReset
        }, reading.Points.Select(point => point.Id).ToArray());
        Assert.IsFalse(reading.Points.Any(point => point.Id.Contains("credit", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(reading.Points.Any(point => point.Id.Contains("extra_usage", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ParserPreservesMissingInvalidAndElapsedWindowStatesWithoutInventingValues()
    {
        var missing = Parse("""{"five_hour":null,"seven_day":{"utilization":101,"resets_at":"bad"}}""");
        AssertPoint(missing, SessionRemaining, MetricState.Unavailable, null);
        AssertPoint(missing, SessionReset, MetricState.Unavailable, null);
        AssertPoint(missing, WeeklyRemaining, MetricState.Error, null, "invalid_quota");
        AssertPoint(missing, WeeklyReset, MetricState.Error, null, "invalid_reset");

        var elapsed = Parse($"{{\"five_hour\":{{\"utilization\":0,\"resets_at\":\"{InitialUtc.AddSeconds(-1):O}\"}}}}");
        AssertPoint(elapsed, SessionRemaining, MetricState.Unavailable, null, "reset_elapsed");
        AssertPoint(elapsed, SessionReset, MetricState.Unavailable, null, "reset_elapsed");

        var invalid = Parse("not-json");
        Assert.IsTrue(invalid.Points.All(point => point.State == MetricState.Error && point.ErrorCode == "schema_invalid"));
    }

    [TestMethod]
    public void AuthReaderRejectsExpiredSavedOAuthAndAllActiveOfficialRoutingOverrides()
    {
        using var harness = new Harness();
        harness.WriteAuth(TokenA, InitialUtc.AddMinutes(-1));
        Assert.AreEqual(ClaudeAuthStatus.Error, harness.AuthReader.Read().Status);
        Assert.AreEqual("auth_refresh_required", harness.AuthReader.Read().ErrorCode);

        harness.WriteAuth(TokenA, InitialUtc.AddHours(1));
        harness.Environment["ANTHROPIC_AUTH_TOKEN"] = "synthetic-unrelated-token";
        Assert.AreEqual(ClaudeAuthStatus.Unsupported, harness.AuthReader.Read().Status);
        harness.Environment.Clear();
        File.WriteAllText(Path.Combine(harness.Home, "settings.json"),
            "{\"apiKeyHelper\":\"synthetic-helper\",\"env\":{}}");
        Assert.AreEqual(ClaudeAuthStatus.Unsupported, harness.AuthReader.Read().Status);
        Assert.AreEqual(0, harness.Handler.Requests.Length);
    }

    [TestMethod]
    public async Task MissingAndExpiredAuthMakeNoWritesAndReuseOwnerUpdatedToken()
    {
        using var harness = new Harness();
        var path = Path.Combine(harness.Home, ".credentials.json");
        File.Delete(path);
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(MetricState.Unavailable, (await ReadOneAsync(harness, SessionRemaining)).State);
        harness.WriteAuth(TokenA, InitialUtc.AddMinutes(-1));
        var original = File.ReadAllBytes(path);
        await harness.Provider.PollOnceAsync();
        var expired = await ReadOneAsync(harness, SessionRemaining);
        Assert.IsNull(expired.Value);
        Assert.AreEqual("auth_refresh_required", expired.ErrorCode);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.AreEqual(0, harness.Handler.Requests.Length);
        harness.WriteAuth(TokenB, InitialUtc.AddHours(1));
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, UsageJson());
        Assert.AreEqual(MetricState.Fresh, (await ReadOneAsync(harness, SessionRemaining)).State);
        Assert.AreEqual(3, harness.Handler.Requests.Length);
        Assert.IsTrue(harness.Handler.Requests.All(request => request.AccessToken == TokenB));
    }

    [TestMethod]
    public void ExplicitProfileFederationEmptyKeyGatewayAndManagedPolicyRejectSavedOAuth()
    {
        using var harness = new Harness();
        foreach (var name in new[] { "ANTHROPIC_PROFILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_ORGANIZATION_ID",
            "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_OAUTH_TOKEN" })
        {
            harness.Environment[name] = "";
            Assert.AreEqual(ClaudeAuthStatus.Unsupported, harness.AuthReader.Read().Status, name);
            harness.Environment.Clear();
        }
        var profileDirectory = Path.Combine(harness.Home, "anthropic-profiles");
        Directory.CreateDirectory(profileDirectory);
        var selector = Path.Combine(profileDirectory, "active_config");
        File.WriteAllText(selector, "synthetic-federated-profile");
        Assert.AreEqual(ClaudeAuthStatus.Unsupported, harness.AuthReader.Read().Status);
        File.Delete(selector);
        File.WriteAllText(Path.Combine(harness.Home, "settings.json"), "{\"forceLoginMethod\":\"gateway\"}");
        Assert.AreEqual(ClaudeAuthStatus.Unsupported, harness.AuthReader.Read().Status);
        var managed = new ClaudeQuotaAuthReader(harness.Home, _ => null, harness.Time, () => true);
        Assert.AreEqual(ClaudeAuthStatus.Unsupported, managed.Read().Status);
    }

    [TestMethod]
    public async Task ProviderMakesOnlyFixedReadOnlyProfileUsageProfileRequestsAndPublishesTypedMetrics()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueProfile(AccountA, OrganizationA);
        harness.Handler.EnqueueJson(UsageJson());
        harness.Handler.EnqueueProfile(AccountA, OrganizationA);
        var subscription = harness.Bus.Subscribe(new MetricId(SessionRemaining)).GetAsyncEnumerator();
        var pending = subscription.MoveNextAsync().AsTask();

        await harness.Provider.PollOnceAsync();
        Assert.IsTrue(await pending);
        var snapshot = subscription.Current;
        await subscription.DisposeAsync();

        var requests = harness.Handler.Requests;
        CollectionAssert.AreEqual(new[]
        {
            ClaudeQuotaProvider.ProfileEndpoint,
            ClaudeQuotaProvider.UsageEndpoint,
            ClaudeQuotaProvider.ProfileEndpoint
        }, requests.Select(request => request.Uri).ToArray());
        Assert.IsTrue(requests.All(request => request.Method == HttpMethod.Get && request.AuthorizationScheme == "Bearer" &&
            request.AccessToken == TokenA && request.Beta == "oauth-2025-04-20" && request.ContentType is null));
        Assert.IsTrue(requests.All(request => request.Uri.Scheme == Uri.UriSchemeHttps && request.Uri.Host == "api.anthropic.com"));
        Assert.AreEqual(MetricState.Fresh, snapshot.State);
        Assert.AreEqual(62.75, snapshot.Value!.Value.AsDouble(), 0.000001);
        Assert.AreEqual(MetricConfidence.Measured, snapshot.Confidence);
        Assert.AreEqual("Claude.OAuthUsage.ReadOnly.R1", snapshot.Source);

        var descriptors = harness.Registry.GetSnapshot();
        Assert.AreEqual(MetricValueKind.Double, descriptors.Single(item => item.Id.Value == SessionRemaining).ValueKind);
        Assert.AreEqual("%", descriptors.Single(item => item.Id.Value == WeeklyRemaining).Unit);
        Assert.AreEqual(MetricValueKind.DateTimeOffset, descriptors.Single(item => item.Id.Value == SessionReset).ValueKind);
    }

    [TestMethod]
    public async Task OrganizationChangeBetweenProfileReadsDiscardsPriorOwnerCache()
    {
        using var harness = new Harness();
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, UsageJson());
        harness.Time.Advance(ClaudeQuotaProvider.PollInterval);
        harness.Handler.EnqueueProfile(AccountA, OrganizationA);
        harness.Handler.EnqueueJson(UsageJson());
        harness.Handler.EnqueueProfile(AccountA, OrganizationB);
        await harness.Provider.PollOnceAsync();

        var current = await ReadOneAsync(harness, SessionRemaining);
        Assert.IsNull(current.Value);
        Assert.AreEqual(MetricState.Error, current.State);
        Assert.AreEqual("identity_changed", current.ErrorCode);
    }

    [TestMethod]
    public async Task OwnerGenerationCancelsBackpressuredPublicationBeforeNewOwnerSnapshots()
    {
        using var harness = new Harness(subscriberCapacity: 1);
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, UsageJson());
        var reader = harness.Bus.Subscribe(new MetricId(SessionRemaining)).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await harness.Provider.PublishCurrentAsync();

        var pendingOldPublication = harness.Provider.PublishCurrentAsync();
        await Task.Delay(20);
        Assert.IsFalse(pendingOldPublication.IsCompleted, "The full subscriber queue should block publication.");

        harness.Time.Advance(ClaudeQuotaProvider.PollInterval);
        harness.Handler.EnqueueProfile(AccountB, OrganizationB);
        harness.Handler.EnqueueJson(UsageJson(81.25));
        harness.Handler.EnqueueProfile(AccountB, OrganizationB);
        var pollForNewOwner = harness.Provider.PollOnceAsync();

        await pendingOldPublication.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, reader.Current.State, "Previously buffered snapshots cannot retract.");
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State,
            "Only the pretransition buffered value may precede the new-owner empty state.");
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(18.75, reader.Current.Value!.Value.AsDouble(), 0.000001);
        await pollForNewOwner.WaitAsync(TimeSpan.FromSeconds(2));
        await reader.DisposeAsync();
    }

    [TestMethod]
    public async Task ResetCrossingCancelsPendingFreshPublicationBeforeUnavailableSnapshot()
    {
        using var harness = new Harness(subscriberCapacity: 1);
        var usage = $$$"""
            {"five_hour":{"utilization":12.5,"resets_at":"{{{InitialUtc.AddMinutes(10):O}}}"},
             "seven_day":{"utilization":30,"resets_at":"{{{InitialUtc.AddDays(2):O}}}"}}
            """;
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, usage);

        var reader = harness.Bus.Subscribe(new MetricId(SessionRemaining)).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);

        await harness.Provider.PublishCurrentAsync();

        var pendingFreshPublication = harness.Provider.PublishCurrentAsync();
        await Task.Delay(20);
        Assert.IsFalse(pendingFreshPublication.IsCompleted, "The full subscriber queue should block the old value publication.");

        harness.Time.Advance(TimeSpan.FromMinutes(11));
        var expiryPublication = harness.Provider.PublishCurrentAsync();
        await pendingFreshPublication.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, reader.Current.State, "Previously buffered snapshots cannot retract.");
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("reset_elapsed", reader.Current.ErrorCode);
        await expiryPublication.WaitAsync(TimeSpan.FromSeconds(2));
        await reader.DisposeAsync();
    }

    [TestMethod]
    public async Task CacheBecomesStaleExpiresAtResetAndNeverRefreshesObservedAt()
    {
        using var harness = new Harness();
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, UsageJson());
        harness.Time.Advance(ClaudeQuotaProvider.StaleAfter);
        var stale = await ReadOneAsync(harness, SessionRemaining);
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual(InitialUtc, stale.ObservedAt);
        Assert.AreEqual(62.75, stale.Value!.Value.AsDouble(), 0.000001);

        harness.Time.Advance(TimeSpan.FromMinutes(13));
        var expired = await ReadOneAsync(harness, SessionRemaining);
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.IsNull(expired.Value);
        Assert.AreEqual("cache_expired", expired.ErrorCode);
    }

    [TestMethod]
    public async Task LongerRetryAfterIsNotShortenedByCacheExpiryAndUnauthorizedClearsCache()
    {
        using var harness = new Harness();
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, UsageJson());
        harness.Time.Advance(ClaudeQuotaProvider.PollInterval);
        harness.Handler.EnqueueProfile(AccountA, OrganizationA);
        harness.Handler.EnqueueStatus(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMinutes(20));
        await harness.Provider.PollOnceAsync();
        var stale = await ReadOneAsync(harness, SessionRemaining);
        Assert.AreEqual(MetricState.Stale, stale.State);
        Assert.AreEqual("rate_limited", stale.ErrorCode);

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(5, harness.Handler.Requests.Length, "Retry-After remains in force beyond cache expiry.");
        var expired = await ReadOneAsync(harness, SessionRemaining);
        Assert.AreEqual(MetricState.Unavailable, expired.State);
        Assert.AreEqual("cache_expired", expired.ErrorCode);

        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Handler.EnqueueProfile(AccountA, OrganizationA);
        harness.Handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        await harness.Provider.PollOnceAsync();
        var rejected = await ReadOneAsync(harness, SessionRemaining);
        Assert.IsNull(rejected.Value);
        Assert.AreEqual(MetricState.Error, rejected.State);
        Assert.AreEqual("auth_required", rejected.ErrorCode);
        var count = harness.Handler.Requests.Length;
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Provider.PollOnceAsync();
        Assert.AreEqual(count, harness.Handler.Requests.Length, "401 suppresses requests until the saved credential changes.");
    }

    [TestMethod]
    public async Task RedirectOversizedBodyAndProfileFailureNeverPublishQuota()
    {
        using var harness = new Harness();
        harness.Handler.EnqueueRedirect();
        await harness.Provider.PollOnceAsync();
        var redirect = await ReadOneAsync(harness, SessionRemaining);
        Assert.AreEqual("redirect_rejected", redirect.ErrorCode);
        Assert.AreEqual(1, harness.Handler.Requests.Length);

        harness.Time.Advance(ClaudeQuotaProvider.PollInterval);
        harness.Handler.EnqueueProfile(AccountA, OrganizationA);
        harness.Handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[ClaudeQuotaProvider.MaxUsageResponseBytes + 1])
        });
        await harness.Provider.PollOnceAsync();
        var large = await ReadOneAsync(harness, SessionRemaining);
        Assert.IsNull(large.Value);
        Assert.AreEqual("response_too_large", large.ErrorCode);
    }

    [TestMethod]
    public async Task LocalAuthChangeCancelsInFlightRequestAndClearsAccountSnapshots()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return JsonResponse(ProfileJson(AccountA, OrganizationA));
        });
        var poll = harness.Provider.PollOnceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.WriteAuth("synthetic-oauth-access-b", InitialUtc.AddHours(1));
        await harness.Provider.ObserveLocalStateAsync();
        await poll.WaitAsync(TimeSpan.FromSeconds(2));
        var current = await ReadOneAsync(harness, SessionRemaining);
        Assert.IsNull(current.Value);
        Assert.AreEqual(MetricState.Unavailable, current.State);
        Assert.AreEqual(1, harness.Handler.Requests.Length);
        release.TrySetResult();
    }

    [TestMethod]
    public async Task OfflineAndDeadlineServeOnlyEligibleStaleCache()
    {
        using var harness = new Harness();
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, UsageJson());
        harness.Time.Advance(ClaudeQuotaProvider.PollInterval);
        harness.Handler.Enqueue((_, _) => throw new HttpRequestException("synthetic offline"));
        await harness.Provider.PollOnceAsync();
        var offline = await ReadOneAsync(harness, SessionRemaining);
        Assert.AreEqual(MetricState.Stale, offline.State);
        Assert.AreEqual("offline", offline.ErrorCode);
        Assert.AreEqual(InitialUtc, offline.ObservedAt);
        harness.Time.Advance(ClaudeQuotaProvider.PollInterval);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Enqueue(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable after cancellation.");
        });
        var poll = harness.Provider.PollOnceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Time.Advance(ClaudeQuotaProvider.RequestTimeout);
        await poll.WaitAsync(TimeSpan.FromSeconds(2));
        var timeout = await ReadOneAsync(harness, SessionRemaining);
        Assert.AreEqual(MetricState.Stale, timeout.State);
        Assert.AreEqual("timeout", timeout.ErrorCode);
        Assert.AreEqual(InitialUtc, timeout.ObservedAt);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ValidityDeadlineCancelsBackpressureWithoutAnotherPublisher(bool resetCrossing)
    {
        using var harness = new Harness(subscriberCapacity: 1);
        var usage = resetCrossing ? JsonSerializer.Serialize(new
        {
            five_hour = new { utilization = 10, resets_at = InitialUtc.AddSeconds(60) }
        }) : UsageJson();
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, usage);
        await using var reader = harness.Bus.Subscribe(new MetricId(SessionRemaining)).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await first);
        await harness.Provider.PublishCurrentAsync();
        var pending = harness.Provider.PublishCurrentAsync();
        Assert.IsFalse(pending.IsCompleted);
        harness.Time.Advance(resetCrossing ? TimeSpan.FromSeconds(61) : ClaudeQuotaProvider.MaximumCacheAge);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        var next = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsNull(reader.Current.Value);
        Assert.AreEqual(resetCrossing ? "reset_elapsed" : "cache_expired", reader.Current.ErrorCode);
    }

    [TestMethod]
    public async Task FaultedWorkerStillPublishesStoppedAndPreservesSupervisorFault()
    {
        using var harness = new Harness();
        await using var reader = harness.Bus.Subscribe(new MetricId(SessionRemaining)).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        harness.Handler.Enqueue((_, _) => throw new InvalidOperationException("synthetic supervisor fault"));
        var run = harness.Provider.RunAsync(CancellationToken.None);
        Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Fail("The supervisor must receive the worker fault.");
        }
        catch (AggregateException exception)
        {
            Assert.IsTrue(exception.Flatten().InnerExceptions.Any(error =>
                error is InvalidOperationException && error.Message == "synthetic supervisor fault"));
        }
    }

    [TestMethod]
    public async Task ShutdownWithFullSubscriberBoundsStoppedPublication()
    {
        using var harness = new Harness(subscriberCapacity: 1);
        await harness.PollAndDrainBaseMetrics(AccountA, OrganizationA, UsageJson());
        await using var reader = harness.Bus.Subscribe(new MetricId(SessionRemaining)).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await first);
        await harness.Provider.PublishCurrentAsync();
        using var stop = new CancellationTokenSource();
        var run = harness.Provider.RunAsync(stop.Token);
        Assert.IsFalse(run.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static ClaudeQuotaReading Parse(string json) =>
        ClaudeQuotaParser.Parse(Encoding.UTF8.GetBytes(json), InitialUtc);

    private static string UsageJson(double sessionUtilization = 37.25) => JsonSerializer.Serialize(new
    {
        five_hour = new { utilization = sessionUtilization, resets_at = "2026-10-01T11:30:00+02:00" },
        seven_day = new { utilization = 51.5, resets_at = "2026-10-12T10:00:00Z" },
        seven_day_opus = new { utilization = 55.5, resets_at = "2026-10-12T10:00:00Z" },
        seven_day_sonnet = new { utilization = 79.75, resets_at = "2026-10-13T10:00:00Z" },
        limits = new[]
        {
            new
            {
                kind = "weekly_scoped",
                scope = new { model = new { id = "claude-opus-4-1", display_name = "Opus" } },
                percent = 99.0,
                resets_at = "2026-10-12T10:00:00Z"
            },
            new
            {
                kind = "weekly_scoped",
                scope = new { model = new { id = "different-source-scope", display_name = "Opus" } },
                percent = 1.0,
                resets_at = "2026-10-12T10:00:00Z"
            }
        },
        extra_usage = new { is_enabled = true, used_credits = 450, monthly_limit = 900 },
        unknown_additive_field = true
    });

    private static ClaudeQuotaPoint Point(ClaudeQuotaReading reading, string id) =>
        reading.Points.Single(point => point.Id == id);

    private static void AssertPoint(ClaudeQuotaReading reading, string id, MetricState state,
        double? value, string? errorCode = null)
    {
        var point = Point(reading, id);
        Assert.AreEqual(state, point.State, id + " / " + point.ErrorCode);
        Assert.AreEqual(errorCode, point.ErrorCode, id);
        if (value is double expected)
        {
            Assert.IsNotNull(point.Value, id);
            Assert.AreEqual(expected, point.Value!.Value.AsDouble(), 0.000001, id);
        }
        else
        {
            Assert.IsNull(point.Value, id);
        }
    }

    private static string ProfileJson(string account, string organization) => JsonSerializer.Serialize(new
    {
        account = new { uuid = account },
        organization = new { uuid = organization },
        email = "synthetic@example.invalid"
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static async Task<MetricSnapshot> ReadOneAsync(Harness harness, string id)
    {
        await using var reader = harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await harness.Provider.PublishCurrentAsync();
        Assert.IsTrue(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        return reader.Current;
    }

    private sealed class Harness : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "cerith-claude-test-" + Guid.NewGuid().ToString("N"));
        public ConcurrentDictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; }
        public TestTimeProvider Time { get; } = new(InitialUtc);
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public ClaudeQuotaAuthReader AuthReader { get; }
        public ClaudeQuotaProvider Provider { get; }

        public Harness(int subscriberCapacity = 8)
        {
            Directory.CreateDirectory(Home);
            Bus = new MetricBus(subscriberCapacity);
            WriteAuth(TokenA, InitialUtc.AddHours(1));
            AuthReader = new ClaudeQuotaAuthReader(Home, name => Environment.TryGetValue(name, out var value) ? value :
                name == "ANTHROPIC_CONFIG_DIR" ? Path.Combine(Home, "anthropic-profiles") : null, Time, () => false);
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Provider = new ClaudeQuotaProvider(Registry, Bus, AuthReader, Client, Time);
        }

        public void WriteAuth(string token, DateTimeOffset expiry)
        {
            File.WriteAllText(Path.Combine(Home, ".credentials.json"), JsonSerializer.Serialize(new
            {
                claudeAiOauth = new
                {
                    accessToken = token,
                    expiresAt = expiry.ToUnixTimeMilliseconds(),
                    refreshToken = "synthetic-refresh-token-never-used"
                }
            }));
        }

        public async Task PollAndDrainBaseMetrics(string account, string organization, string usage)
        {
            var ids = new[] { SessionRemaining, SessionReset, WeeklyRemaining, WeeklyReset };
            var readers = ids.Select(id => Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
            var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            Handler.EnqueueProfile(account, organization);
            Handler.EnqueueJson(usage);
            Handler.EnqueueProfile(account, organization);
            await Provider.PollOnceAsync();
            await Task.WhenAll(pending);
            foreach (var reader in readers) await reader.DisposeAsync();
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
        public void Enqueue(HttpResponseMessage response) => Enqueue((_, _) => Task.FromResult(response));
        public void EnqueueProfile(string account, string organization) => EnqueueJson(ProfileJson(account, organization));
        public void EnqueueStatus(HttpStatusCode status, TimeSpan? retryAfter = null)
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is TimeSpan delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
            Enqueue((_, _) => Task.FromResult(response));
        }
        public void EnqueueRedirect()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://redirect.invalid/never-follow") }
            };
            Enqueue((_, _) => Task.FromResult(response));
        }
        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.Authorization;
            _requests.Enqueue(new RequestObservation(request.Method, request.RequestUri!, authorization?.Scheme,
                authorization?.Parameter, request.Headers.TryGetValues("anthropic-beta", out var beta) ? beta.Single() : null,
                request.Content?.Headers.ContentType?.MediaType));
            var response = _responses.TryDequeue(out var next)
                ? await next(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, string? AuthorizationScheme,
        string? AccessToken, string? Beta, string? ContentType);

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;
        private readonly List<TestTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            List<TestTimer> due;
            lock (_gate)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                due = _timers.Where(timer => timer.Due <= _timestamp).ToList();
                foreach (var timer in due) timer.Due = timer.Period > 0 ? _timestamp + timer.Period : long.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class TestTimer(TestTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; set; } = long.MaxValue;
            public long Period { get; private set; }
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    if (!owner._timers.Contains(this)) owner._timers.Add(this);
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._timestamp + dueTime.Ticks;
                    Period = period.Ticks;
                    return true;
                }
            }
            public void Fire() => callback(state);
            public void Dispose()
            {
                lock (owner._gate)
                {
                    _disposed = true;
                    owner._timers.Remove(this);
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
