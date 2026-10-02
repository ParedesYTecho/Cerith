#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.ArkAgentPlan;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.ArkAgentPlan;

[TestClass]
public sealed class ArkAgentPlanProviderTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);
    private static readonly string[] MetricIds =
    [
        "ai.ark.agent_plan.5h.used",
        "ai.ark.agent_plan.5h.total",
        "ai.ark.agent_plan.5h.used_percent",
        "ai.ark.agent_plan.5h.reset",
        "ai.ark.agent_plan.weekly.used",
        "ai.ark.agent_plan.weekly.total",
        "ai.ark.agent_plan.weekly.used_percent",
        "ai.ark.agent_plan.weekly.reset",
        "ai.ark.agent_plan.monthly.used",
        "ai.ark.agent_plan.monthly.total",
        "ai.ark.agent_plan.monthly.used_percent",
        "ai.ark.agent_plan.monthly.reset"
    ];

    [TestMethod]
    public async Task PublishesPersonalCliPeriodsAndIgnoresBackendDailyWhenWhoamiOmitsProfile()
    {
        using var harness = new Harness(_ => Task.FromResult(ArkCliResult.Success(Encoding.UTF8.GetBytes(
            """
            {
              "viewer": {
                "auth_method": "sso",
                "is_root": false,
                "user_id": "user-A",
                "account_id": "account-A",
                "user_name": "private-user",
                "profile": "private-profile",
                "tenant": "tenant-A",
                "region": "cn-beijing",
                "project_name": "default"
              },
              "items": [
                {
                  "product": "agent-plan-team",
                  "subscribed": true,
                  "periods": [{"label": "weekly", "used": 900, "total": 1000, "percent": 90}]
                },
                {
                  "product": "agent-plan",
                  "subscribed": true,
                  "periods": [
                    {"label": "5h", "used": 2.25, "total": 10, "percent": 22.5, "reset_at": "2026-10-02T18:00:00+08:00"},
                    {"label": "weekly", "used": 8, "total": 40, "percent": 20},
                    {"label": "monthly", "used": 18.5, "total": 100, "percent": 18.5, "reset_at": "2026-11-01T00:00:00+08:00"},
                    {"label": "daily", "used": 999, "total": 1000, "percent": 99.9}
                  ]
                }
              ]
            }
            """))));

        var snapshots = await ReadAfterPollAsync(harness);

        Assert.AreEqual(12, snapshots.Length);
        CollectionAssert.AreEqual(MetricIds, snapshots.Select(snapshot => snapshot.MetricId.Value).ToArray());
        Assert.IsTrue(snapshots.Where((_, index) => index != 7).All(snapshot =>
            snapshot.State == MetricState.Fresh && snapshot.Confidence == MetricConfidence.Measured &&
            snapshot.Source == ArkAgentPlanCli.Source && snapshot.ObservedAt == ObservedAt));
        Assert.AreEqual(MetricValueKind.Decimal, snapshots[0].Value!.Value.Kind);
        Assert.AreEqual(2.25m, snapshots[0].Value!.Value.AsDecimal());
        Assert.AreEqual("AFP", FindDescriptor(harness.Registry, MetricIds[0]).Unit);
        Assert.AreEqual(10m, snapshots[1].Value!.Value.AsDecimal());
        Assert.AreEqual(22.5m, snapshots[2].Value!.Value.AsDecimal());
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.FromHours(8)),
            snapshots[3].Value!.Value.AsDateTimeOffset());
        Assert.IsNull(snapshots[7].Value);
        Assert.AreEqual(MetricState.Unavailable, snapshots[7].State);
    }

    [TestMethod]
    public void ParserRejectsMalformedPayloadAndUnverifiedViewerIdentity()
    {
        var malformed = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes("{"));
        Assert.IsTrue(malformed.Points.All(point =>
            point.State == MetricState.Error && point.ErrorCode == "schema_invalid"));

        var missingViewer = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """{"items":[{"product":"agent-plan","subscribed":true}]}"""));
        Assert.IsTrue(missingViewer.Points.All(point =>
            point.State == MetricState.Error && point.ErrorCode == "identity_unverified"));

        var missingSubuser = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {"viewer":{"auth_method":"sso","is_root":false,"account_id":"account-A"},
             "items":[{"product":"agent-plan","subscribed":true}]}
            """));
        Assert.IsTrue(missingSubuser.Points.All(point =>
            point.State == MetricState.Error && point.ErrorCode == "identity_unverified"));

        var missingAccount = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {"viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","region":"cn-beijing","project_name":"default"},
             "items":[{"product":"agent-plan","subscribed":true}]}
            """));
        Assert.IsTrue(missingAccount.Points.All(point =>
            point.State == MetricState.Error && point.ErrorCode == "identity_unverified"));
    }

    [TestMethod]
    public void MissingAndUnsubscribedPlanFieldsAreUnavailableRatherThanZero()
    {
        var missing = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A"},
              "items":[{"product":"agent-plan","subscribed":false}]
            }
            """));
        Assert.IsTrue(missing.Points.All(point =>
            point.State == MetricState.Unavailable && point.Value is null));

        var absentFields = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A"},
              "items":[{"product":"agent-plan","subscribed":true,"periods":[{"label":"5h"}]}]
            }
            """));
        Assert.IsTrue(absentFields.Points.Take(4).All(point =>
            point.State == MetricState.Unavailable && point.Value is null));

        var reportedZero = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A"},
              "items":[{"product":"agent-plan","subscribed":true,
                "periods":[{"label":"5h","used":0,"total":0,"percent":0}]}]
            }
            """));
        Assert.AreEqual(MetricState.Fresh, reportedZero.Points[0].State);
        Assert.AreEqual(0m, reportedZero.Points[0].Value!.Value.AsDecimal());
        Assert.AreEqual(MetricState.Fresh, reportedZero.Points[1].State);
        Assert.AreEqual(MetricState.Fresh, reportedZero.Points[2].State);
        Assert.AreEqual(MetricState.Unavailable, reportedZero.Points[3].State);
    }

    [TestMethod]
    public void ParserRejectsInvalidQuotaAndResetWithoutAnExplicitOffset()
    {
        var reading = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A"},
              "items":[{"product":"agent-plan","subscribed":true,"periods":[
                {"label":"5h","used":1,"total":10,"percent":101,"reset_at":"2026-10-02T18:00:00"}
              ]}]
            }
            """));
        Assert.AreEqual("invalid_quota", reading.Points[2].ErrorCode);
        Assert.AreEqual("invalid_reset", reading.Points[3].ErrorCode);
        Assert.IsTrue(reading.Points.Take(2).All(point => point.State == MetricState.Fresh));
    }

    [TestMethod]
    public void ViewerIdentityChangesWithDocumentedUserAndAccountScope()
    {
        var first = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {"viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A"},
             "items":[]}
            """));
        var second = ArkAgentPlanParser.Parse(Encoding.UTF8.GetBytes(
            """
            {"viewer":{"auth_method":"sso","is_root":false,"user_id":"user-B","account_id":"account-A"},
             "items":[]}
            """));
        Assert.AreNotEqual(first.Identity, second.Identity);
        Assert.AreEqual("user-A", first.Identity!.UserId);
        Assert.AreEqual("account-A", first.Identity.AccountId);
    }

    [TestMethod]
    public void CliProcessUsesOnlyThePinnedPackagePathAndReadOnlyCommands()
    {
        var executablePath = ArkAgentPlanCli.GetExecutablePath();
        Assert.IsNotNull(executablePath);
        Assert.IsTrue(executablePath.EndsWith(
            Path.Combine("npm", "node_modules", "@volcengine", "ark-cli", "bin", "arkcli-windows-amd64.exe"),
            StringComparison.OrdinalIgnoreCase));

        var originalBaseUrl = Environment.GetEnvironmentVariable("ARK_BASE_URL");
        var originalProfile = Environment.GetEnvironmentVariable("ARK_PROFILE");
        var originalApiKey = Environment.GetEnvironmentVariable("ARK_API_KEY");
        var proxyVariables = new[]
        {
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
            "http_proxy", "https_proxy", "all_proxy",
            "NO_PROXY", "no_proxy"
        };
        var originalProxyValues = proxyVariables.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(variable => variable, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase);
        try
        {
            Environment.SetEnvironmentVariable("ARK_BASE_URL", "https://untrusted.invalid");
            Environment.SetEnvironmentVariable("ARK_PROFILE", "synthetic-profile");
            Environment.SetEnvironmentVariable("ARK_API_KEY", "synthetic-key");
            foreach (var variable in proxyVariables)
                Environment.SetEnvironmentVariable(variable, "http://proxy.invalid:8080");
            var startInfo = ArkAgentPlanCli.CreateStartInfo(executablePath);
            var identityStartInfo = ArkAgentPlanCli.CreateIdentityStartInfo(executablePath);
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.IsTrue(startInfo.RedirectStandardInput);
            CollectionAssert.AreEqual(
                new[] { "usage", "plan", "--product", "agent-plan", "--format", "json" },
                startInfo.ArgumentList.ToArray());
            Assert.IsFalse(startInfo.Environment.ContainsKey("ARK_BASE_URL"));
            Assert.AreEqual("synthetic-profile", startInfo.Environment["ARK_PROFILE"]);
            Assert.IsFalse(startInfo.Environment.ContainsKey("ARK_API_KEY"));
            Assert.IsTrue(proxyVariables.All(variable => !startInfo.Environment.ContainsKey(variable)));
            CollectionAssert.AreEqual(new[] { "auth", "whoami", "--format", "json" },
                identityStartInfo.ArgumentList.ToArray());
            Assert.AreEqual("synthetic-profile", identityStartInfo.Environment["ARK_PROFILE"]);
            Assert.IsTrue(proxyVariables.All(variable => !identityStartInfo.Environment.ContainsKey(variable)));
            Assert.AreEqual("1", startInfo.Environment["ARKCLI_NO_UPDATE_NOTIFIER"]);
            Assert.AreEqual(64, ArkAuthenticodeVerifier.ExpectedSha256.Length);
            var rejectedPath = false;
            try
            {
                ArkAgentPlanCli.CreateStartInfo(Path.Combine(Path.GetTempPath(), "arkcli.exe"));
            }
            catch (ArgumentException)
            {
                rejectedPath = true;
            }
            Assert.IsTrue(rejectedPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARK_BASE_URL", originalBaseUrl);
            Environment.SetEnvironmentVariable("ARK_PROFILE", originalProfile);
            Environment.SetEnvironmentVariable("ARK_API_KEY", originalApiKey);
            foreach (var (variable, value) in originalProxyValues)
                Environment.SetEnvironmentVariable(variable, value);
        }
    }

    [TestMethod]
    public async Task BoundedCliReadersRejectOversizeAndHonorCancellation()
    {
        var bytes = await ArkAgentPlanCli.ReadBoundedOutputAsync(new MemoryStream([1, 2, 3]), 3,
            CancellationToken.None);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
        await ArkAgentPlanCli.DrainBoundedOutputAsync(new MemoryStream([1, 2, 3]), 3,
            CancellationToken.None);

        var tooLarge = false;
        try
        {
            await ArkAgentPlanCli.ReadBoundedOutputAsync(new MemoryStream([1, 2, 3, 4]), 3,
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            tooLarge = true;
        }
        Assert.IsTrue(tooLarge);

        var stderrTooLarge = false;
        try
        {
            await ArkAgentPlanCli.DrainBoundedOutputAsync(new MemoryStream([1, 2, 3, 4]), 3,
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            stderrTooLarge = true;
        }
        Assert.IsTrue(stderrTooLarge);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var wasCanceled = false;
        try
        {
            await ArkAgentPlanCli.ReadBoundedOutputAsync(new MemoryStream([1]), 3, canceled.Token);
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
        }
        Assert.IsTrue(wasCanceled);
    }

    [TestMethod]
    public void AuthenticodePublisherPolicyRequiresExpectedCommonNameAndOrganization()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=北京火山引擎科技有限公司, O=北京火山引擎科技有限公司"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expected = request.CreateSelfSigned(ObservedAt.AddMinutes(-1), ObservedAt.AddMinutes(1));
        Assert.IsTrue(ArkAuthenticodeVerifier.IsExpectedPublisher(expected));

        using var wrongName = new CertificateRequest(
            new X500DistinguishedName("CN=Other Publisher, O=北京火山引擎科技有限公司"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(ObservedAt.AddMinutes(-1), ObservedAt.AddMinutes(1));
        Assert.IsFalse(ArkAuthenticodeVerifier.IsExpectedPublisher(wrongName));
    }

    [TestMethod]
    public async Task CliTimeoutPublishesSanitizedErrorsWithoutValues()
    {
        using var harness = new Harness(_ =>
            Task.FromResult(ArkCliResult.Failure("timeout")));

        var snapshots = await ReadAfterPollAsync(harness);

        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error &&
            snapshot.Value is null && snapshot.ErrorCode == "timeout" &&
            snapshot.Confidence == MetricConfidence.Unknown));
    }

    [TestMethod]
    public async Task AcquisitionFailureDoesNotReplayPreviousAccountValues()
    {
        var payload = Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A"},
              "items":[{"product":"agent-plan","subscribed":true,
                "periods":[{"label":"5h","used":3,"total":10,"percent":30}]}]
            }
            """);
        var acquisitions = 0;
        using var harness = new Harness(_ => Task.FromResult(
            Interlocked.Increment(ref acquisitions) == 1
                ? ArkCliResult.Success(payload)
                : ArkCliResult.Failure("timeout")));

        var first = await ReadAfterPollAsync(harness);
        var second = await ReadAfterPollAsync(harness);

        Assert.AreEqual(MetricState.Fresh, first[0].State);
        Assert.AreEqual(3m, first[0].Value!.Value.AsDecimal());
        Assert.IsTrue(second.All(snapshot => snapshot.State == MetricState.Error &&
            snapshot.Value is null && snapshot.ErrorCode == "timeout"));
    }

    [TestMethod]
    public async Task DoesNotReplayQuotaValuesAcrossSequentialViewerIdentityChange()
    {
        var firstPayload = Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A"},
              "items":[{"product":"agent-plan","subscribed":true,"periods":[
                {"label":"5h","used":3,"total":10,"percent":30,"reset_at":"2026-10-02T18:00:00+08:00"},
                {"label":"weekly","used":8,"total":40,"percent":20,"reset_at":"2026-10-09T00:00:00+08:00"},
                {"label":"monthly","used":18,"total":100,"percent":18,"reset_at":"2026-11-01T00:00:00+08:00"}
              ]}]
            }
            """);
        var secondPayload = Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-B","account_id":"account-A"},
              "items":[{"product":"agent-plan","subscribed":true,"periods":[
                {"label":"5h","used":0,"total":20,"percent":0}
              ]}]
            }
            """);
        var firstIdentity = ArkAgentPlanParser.Parse(firstPayload).Identity!;
        var secondIdentity = ArkAgentPlanParser.Parse(secondPayload).Identity!;
        Assert.AreNotEqual(firstIdentity.UserId, secondIdentity.UserId);

        var acquisitions = 0;
        using var harness = new Harness(_ => Task.FromResult(
            ArkCliResult.Success(Interlocked.Increment(ref acquisitions) == 1 ? firstPayload : secondPayload)));

        var first = await ReadAfterPollAsync(harness);
        var second = await ReadAfterPollAsync(harness);

        Assert.AreEqual(3m, first[0].Value!.Value.AsDecimal());
        Assert.AreEqual(0m, second[0].Value!.Value.AsDecimal());
        Assert.AreEqual(20m, second[1].Value!.Value.AsDecimal());
        Assert.AreEqual(0m, second[2].Value!.Value.AsDecimal());
        Assert.IsTrue(second.Skip(3).All(snapshot =>
            snapshot.State == MetricState.Unavailable && snapshot.Value is null));
    }

    [TestMethod]
    public async Task WithholdsQuotaWhenPostAcquisitionAccountUserOrRouteDiffers()
    {
        var usagePayload = Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A","profile":"profile-A","region":"cn-beijing","project_name":"default"},
              "items":[{"product":"agent-plan","subscribed":true,"periods":[
                {"label":"5h","used":3,"total":10,"percent":30}
              ]}]
            }
            """);
        var currentIdentities = new[]
        {
            new ArkAgentPlanIdentity("sso", false, "user-A", "account-B", "cn-beijing", "default"),
            new ArkAgentPlanIdentity("sso", false, "user-B", "account-A", "cn-beijing", "default"),
            new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-shanghai", "default"),
            new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-beijing", "other"),
            new ArkAgentPlanIdentity("sts", false, "user-A", "account-A", "cn-beijing", "default")
        };

        foreach (var identity in currentIdentities)
        {
            using var harness = new Harness(_ => Task.FromResult(ArkCliResult.Success(usagePayload)),
                _ => CreateWhoamiPayload(identity));

            var snapshots = await ReadAfterPollAsync(harness);

            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error &&
                snapshot.Value is null && snapshot.ErrorCode == "identity_changed"));
        }
    }

    [TestMethod]
    public async Task WithholdsQuotaWhenPostAcquisitionIdentityIsMissingMalformedOrUnscoped()
    {
        var usagePayload = Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A","profile":"profile-A","region":"cn-beijing","project_name":"default"},
              "items":[{"product":"agent-plan","subscribed":true,"periods":[
                {"label":"5h","used":3,"total":10,"percent":30}
              ]}]
            }
            """);

        var currentIdentityWithoutAccount = CreateWhoamiPayload(new ArkAgentPlanIdentity(
            "sso", false, "user-A", null, "cn-beijing", "default"));
        var currentIdentityWithoutProject = CreateWhoamiPayload(new ArkAgentPlanIdentity(
            "sso", false, "user-A", "account-A", "cn-beijing", null));
        foreach (var identityPayload in new byte[]?[]
                 { null, Encoding.UTF8.GetBytes("{"), currentIdentityWithoutAccount, currentIdentityWithoutProject })
        {
            using var harness = new Harness(_ => Task.FromResult(ArkCliResult.Success(usagePayload)),
                _ => identityPayload);

            var snapshots = await ReadAfterPollAsync(harness);

            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Error &&
                snapshot.Value is null && snapshot.ErrorCode == "identity_unverified"));
        }
    }

    [TestMethod]
    public async Task ShutdownCompletesWhenSubscriberQueueIsFull()
    {
        using var harness = new Harness(_ =>
            Task.FromResult(ArkCliResult.Unavailable("not_subscribed")));
        await using var reader = harness.Bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        await harness.Provider.PollOnceAsync();
        Assert.IsTrue(await firstRead.WaitAsync(TimeSpan.FromSeconds(1)));
        await harness.Provider.PollOnceAsync();

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await harness.Provider.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task CancellationStopsProviderWithoutPublishingQuotaValues()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ArkCliResult.Failure("unreachable");
        });
        var readers = MetricIds.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pendingReads = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        using var stop = new CancellationTokenSource();
        try
        {
            var run = harness.Provider.RunAsync(stop.Token);
            await entered.Task;
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(pendingReads);
            Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Stopped &&
                reader.Current.Value is null && reader.Current.Confidence == MetricConfidence.Unknown));
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    private static MetricDescriptor FindDescriptor(MetricRegistry registry, string id)
    {
        Assert.IsTrue(registry.TryGet(new MetricId(id), out var descriptor));
        return descriptor!;
    }

    private static byte[] CreateWhoamiPayload(ArkAgentPlanIdentity identity) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            logged_in = true,
            auth_method = identity.AuthMethod,
            is_root = identity.IsRoot,
            user_id = identity.UserId,
            account_id = identity.AccountId,
            region = identity.Region,
            project_name = identity.ProjectName
        });

    private static byte[]? CreateWhoamiPayload(ArkCliResult usageResult)
    {
        if (usageResult.Payload is null) return null;
        var identity = ArkAgentPlanParser.Parse(usageResult.Payload).Identity;
        return identity is null ? null : CreateWhoamiPayload(identity);
    }

    private static async Task<MetricSnapshot[]> ReadAfterPollAsync(Harness harness)
    {
        var readers = MetricIds.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pendingReads = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        try
        {
            await harness.Provider.PollOnceAsync();
            await Task.WhenAll(pendingReads);
            return readers.Select(reader => reader.Current).ToArray();
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    private sealed class Harness : IDisposable
    {
        public MetricRegistry Registry { get; } = new();
        public MetricBus Bus { get; } = new(1);
        public FixedTimeProvider Time { get; } = new(ObservedAt);
        public ArkAgentPlanProvider Provider { get; }

        public Harness(Func<CancellationToken, Task<ArkCliResult>> acquire,
            Func<ArkCliResult, byte[]?>? currentIdentity = null) =>
            Provider = new ArkAgentPlanProvider(Registry, Bus, async token =>
            {
                var result = await acquire(token).ConfigureAwait(false);
                var identityPayload = currentIdentity is null
                    ? result.CurrentIdentityPayload ?? CreateWhoamiPayload(result)
                    : currentIdentity(result);
                return result with { CurrentIdentityPayload = identityPayload };
            }, Time);

        public void Dispose()
        {
            Provider.Dispose();
            Bus.Dispose();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => utcNow.UtcTicks;
    }
}
