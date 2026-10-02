#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Kiro;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Kiro;

[TestClass]
public sealed class KiroQuotaProviderTests
{
    private static readonly string[] TestMetricIds =
    [
        "ai.kiro.credits.included.remaining",
        "ai.kiro.credits.included.reset",
        "ai.kiro.credits.addon.remaining",
        "ai.kiro.credits.bonus.remaining"
    ];
    private static readonly byte[] IdentityKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ParserKeepsIncludedAddonAndBonusCreditsSeparate()
    {
        var parsed = Parse(CompleteUsage("synthetic-user-a", "synthetic-account-a"));

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Fresh, parsed.Reading.IncludedRemaining.State);
        Assert.AreEqual(87.25m, Decimal(parsed.Reading.IncludedRemaining));
        Assert.AreEqual(MetricState.Fresh, parsed.Reading.IncludedReset.State);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_893_456_000),
            parsed.Reading.IncludedReset.Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(29.75m, Decimal(parsed.Reading.AddOnRemaining));
        Assert.AreEqual(8.75m, Decimal(parsed.Reading.BonusRemaining));
        Assert.AreEqual(2, parsed.Reading.BonusAllocations!.Length);
    }

    [TestMethod]
    public void ParserRejectsAmbiguousIncludedCreditPools()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"usageLimitWithPrecision\":\"100.00\"}",
                "\"usageLimitWithPrecision\":\"100.00\"},{\"resourceType\":\"CREDIT\",\"currentUsage\":0,\"usageLimit\":100}");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Error, parsed.Reading.IncludedRemaining.State);
        Assert.AreEqual("quota_ambiguous", parsed.Reading.IncludedRemaining.ErrorCode);
    }

    [TestMethod]
    public void ParserKeepsMissingBucketsUnavailableInsteadOfInventingZero()
    {
        const string json = """
            {"userInfo":{"userId":"synthetic-user-a"},"usageBreakdownList":[]}
            """;

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.IncludedRemaining.State);
        Assert.IsNull(parsed.Reading.IncludedRemaining.Value);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.IncludedReset.State);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.AddOnRemaining.State);
        Assert.AreEqual(MetricState.Unavailable, parsed.Reading.BonusRemaining.State);
    }

    [TestMethod]
    public void ParserMarksMalformedCreditAsErrorRatherThanZero()
    {
        const string json = """
            {"user_info":{"user_id":"synthetic-user-a"},"usage_breakdown_list":[
              {"resource_type":"CREDIT","current_usage_with_precision":"not-a-number","usage_limit_with_precision":"100"}]}
            """;

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        Assert.AreEqual(MetricState.Error, parsed.Reading.IncludedRemaining.State);
        Assert.AreEqual("invalid_quota", parsed.Reading.IncludedRemaining.ErrorCode);
    }

    [TestMethod]
    public void ParserRejectsOverLimitIncludedCreditsWithoutClamping()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"currentUsageWithPrecision\":\"12.75\"",
                "\"currentUsageWithPrecision\":\"100.01\"");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.IncludedRemaining, "invalid_quota");
    }

    [TestMethod]
    public void ParserRejectsOverLimitAddOnCreditsWithoutClamping()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"addOnTotalUsage\":\"10.25\"", "\"addOnTotalUsage\":\"40.01\"");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.AddOnRemaining, "invalid_addon");
    }

    [TestMethod]
    public void ParserRejectsOverLimitBonusCreditsWithoutClamping()
    {
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"currentUsage\":\"1.25\"", "\"currentUsage\":\"10.01\"");

        var parsed = Parse(json);

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.BonusRemaining, "invalid_bonus");
        Assert.IsNull(parsed.Reading.BonusAllocations);
    }

    [TestMethod]
    public void ParserMarksMalformedAddOnAndBonusCreditsAsValuelessErrors()
    {
        var addOn = Parse(CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"addOnTotalUsage\":\"10.25\"", "\"addOnTotalUsage\":\"unknown\""));
        var bonus = Parse(CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("\"currentUsage\":\"1.25\"", "\"currentUsage\":\"unknown\""));

        Assert.IsNotNull(addOn.Reading);
        Assert.IsNotNull(bonus.Reading);
        AssertValuelessError(addOn.Reading.AddOnRemaining, "invalid_addon");
        AssertValuelessError(bonus.Reading.BonusRemaining, "invalid_bonus");
        Assert.IsNull(bonus.Reading.BonusAllocations);
    }

    [TestMethod]
    public void ParserRejectsBonusListsAboveTheFixedAllocationBound()
    {
        var allocations = string.Join(",", Enumerable.Repeat(
            "{\"currentUsage\":\"0\",\"usageLimit\":\"1\"}",
            KiroQuotaParser.MaximumBonusAllocations + 1));
        var parsed = Parse(string.Concat(
            "{\"userInfo\":{\"userId\":\"synthetic-user-a\"},\"freeTrialInfo\":{\"bonuses\":[",
            allocations, "]}}"));

        Assert.IsNotNull(parsed.Reading);
        AssertValuelessError(parsed.Reading.BonusRemaining, "bonus_limit_exceeded");
        Assert.IsNull(parsed.Reading.BonusAllocations);
    }

    [TestMethod]
    public void AcpUsageDispatchRequiresAdvertisedTypedExtension()
    {
        using var supported = JsonDocument.Parse("""
            {"agentCapabilities":{"_meta":{"kiro":{"extensionMethods":["_kiro/account/getUsage"]}}}}
            """);
        using var unsupported = JsonDocument.Parse("""
            {"agentCapabilities":{"_meta":{"kiro":{"extensionMethods":["_kiro/session/context"]}}}}
            """);
        using var empty = JsonDocument.Parse("{}");

        Assert.IsTrue(KiroAcpUsageClient.AdvertisesUsageMethod(supported.RootElement));
        Assert.IsFalse(KiroAcpUsageClient.AdvertisesUsageMethod(unsupported.RootElement));
        Assert.IsFalse(KiroAcpUsageClient.AdvertisesUsageMethod(empty.RootElement));
    }

    [TestMethod]
    public void AcpStartInfoUsesOfficialCliAuthAndDoesNotForwardApiKey()
    {
        var startInfo = KiroAcpUsageClient.CreateStartInfo();
        var identityStartInfo = KiroAcpUsageClient.CreateIdentityStartInfo();

        CollectionAssert.AreEqual(new[] { "acp", "--agent-engine=v3", "--auth-method=cli" },
            startInfo.ArgumentList.ToArray());
        CollectionAssert.AreEqual(new[] { "whoami", "--format", "json" },
            identityStartInfo.ArgumentList.ToArray());
        Assert.AreEqual(Path.GetTempPath(), startInfo.WorkingDirectory);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.AreEqual("error", startInfo.Environment["KIRO_LOG_LEVEL"]);
        Assert.IsTrue(startInfo.Environment.TryGetValue("KIRO_API_KEY", out var apiKey));
        Assert.IsNull(apiKey);
        Assert.IsTrue(identityStartInfo.Environment.TryGetValue("KIRO_API_KEY", out apiKey));
        Assert.IsNull(apiKey);
    }

    [TestMethod]
    public void IdentityParserTreatsAccountAsOpaqueAndRecognizesSignedOutState()
    {
        var signedOut = KiroAcpUsageClient.ParseIdentity(
            Encoding.UTF8.GetBytes("{\"account\":null}"), IdentityKey);
        var first = KiroAcpUsageClient.ParseIdentity(
            Encoding.UTF8.GetBytes("{\"account\":{\"synthetic\":\"account-a\"}}"), IdentityKey);
        var second = KiroAcpUsageClient.ParseIdentity(
            Encoding.UTF8.GetBytes("{\"account\":{\"synthetic\":\"account-b\"}}"), IdentityKey);

        Assert.AreEqual(KiroIdentityStatus.SignedOut, signedOut.Status);
        Assert.AreEqual(KiroIdentityStatus.Ready, first.Status);
        Assert.AreEqual(KiroIdentityStatus.Ready, second.Status);
        Assert.AreEqual(32, first.Fingerprint!.Length);
        Assert.IsFalse(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            first.Fingerprint, second.Fingerprint!));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(first.Fingerprint);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(second.Fingerprint!);
    }

    [TestMethod]
    public async Task IdentityClientReadsOnlySyntheticCliWhoAmIJson()
    {
        const string script = """
            @echo off
            echo {"account":{"opaque":"synthetic"}}
            """;

        var (startInfo, scriptPath) = CreateCommandStartInfo(script);
        KiroIdentityObservation observation;
        try
        {
            observation = await KiroAcpUsageClient.ReadIdentityAsync(TimeProvider.System, IdentityKey,
                CancellationToken.None, startInfo, TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(scriptPath);
        }

        Assert.AreEqual(KiroIdentityStatus.Ready, observation.Status);
        Assert.AreEqual(32, observation.Fingerprint!.Length);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(observation.Fingerprint);
    }

    [TestMethod]
    public async Task AcpClientSuppressesRetriesOnV3RateLimitNotificationWithoutParsingTiming()
    {
        const string script = """
            @echo off
            set /p "initialize="
            echo %initialize% | findstr /C:initialize >nul
            if errorlevel 1 exit /b 21
            echo {"jsonrpc":"2.0","id":1,"result":{"agentCapabilities":{"_meta":{"kiro":{"extensionMethods":["_kiro/account/getUsage"]}}}}}
            set /p "usage="
            echo %usage% | findstr /C:getUsage >nul
            if errorlevel 1 exit /b 22
            echo {"jsonrpc":"2.0","method":"_kiro/error/rate_limit","params":{"opaque":true}}
            """;

        var (startInfo, scriptPath) = CreateCommandStartInfo(script);
        KiroAcquisition acquisition;
        try
        {
            acquisition = await KiroAcpUsageClient.ReadAsync(TimeProvider.System, CancellationToken.None,
                startInfo, TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(scriptPath);
        }

        Assert.AreEqual(KiroAcquisitionStatus.RetrySuppressed, acquisition.Status);
        Assert.AreEqual("rate_limit_timing_unknown", acquisition.ErrorCode);
        Assert.IsNull(acquisition.Payload);
    }

    [TestMethod]
    public async Task AcpClientSendsInitializeAndUsageOnlyWithoutPrompting()
    {
        const string script = """
            @echo off
            set /p "initialize="
            echo %initialize% | findstr /C:initialize >nul
            if errorlevel 1 exit /b 21
            echo {"jsonrpc":"2.0","id":1,"result":{"agentCapabilities":{"_meta":{"kiro":{"extensionMethods":["_kiro/account/getUsage"]}}}}}
            set /p "usage="
            echo %usage% | findstr /C:getUsage >nul
            if errorlevel 1 exit /b 22
            echo {"jsonrpc":"2.0","id":2,"result":{"synthetic":true}}
            """;

        var (startInfo, scriptPath) = CreateCommandStartInfo(script);
        KiroAcquisition acquisition;
        try
        {
            acquisition = await KiroAcpUsageClient.ReadAsync(TimeProvider.System, CancellationToken.None,
                startInfo, TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(scriptPath);
        }

        Assert.AreEqual(KiroAcquisitionStatus.Ready, acquisition.Status);
        Assert.AreEqual("{\"synthetic\":true}", Encoding.UTF8.GetString(acquisition.Payload!));
    }

    [TestMethod]
    public async Task AcpClientMapsMissingCliToUnavailableWithoutStartingAnInference()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), $"missing-kiro-{Guid.NewGuid():N}.exe"));

        var acquisition = await KiroAcpUsageClient.ReadAsync(TimeProvider.System, CancellationToken.None,
            startInfo, TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(KiroAcquisitionStatus.Unavailable, acquisition.Status);
        Assert.AreEqual("cli_unavailable", acquisition.ErrorCode);
    }

    [TestMethod]
    public async Task AcpClientTimesOutAndStopsAStalledSyntheticChild()
    {
        const string script = """
            $null = [Console]::In.ReadLine()
            Start-Sleep -Seconds 30
            """;
        var started = TimeProvider.System.GetTimestamp();

        var acquisition = await KiroAcpUsageClient.ReadAsync(TimeProvider.System, CancellationToken.None,
            CreatePowerShellStartInfo(script), TimeSpan.FromMilliseconds(150));
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.AreEqual(KiroAcquisitionStatus.Error, acquisition.Status);
        Assert.AreEqual("request_timeout", acquisition.ErrorCode);
        Assert.IsTrue(elapsed < TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AcpLineReaderRejectsOversizedResponseLine()
    {
        var bytes = new byte[KiroAcpUsageClient.MaxResponseBytes + 2];
        Array.Fill(bytes, (byte)'x');
        bytes[^1] = (byte)'\n';
        var reader = new KiroAcpUsageClient.BoundedLineReader(new MemoryStream(bytes),
            KiroAcpUsageClient.MaxResponseBytes);

        var rejected = false;
        try { _ = await reader.ReadLineAsync(CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        Assert.IsTrue(rejected);
    }

    [TestMethod]
    public async Task ProviderPublishesTypedMeasuredValuesAndUsesCreditUnits()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))));
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(4, snapshots.Length);
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.State == MetricState.Fresh));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.Confidence == MetricConfidence.Measured));
        Assert.AreEqual(87.25m, Decimal(snapshots[0]));
        Assert.AreEqual(29.75m, Decimal(snapshots[2]));
        Assert.AreEqual(8.75m, Decimal(snapshots[3]));
        Assert.AreEqual("credits", registry.GetSnapshot().Single(descriptor =>
            descriptor.Id.Value == "ai.kiro.credits.included.remaining").Unit);
        Assert.AreEqual(MetricValueKind.DateTimeOffset, registry.GetSnapshot().Single(descriptor =>
            descriptor.Id.Value == "ai.kiro.credits.included.reset").ValueKind);
    }

    [TestMethod]
    public async Task ProviderClearsCachedValuesWhenOfficialUsageIdentityChanges()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var call = 0;
        using var provider = CreateProvider(registry, bus, _ =>
        {
            var payload = Interlocked.Increment(ref call) == 1
                ? CompleteUsage("synthetic-user-a", "synthetic-account-a")
                : """{"userInfo":{"userId":"synthetic-user-b"},"usageBreakdownList":[]}""";
            return Task.FromResult(Ready(payload));
        });
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var first = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Fresh, first[0].State);

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var afterIdentityChange = await readers.ReadNextBatchAsync();

        Assert.AreEqual(MetricState.Unavailable, afterIdentityChange[0].State);
        Assert.IsNull(afterIdentityChange[0].Value);
        Assert.AreEqual(MetricState.Unavailable, afterIdentityChange[2].State);
        Assert.AreEqual(MetricState.Unavailable, afterIdentityChange[3].State);
    }

    [TestMethod]
    public async Task ProviderPublishesCliUnavailableWithoutFabricatingValues()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(KiroAcquisition.Unavailable("cli_unavailable")));
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(4, snapshots.Length);
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.State == MetricState.Unavailable));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.Value is null));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.ErrorCode == "cli_unavailable"));
    }

    [TestMethod]
    public async Task ProviderSkipsUsageWhenOfficialIdentityCannotBeVerified()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var usageCalls = 0;
        using var provider = CreateProvider(registry, bus, _ =>
        {
            Interlocked.Increment(ref usageCalls);
            return Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a")));
        }, readIdentity: _ => Task.FromResult(KiroIdentityObservation.Unavailable()));
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(0, usageCalls);
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.State == MetricState.Unavailable));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.Value is null));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.ErrorCode == "identity_unavailable"));
    }

    [TestMethod]
    public async Task ProviderDiscardsDelayedUsageWhenOfficialIdentityChangesAcrossRequest()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var identityCalls = 0;
        var usageStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUsage = new TaskCompletionSource<KiroAcquisition>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(registry, bus, async _ =>
        {
            usageStarted.TrySetResult(true);
            return await releaseUsage.Task.ConfigureAwait(false);
        }, readIdentity: _ => Task.FromResult(
            IdentityObservation(Interlocked.Increment(ref identityCalls) == 1 ? (byte)1 : (byte)2)));
        await using var readers = SubscribeAll(bus);
        readers.Start();

        var poll = provider.PollOnceAsync();
        await usageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseUsage.TrySetResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a")));
        await poll.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(2, identityCalls);
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.State == MetricState.Unavailable));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.Value is null));
        Assert.IsTrue(snapshots.All(static snapshot => snapshot.ErrorCode == "identity_changed"));
    }

    [TestMethod]
    public async Task ProviderCancelsPendingOldOwnerPublicationWhenIdentityBecomesUnavailable()
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        var identityCalls = 0;
        var usageCalls = 0;
        var usageStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUsage = new TaskCompletionSource<KiroAcquisition>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(registry, bus, async _ =>
        {
            if (Interlocked.Increment(ref usageCalls) == 1)
                return Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"));
            usageStarted.TrySetResult(true);
            return await releaseUsage.Task.ConfigureAwait(false);
        }, readIdentity: _ =>
        {
            var call = Interlocked.Increment(ref identityCalls);
            return Task.FromResult(call < 4
                ? IdentityObservation(1)
                : KiroIdentityObservation.Unavailable());
        });
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var first = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Fresh, first[0].State);
        Assert.AreEqual(87.25m, Decimal(first[0]));

        foreach (var metricId in TestMetricIds)
            await bus.PublishAsync(new MetricSnapshot(new MetricId(metricId), null, ObservedAt,
                MetricState.Unavailable, MetricConfidence.Unknown, "test_marker", "test_marker"));
        var oldOwnerPublication = provider.PublishOnceAsync();
        await WaitForActivePublicationAsync(provider, oldOwnerPublication);
        var poll = provider.PollOnceAsync();
        await usageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseUsage.TrySetResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a")));
        await poll.WaitAsync(TimeSpan.FromSeconds(2));
        await oldOwnerPublication.WaitAsync(TimeSpan.FromSeconds(2));

        var markers = await readers.ReadNextBatchAsync();
        Assert.IsTrue(markers.All(static snapshot => snapshot.ErrorCode == "test_marker"));
        await provider.PublishOnceAsync();
        var unavailable = await readers.ReadNextBatchAsync();
        Assert.IsTrue(unavailable.All(static snapshot => snapshot.State == MetricState.Unavailable));
        Assert.IsTrue(unavailable.All(static snapshot => snapshot.Value is null));
        Assert.IsTrue(unavailable.All(static snapshot => snapshot.ErrorCode == "identity_unavailable"));
        Assert.AreEqual(4, identityCalls);
        Assert.AreEqual(2, usageCalls);
    }

    [TestMethod]
    public async Task ProviderSuppressesUsageRetriesButContinuesIdentityAndCacheTransitions()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt);
        var usageCalls = 0;
        var identityCalls = 0;
        using var provider = CreateProvider(registry, bus, _ =>
        {
            var call = Interlocked.Increment(ref usageCalls);
            return Task.FromResult(call == 1
                ? Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))
                : call == 2
                    ? KiroAcquisition.RetrySuppressed()
                    : Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a")));
        }, time,
            _ =>
            {
                Interlocked.Increment(ref identityCalls);
                return Task.FromResult(IdentityObservation(1));
            });
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var fresh = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Fresh, fresh[0].State);

        time.Advance(TimeSpan.FromMinutes(1));
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var rateLimited = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Fresh, rateLimited[0].State);

        time.Advance(KiroQuotaProvider.StaleAfter - TimeSpan.FromMinutes(1));
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var stale = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Stale, stale[0].State);
        Assert.AreEqual(ObservedAt, stale[0].ObservedAt);
        Assert.AreEqual("rate_limit_retry_suppressed", stale[0].ErrorCode);

        time.Advance(KiroQuotaProvider.MaximumCacheAge - KiroQuotaProvider.StaleAfter);
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var expired = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Unavailable, expired[0].State);
        Assert.IsNull(expired[0].Value);
        Assert.AreEqual(ObservedAt, expired[0].ObservedAt);
        Assert.AreEqual("cache_expired", expired[0].ErrorCode);

        time.Advance(TimeSpan.FromHours(1));
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var stillSuppressed = await readers.ReadNextBatchAsync();
        Assert.AreEqual(2, usageCalls);
        Assert.AreEqual(7, identityCalls);
        Assert.AreEqual(MetricState.Unavailable, stillSuppressed[0].State);
        Assert.AreEqual("cache_expired", stillSuppressed[0].ErrorCode);
    }

    [TestMethod]
    public async Task ProviderClearsCachedIdentityOnMonitorChangeWithoutResumingSuppressedUsage()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var identityCalls = 0;
        var usageCalls = 0;
        using var provider = CreateProvider(registry, bus, _ => Task.FromResult(
            Interlocked.Increment(ref usageCalls) == 1
                ? Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))
                : KiroAcquisition.RetrySuppressed()), TimeProvider.System, _ =>
        {
            var observation = Interlocked.Increment(ref identityCalls) < 3
                ? IdentityObservation(1)
                : IdentityObservation(2);
            return Task.FromResult(observation);
        });
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        _ = await readers.ReadNextBatchAsync();
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var identityChanged = await readers.ReadNextBatchAsync();
        await provider.PollOnceAsync();

        Assert.AreEqual(2, usageCalls);
        Assert.AreEqual(6, identityCalls);
        Assert.IsTrue(identityChanged.All(static snapshot => snapshot.Value is null));
        Assert.IsTrue(identityChanged.All(static snapshot => snapshot.ErrorCode == "rate_limit_retry_suppressed"));
    }

    [TestMethod]
    public async Task RunPublishesStoppedSnapshotsWhileUsageRetriesAreSuppressed()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var usageCalls = 0;
        using var provider = CreateProvider(registry, bus, _ =>
        {
            Interlocked.Increment(ref usageCalls);
            return Task.FromResult(KiroAcquisition.RetrySuppressed());
        }, readIdentity: _ => Task.FromResult(IdentityObservation(1)));
        using var stop = new CancellationTokenSource();
        await using var readers = SubscribeAll(bus);
        var stoppedReaders = readers.ReadUntilStoppedAsync(
            Enumerable.Range(0, 4).Select(static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray());
        var run = provider.RunAsync(stop.Token);
        await WaitForAcquisitionSuppressionAsync(provider, run);
        stop.Cancel();

        var stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(3));
        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(1, usageCalls);
        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    [TestMethod]
    public async Task RunCancelsPendingAcquisitionAndPublishesStoppedState()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(registry, bus, async cancellationToken =>
        {
            readStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return KiroAcquisition.Error("unreachable");
        });
        using var stop = new CancellationTokenSource();
        await using var readers = SubscribeAll(bus);
        var stoppedReaders = readers.ReadUntilStoppedAsync(
            Enumerable.Range(0, 4).Select(static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray());
        var run = provider.RunAsync(stop.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();

        var stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(2));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    [TestMethod]
    public async Task RunInvalidatesBlockedPublicationAtPlanResetDuringAcquisition()
    {
        var resetAt = ObservedAt.AddSeconds(3).ToUnixTimeSeconds();
        await AssertRunInvalidatesPendingPublicationAsync(
            "ai.kiro.credits.included.remaining",
            CompleteUsage("synthetic-user-a", "synthetic-account-a", resetAt),
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), 2, null,
            MetricState.Unavailable, "reset_elapsed", null);
    }

    [TestMethod]
    public async Task RunInvalidatesBlockedPublicationAtBonusExpiryDuringAcquisition()
    {
        var expiry = ObservedAt.AddSeconds(2).ToString("O", CultureInfo.InvariantCulture);
        var payload = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("2030-01-02T00:00:00Z", expiry, StringComparison.Ordinal);
        await AssertRunInvalidatesPendingPublicationAsync(
            "ai.kiro.credits.bonus.remaining", payload,
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), 2, null,
            MetricState.Stale, null, 0m);
    }

    [TestMethod]
    public async Task RunInvalidatesBlockedPublicationAtHardCacheExpiryDuringAcquisition()
    {
        await AssertRunInvalidatesPendingPublicationAsync(
            "ai.kiro.credits.included.remaining",
            CompleteUsage("synthetic-user-a", "synthetic-account-a"),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.Zero, 3, 2,
            MetricState.Unavailable, "cache_expired", null);
    }

    [TestMethod]
    public async Task RunBoundsShutdownWhenUsageDependencyIgnoresCancellationAndDiscardsLateValue()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<KiroAcquisition>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(registry, bus, async _ =>
        {
            readStarted.TrySetResult(true);
            return await releaseRead.Task.ConfigureAwait(false);
        });
        using var stop = new CancellationTokenSource();
        await using var readers = SubscribeAll(bus);
        var stoppedReaders = readers.ReadUntilStoppedAsync(
            Enumerable.Range(0, 4).Select(static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray());
        var run = provider.RunAsync(stop.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();

        var stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(3));
        Exception? failure = null;
        try { await run.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception exception) { failure = exception; }

        Assert.IsNotNull(failure);
        Assert.IsTrue(Flatten(failure).Any(static exception => exception is TimeoutException));
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));

        var afterStopped = readers.ReadNextBatchAsync();
        releaseRead.TrySetResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a")));
        await Task.Delay(100);
        Assert.IsFalse(afterStopped.IsCompleted, "A late usage result was published after shutdown.");
    }

    [TestMethod]
    public async Task CliStderrObservationHasAnActualDeadlineAndObservesLateFaults()
    {
        var unfinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = TimeProvider.System.GetTimestamp();

        var failure = await KiroAcpUsageClient.ObserveAsync(unfinished.Task, TimeSpan.FromMilliseconds(50));

        Assert.IsInstanceOfType<TimeoutException>(failure);
        Assert.IsTrue(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
        unfinished.SetException(new InvalidOperationException("synthetic late cleanup fault"));
        await Task.Delay(25);
    }

    [TestMethod]
    public async Task ProviderPreservesOriginalObservationWhileStaleThenExpiresMonotonicCache()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt);
        var calls = 0;
        using var provider = CreateProvider(registry, bus, _ => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))
                : KiroAcquisition.Error("request_timeout")), time);
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var fresh = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Fresh, fresh[0].State);

        time.Advance(KiroQuotaProvider.StaleAfter);
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var stale = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Stale, stale[0].State);
        Assert.AreEqual(87.25m, Decimal(stale[0]));
        Assert.AreEqual(ObservedAt, stale[0].ObservedAt);
        Assert.AreEqual("request_timeout", stale[0].ErrorCode);

        time.Advance(KiroQuotaProvider.MaximumCacheAge - KiroQuotaProvider.StaleAfter);
        await provider.PublishOnceAsync();
        var expired = await readers.ReadNextBatchAsync();
        Assert.AreEqual(MetricState.Unavailable, expired[0].State);
        Assert.IsNull(expired[0].Value);
        Assert.AreEqual(ObservedAt, expired[0].ObservedAt);
        Assert.AreEqual("cache_expired", expired[0].ErrorCode);
    }

    [TestMethod]
    public async Task ProviderInvalidatesIncludedCreditsAtPlanResetCrossing()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt);
        var resetAt = ObservedAt.AddMinutes(1);
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a",
                resetAt.ToUnixTimeSeconds()))), time);
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        _ = await readers.ReadNextBatchAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await provider.PublishOnceAsync();
        var reset = await readers.ReadNextBatchAsync();

        Assert.AreEqual(MetricState.Unavailable, reset[0].State);
        Assert.AreEqual("reset_elapsed", reset[0].ErrorCode);
        Assert.AreEqual(ObservedAt, reset[0].ObservedAt);
        Assert.IsNull(reset[0].Value);
        Assert.AreEqual(MetricState.Unavailable, reset[1].State);
        Assert.AreEqual("reset_elapsed", reset[1].ErrorCode);
        Assert.AreEqual(MetricState.Fresh, reset[2].State);
    }

    [TestMethod]
    public async Task ProviderRecalculatesBonusCreditsAtAllocationExpiry()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt);
        var json = CompleteUsage("synthetic-user-a", "synthetic-account-a")
            .Replace("2030-01-02T00:00:00Z", "2026-10-01T08:01:00Z");
        using var provider = CreateProvider(registry, bus, _ => Task.FromResult(Ready(json)), time);
        await using var readers = SubscribeAll(bus);
        readers.Start();

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        _ = await readers.ReadNextBatchAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await provider.PublishOnceAsync();
        var expired = await readers.ReadNextBatchAsync();

        Assert.AreEqual(MetricState.Stale, expired[3].State);
        Assert.AreEqual(0m, Decimal(expired[3]));
        Assert.AreEqual(ObservedAt, expired[3].ObservedAt);
    }

    [TestMethod]
    public async Task ProviderSerializesLateAcquisitionsAndPublishesTheLatestIdentity()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var provider = CreateProvider(registry, bus, async cancellationToken =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"));
            }
            return Ready(CompleteUsage("synthetic-user-b", "synthetic-account-b")
                .Replace("\"currentUsageWithPrecision\":\"12.75\"",
                    "\"currentUsageWithPrecision\":\"50.00\""));
        });
        await using var readers = SubscribeAll(bus);
        readers.Start();

        var first = provider.PollOnceAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = provider.PollOnceAsync();
        releaseFirst.TrySetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await provider.PublishOnceAsync();
        var snapshots = await readers.ReadNextBatchAsync();

        Assert.AreEqual(2, calls);
        Assert.AreEqual(50m, Decimal(snapshots[0]));
    }

    [TestMethod]
    public async Task ProviderCancelsPendingPublicationWhenIdentityChanges()
    {
        const string includedId = "ai.kiro.credits.included.remaining";
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        var calls = 0;
        using var provider = CreateProvider(registry, bus, _ => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))
                : Ready(CompleteUsage("synthetic-user-b", "synthetic-account-b")
                    .Replace("\"currentUsageWithPrecision\":\"12.75\"",
                        "\"currentUsageWithPrecision\":\"50.00\""))));
        using var stop = new CancellationTokenSource();
        await using var reader = bus.Subscribe(new MetricId(includedId), stop.Token).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(new MetricSnapshot(new MetricId(includedId), null, ObservedAt,
            MetricState.Unavailable, MetricConfidence.Unknown, "test"));
        await initialRead.WaitAsync(TimeSpan.FromSeconds(2));

        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();
        var pendingPublication = provider.PublishOnceAsync();
        await WaitForActivePublicationAsync(provider, pendingPublication);
        await provider.PollOnceAsync();
        await pendingPublication.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(87.25m, Decimal(reader.Current));
        var publishNewIdentity = provider.PublishOnceAsync();
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await publishNewIdentity;
        Assert.AreEqual(50m, Decimal(reader.Current));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task RunBoundsStoppedPublicationWhenSubscriberQueueIsFull()
    {
        const string includedId = "ai.kiro.credits.included.remaining";
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))));
        using var stop = new CancellationTokenSource();
        using var subscriptionStop = new CancellationTokenSource();
        await using var reader = bus.Subscribe(new MetricId(includedId), subscriptionStop.Token).GetAsyncEnumerator();
        var initialRead = reader.MoveNextAsync().AsTask();
        await bus.PublishAsync(new MetricSnapshot(new MetricId(includedId), null, ObservedAt,
            MetricState.Unavailable, MetricConfidence.Unknown, "test"));
        await initialRead.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.PollOnceAsync();
        await provider.PublishOnceAsync();

        var run = provider.RunAsync(stop.Token);
        await Task.Delay(25);
        var started = TimeProvider.System.GetTimestamp();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.IsTrue(elapsed < TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RunPublishesStoppedSnapshotsAfterWorkerFaults()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt) { ThrowOnGetTimestamp = true };
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))), time);
        await using var readers = SubscribeAll(bus);
        var stoppedReaders = readers.ReadUntilStoppedAsync(
            Enumerable.Range(0, 4).Select(static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray());
        var run = provider.RunAsync(CancellationToken.None);

        var stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(3));
        Exception? failure = null;
        try { await run; }
        catch (Exception exception) { failure = exception; }

        Assert.IsNotNull(failure);
        Assert.IsTrue(Flatten(failure).Any(static exception => exception is InvalidOperationException));
        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    [TestMethod]
    [DataRow("usage")]
    [DataRow("identity")]
    public async Task RunPreservesUnexpectedAcquisitionCleanupFailuresAfterStopped(string failingDependency)
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        var expectedFailure = new InvalidOperationException($"Synthetic {failingDependency} cleanup failure.");
        Func<CancellationToken, Task<KiroAcquisition>> readUsage = failingDependency == "usage"
            ? _ => Task.FromException<KiroAcquisition>(expectedFailure)
            : _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a")));
        Func<CancellationToken, Task<KiroIdentityObservation>> readIdentity = failingDependency == "identity"
            ? _ => Task.FromException<KiroIdentityObservation>(expectedFailure)
            : _ => Task.FromResult(IdentityObservation(1));
        using var provider = CreateProvider(registry, bus, readUsage, readIdentity: readIdentity);
        await using var readers = SubscribeAll(bus);
        var stoppedReaders = readers.ReadUntilStoppedAsync(
            Enumerable.Range(0, 4).Select(static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray());
        var run = provider.RunAsync(CancellationToken.None);

        var stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(3));
        Exception? runFailure = null;
        try { await run; }
        catch (Exception exception) { runFailure = exception; }

        Assert.IsNotNull(runFailure);
        Assert.IsTrue(Flatten(runFailure).Any(exception => ReferenceEquals(exception, expectedFailure)));
        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    [TestMethod]
    public async Task RunPreservesStoppedPublicationFailureAfterBoundedAttempt()
    {
        var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))));
        bus.Dispose();

        Exception? failure = null;
        try { await provider.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception exception) { failure = exception; }

        Assert.IsNotNull(failure);
        Assert.IsTrue(Flatten(failure).Any(static exception => exception is ObjectDisposedException));
    }

    [TestMethod]
    public async Task RunPublishesStoppedSnapshotsAfterCancellation()
    {
        using var bus = new MetricBus(8);
        var registry = new MetricRegistry();
        using var provider = CreateProvider(registry, bus,
            _ => Task.FromResult(Ready(CompleteUsage("synthetic-user-a", "synthetic-account-a"))));
        using var stop = new CancellationTokenSource();
        await using var readers = SubscribeAll(bus);
        readers.Start();
        var freshSeen = Enumerable.Range(0, 4)
            .Select(static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var stoppedReaders = readers.ReadUntilStoppedAsync(freshSeen);
        var run = provider.RunAsync(stop.Token);

        MetricSnapshot[] stopped;
        try
        {
            await Task.WhenAll(freshSeen.Select(static signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            stopped = await stoppedReaders.WaitAsync(TimeSpan.FromSeconds(5));
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.AreEqual(4, stopped.Length);
        Assert.IsTrue(stopped.All(static snapshot => snapshot.State == MetricState.Stopped));
    }

    private static KiroParseResult Parse(string json) => KiroQuotaParser.Parse(
        Encoding.UTF8.GetBytes(json), IdentityKey, ObservedAt);

    private static KiroQuotaProvider CreateProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<KiroAcquisition>> readUsage, TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<KiroIdentityObservation>>? readIdentity = null,
        TimeSpan? pollInterval = null, TimeSpan? staleAfter = null, TimeSpan? maximumCacheAge = null) =>
        new(registry, bus, readUsage, timeProvider ?? TimeProvider.System,
            readIdentity ?? (_ => Task.FromResult(IdentityObservation(1))),
            pollInterval, staleAfter, maximumCacheAge);

    private static async Task AssertRunInvalidatesPendingPublicationAsync(string metricId, string usagePayload,
        TimeSpan staleAfter, TimeSpan maximumCacheAge, TimeSpan transitionAfter, int expectedStateVersionDelta,
        int? intermediateStateVersionDelta,
        MetricState expectedState, string? expectedErrorCode, decimal? expectedValue)
    {
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        var time = new MutableTimeProvider(ObservedAt, manualTimers: true);
        var pollInterval = TimeSpan.FromMilliseconds(10);
        var usageCalls = 0;
        var delayedUsageStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(registry, bus, async cancellationToken =>
        {
            if (Interlocked.Increment(ref usageCalls) == 1) return Ready(usagePayload);
            delayedUsageStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return KiroAcquisition.Error("unreachable");
        }, timeProvider: time, readIdentity: _ => Task.FromResult(IdentityObservation(1)),
            pollInterval: pollInterval, staleAfter: staleAfter,
            maximumCacheAge: maximumCacheAge);
        using var stop = new CancellationTokenSource();
        await using var readers = SubscribeAll(bus);
        var initialStateVersion = ReadStateVersion(provider);
        readers.Start();
        foreach (var id in TestMetricIds)
            await bus.PublishAsync(new MetricSnapshot(new MetricId(id), null, ObservedAt,
                MetricState.Unavailable, MetricConfidence.Unknown, "test_registration", "test_registration"));
        var registered = await readers.ReadNextBatchAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(registered.All(static snapshot => snapshot.ErrorCode == "test_registration"));
        foreach (var id in TestMetricIds)
            await bus.PublishAsync(new MetricSnapshot(new MetricId(id), null, ObservedAt,
                MetricState.Unavailable, MetricConfidence.Unknown, "test_marker", "test_marker"));
        var run = provider.RunAsync(stop.Token);

        try
        {
            await WaitForStateVersionAsync(provider, initialStateVersion + 1, TimeSpan.FromSeconds(2));
            await time.WaitForTimerDueWithinAsync(pollInterval, TimeSpan.FromSeconds(2));
            time.Advance(pollInterval);
            await delayedUsageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(2, usageCalls);
            var blockedPublication = await WaitForActivePublicationAsync(provider, run);
            if (intermediateStateVersionDelta is int intermediateDelta)
            {
                time.Advance(staleAfter);
                await WaitForStateVersionAsync(provider, initialStateVersion + intermediateDelta,
                    TimeSpan.FromSeconds(5));
                await WaitForPublicationCancellationAsync(blockedPublication);
                blockedPublication = await WaitForDifferentActivePublicationAsync(provider, run, blockedPublication);
                time.Advance(maximumCacheAge - staleAfter);
            }
            else time.Advance(transitionAfter);
            await WaitForStateVersionAsync(provider, initialStateVersion + expectedStateVersionDelta,
                TimeSpan.FromSeconds(5));
            await WaitForPublicationCancellationAsync(blockedPublication);

            var markers = await readers.ReadNextBatchAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(markers.All(static snapshot => snapshot.ErrorCode == "test_marker"));
            var transitioned = await readers.ReadNextBatchAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var target = transitioned[Array.IndexOf(TestMetricIds, metricId)];
            Assert.AreEqual(expectedState, target.State);
            Assert.AreEqual(expectedErrorCode, target.ErrorCode);
            if (expectedValue.HasValue) Assert.AreEqual(expectedValue.Value, Decimal(target));
            else Assert.IsNull(target.Value);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static IEnumerable<Exception> Flatten(Exception exception) => exception is AggregateException aggregate
        ? aggregate.Flatten().InnerExceptions
        : [exception];

    private static SubscriptionBatch SubscribeAll(MetricBus bus) => new(bus);

    private static KiroAcquisition Ready(string json) =>
        KiroAcquisition.Ready(Encoding.UTF8.GetBytes(json));

    private static KiroIdentityObservation IdentityObservation(byte value) =>
        KiroIdentityObservation.Ready(Enumerable.Repeat(value, 32).ToArray());

    private static string CompleteUsage(string userId, string accountId, long resetAt = 1893456000) => $$"""
        {
          "userInfo":{"userId":"{{userId}}","accountId":"{{accountId}}","profileArn":"arn:aws:synthetic:profile/test"},
          "usageBreakdownList":[{"resourceType":"CREDIT","currentUsageWithPrecision":"12.75","usageLimitWithPrecision":"100.00"}],
          "subscriptionInfo":{"nextDateReset":{{resetAt}}},
          "addOnMetadata":{"addOnTotalLimit":"40.00","addOnTotalUsage":"10.25"},
          "freeTrialInfo":{"bonuses":[
            {"currentUsage":"1.25","usageLimit":"10.00","expiresAt":"2030-01-02T00:00:00Z"},
            {"currentUsage":"2","usageLimit":"3","expiresAt":"2020-01-01T00:00:00Z"}
          ]}
        }
        """;

    private static decimal Decimal(KiroMetricPoint point)
    {
        Assert.IsTrue(point.Value.HasValue);
        return point.Value!.Value.AsDecimal();
    }

    private static decimal Decimal(MetricSnapshot snapshot)
    {
        Assert.IsTrue(snapshot.Value.HasValue);
        return snapshot.Value!.Value.AsDecimal();
    }

    private static void AssertValuelessError(KiroMetricPoint point, string errorCode)
    {
        Assert.AreEqual(MetricState.Error, point.State);
        Assert.IsNull(point.Value);
        Assert.AreEqual(errorCode, point.ErrorCode);
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string script)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var startInfo = new ProcessStartInfo(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0",
            "powershell.exe"));
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        startInfo.Environment.Clear();
        startInfo.Environment["SystemRoot"] = windows;
        startInfo.Environment["WINDIR"] = windows;
        startInfo.Environment["TEMP"] = Path.GetTempPath();
        startInfo.Environment["TMP"] = Path.GetTempPath();
        startInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return startInfo;
    }

    private static (ProcessStartInfo StartInfo, string ScriptPath) CreateCommandStartInfo(string script)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"cerith-kiro-acp-{Guid.NewGuid():N}.cmd");
        var command = script.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(scriptPath, command, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo(Path.Combine(windows, "System32", "cmd.exe"));
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/Q");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment.Clear();
        startInfo.Environment["SystemRoot"] = windows;
        startInfo.Environment["WINDIR"] = windows;
        startInfo.Environment["TEMP"] = Path.GetTempPath();
        startInfo.Environment["TMP"] = Path.GetTempPath();
        startInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return (startInfo, scriptPath);
    }

    private static async Task<CancellationTokenSource> WaitForActivePublicationAsync(KiroQuotaProvider provider,
        Task publication)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (GetActivePublication(provider)?.IsCancellationRequested != false &&
            !publication.IsCompleted && TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            await Task.Delay(1);
        var active = GetActivePublication(provider);
        Assert.IsNotNull(active);
        Assert.IsFalse(active.IsCancellationRequested);
        Assert.IsFalse(publication.IsCompleted);
        return active;
    }

    private static async Task<CancellationTokenSource> WaitForDifferentActivePublicationAsync(
        KiroQuotaProvider provider, Task run, CancellationTokenSource previous)
    {
        var started = TimeProvider.System.GetTimestamp();
        CancellationTokenSource? active;
        do
        {
            active = GetActivePublication(provider);
            if (active is not null && !ReferenceEquals(active, previous) && !active.IsCancellationRequested) return active;
            await Task.Delay(1);
        } while (!run.IsCompleted && TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
        Assert.Fail("The provider did not start a new publication after invalidating the previous one.");
        throw new AssertFailedException("Unreachable.");
    }

    private static async Task WaitForPublicationCancellationAsync(CancellationTokenSource publication)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!publication.IsCancellationRequested &&
            TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            await Task.Delay(1);
        Assert.IsTrue(publication.IsCancellationRequested,
            "The obsolete publication was not canceled before the subscriber queue drained.");
    }

    private static CancellationTokenSource? GetActivePublication(KiroQuotaProvider provider)
    {
        var field = typeof(KiroQuotaProvider).GetField("_activePublicationStop",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return field.GetValue(provider) as CancellationTokenSource;
    }

    private static long ReadStateVersion(KiroQuotaProvider provider)
    {
        var field = typeof(KiroQuotaProvider).GetField("_stateVersion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (long)field.GetValue(provider)!;
    }

    private static async Task WaitForStateVersionAsync(KiroQuotaProvider provider, long target, TimeSpan timeout)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (ReadStateVersion(provider) < target &&
            TimeProvider.System.GetElapsedTime(started) < timeout)
            await Task.Delay(1);
        Assert.IsTrue(ReadStateVersion(provider) >= target,
            $"Expected provider state version to reach {target}, actual {ReadStateVersion(provider)}.");
    }

    private static async Task WaitForAcquisitionSuppressionAsync(KiroQuotaProvider provider, Task run)
    {
        var field = typeof(KiroQuotaProvider).GetField("_acquisitionSuppressed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var started = TimeProvider.System.GetTimestamp();
        while (field.GetValue(provider) is not true && !run.IsCompleted &&
            TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            await Task.Delay(1);
        Assert.AreEqual(true, field.GetValue(provider));
        Assert.IsFalse(run.IsCompleted);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow, bool manualTimers = false) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public bool ThrowOnGetTimestamp { get; init; }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            if (ThrowOnGetTimestamp) throw new InvalidOperationException("Synthetic time source failure.");
            lock (_gate) return _timestamp;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!manualTimers) return new Timer(callback, state, dueTime, period);
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.ScheduleLocked(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
                foreach (var timer in _timers)
                {
                    if (timer._disposed || timer._dueTimestamp > _timestamp) continue;
                    callbacks.Add((timer.Callback, timer.State));
                    timer._dueTimestamp = timer._periodTicks > 0
                        ? AddSaturating(_timestamp, timer._periodTicks)
                        : long.MaxValue;
                }
            }
            foreach (var (callback, state) in callbacks) callback(state);
        }

        public async Task WaitForTimerDueWithinAsync(TimeSpan dueWithin, TimeSpan timeout)
        {
            var started = TimeProvider.System.GetTimestamp();
            while (TimeProvider.System.GetElapsedTime(started) < timeout)
            {
                lock (_gate)
                {
                    var latestDue = AddSaturating(_timestamp, dueWithin.Ticks);
                    if (_timers.Exists(timer => !timer._disposed && timer._dueTimestamp <= latestDue)) return;
                }
                await Task.Delay(1).ConfigureAwait(false);
            }
            throw new TimeoutException("The requested provider timer was not scheduled.");
        }

        private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (timer._disposed) return false;
                timer.ScheduleLocked(dueTime, period);
                return true;
            }
        }

        private void DisposeTimer(ManualTimer timer)
        {
            lock (_gate)
            {
                timer._disposed = true;
                timer._dueTimestamp = long.MaxValue;
            }
        }

        private static long AddSaturating(long left, long right) =>
            right > long.MaxValue - left ? long.MaxValue : left + right;

        private sealed class ManualTimer(MutableTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback { get; } = callback;
            internal object? State { get; } = state;
            internal long _dueTimestamp = long.MaxValue;
            internal long _periodTicks;
            internal bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.ChangeTimer(this, dueTime, period);

            public void Dispose() => owner.DisposeTimer(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void ScheduleLocked(TimeSpan dueTime, TimeSpan period)
            {
                var delayTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : Math.Max(0, dueTime.Ticks);
                _dueTimestamp = delayTicks == long.MaxValue
                    ? long.MaxValue
                    : AddSaturating(owner._timestamp, delayTicks);
                _periodTicks = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            }
        }
    }

    private sealed class SubscriptionBatch : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<MetricSnapshot>[] _readers;
        private readonly CancellationTokenSource _stop = new();
        private Task<bool>[]? _pendingMoves;
        private Task<MetricSnapshot[]>? _readUntilStopped;

        public SubscriptionBatch(MetricBus bus)
        {
            _readers = TestMetricIds.Select(id => bus.Subscribe(new MetricId(id), _stop.Token).GetAsyncEnumerator()).ToArray();
        }

        public void Start() => _pendingMoves ??= _readers.Select(static reader => reader.MoveNextAsync().AsTask()).ToArray();

        public async Task<MetricSnapshot[]> ReadNextBatchAsync()
        {
            Start();
            var moved = await Task.WhenAll(_pendingMoves!).ConfigureAwait(false);
            Assert.IsTrue(moved.All(static hasValue => hasValue));
            var snapshots = _readers.Select(static reader => reader.Current).ToArray();
            _pendingMoves = null;
            return snapshots;
        }

        public Task<MetricSnapshot[]> ReadUntilStoppedAsync(TaskCompletionSource<bool>[] freshSeen)
        {
            Start();
            _readUntilStopped = ReadUntilStoppedCoreAsync(freshSeen);
            return _readUntilStopped;
        }

        private async Task<MetricSnapshot[]> ReadUntilStoppedCoreAsync(TaskCompletionSource<bool>[] freshSeen)
        {
            var tasks = _readers.Select(async (reader, index) =>
            {
                var move = _pendingMoves![index];
                while (await move.ConfigureAwait(false))
                {
                    var snapshot = reader.Current;
                    if (snapshot.State == MetricState.Fresh) freshSeen[index].TrySetResult(true);
                    if (snapshot.State == MetricState.Stopped) return snapshot;
                    move = reader.MoveNextAsync().AsTask();
                }
                throw new AssertFailedException("Metric subscription ended before Stopped was published.");
            }).ToArray();
            var snapshots = await Task.WhenAll(tasks).ConfigureAwait(false);
            _pendingMoves = null;
            return snapshots;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_readUntilStopped is not null)
                await IgnoreReadTerminationAsync(_readUntilStopped).ConfigureAwait(false);
            if (_pendingMoves is not null)
                await IgnoreReadTerminationAsync(Task.WhenAll(_pendingMoves)).ConfigureAwait(false);
            foreach (var reader in _readers) await reader.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }

        private static async Task IgnoreReadTerminationAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (AssertFailedException) { return; }
        }
    }
}
