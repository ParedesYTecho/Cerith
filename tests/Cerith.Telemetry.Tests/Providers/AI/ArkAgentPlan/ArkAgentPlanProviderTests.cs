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
        "ai.ark.agent_plan.daily.used",
        "ai.ark.agent_plan.daily.total",
        "ai.ark.agent_plan.daily.used_percent",
        "ai.ark.agent_plan.daily.reset",
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
    public async Task PublishesPersonalCliPeriodsAndProfilePinnedBackendDaily()
    {
        var usagePayload = Encoding.UTF8.GetBytes(
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
                  "tier": "medium",
                  "subscribed": true,
                  "periods": [
                    {"label": "5h", "used": 2.25, "total": 10, "percent": 22.5, "reset_at": "2026-10-02T18:00:00+08:00"},
                    {"label": "weekly", "used": 8, "total": 40, "percent": 20},
                    {"label": "monthly", "used": 18.5, "total": 100, "percent": 18.5, "reset_at": "2026-11-01T00:00:00+08:00"}
                  ]
                }
              ]
            }
            """);
        using var harness = new Harness(_ => Task.FromResult(ArkCliResult.SuccessWithDaily(
            usagePayload, CreateDailyPayload())));

        var snapshots = await ReadAfterPollAsync(harness);

        Assert.AreEqual(16, snapshots.Length);
        CollectionAssert.AreEqual(MetricIds, snapshots.Select(snapshot => snapshot.MetricId.Value).ToArray());
        Assert.AreEqual("private-profile", ArkAgentPlanParser.Parse(usagePayload).Identity!.Profile);
        Assert.IsTrue(snapshots.Where((_, index) => index != 11).All(snapshot =>
            snapshot.State == MetricState.Fresh && snapshot.Confidence == MetricConfidence.Measured &&
            snapshot.Source == ArkAgentPlanCli.Source && snapshot.ObservedAt == ObservedAt));
        Assert.AreEqual(MetricValueKind.Decimal, snapshots[0].Value!.Value.Kind);
        Assert.AreEqual(2.25m, snapshots[0].Value!.Value.AsDecimal());
        Assert.AreEqual("AFP", FindDescriptor(harness.Registry, MetricIds[0]).Unit);
        Assert.AreEqual(10m, snapshots[1].Value!.Value.AsDecimal());
        Assert.AreEqual(22.5m, snapshots[2].Value!.Value.AsDecimal());
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.FromHours(8)),
            snapshots[3].Value!.Value.AsDateTimeOffset());
        Assert.AreEqual(22.5m, snapshots[4].Value!.Value.AsDecimal());
        Assert.AreEqual(100m, snapshots[5].Value!.Value.AsDecimal());
        Assert.AreEqual(22.5m, snapshots[6].Value!.Value.AsDecimal());
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000),
            snapshots[7].Value!.Value.AsDateTimeOffset());
        Assert.IsNull(snapshots[11].Value);
        Assert.AreEqual(MetricState.Unavailable, snapshots[11].State);
    }

    [TestMethod]
    public void DailyParserPreservesMissingZeroAndOverQuotaSemantics()
    {
        var identity = new ArkAgentPlanIdentity(
            "sso", false, "synthetic-user", "synthetic-account", "cn-beijing", "default", "synthetic-profile");

        var missing = ArkAgentPlanParser.ParseDaily(CreateDailyPayload(includeDaily: false), identity, "Medium");
        Assert.IsTrue(missing.All(point => point.State == MetricState.Unavailable && point.Value is null));

        var zero = ArkAgentPlanParser.ParseDaily(CreateDailyPayload(quota: "0", used: "0", resetTime: null),
            identity, "Medium");
        Assert.AreEqual(0m, zero[0].Value!.Value.AsDecimal());
        Assert.AreEqual(0m, zero[1].Value!.Value.AsDecimal());
        Assert.AreEqual(MetricState.Unavailable, zero[2].State);
        Assert.AreEqual(MetricState.Unavailable, zero[3].State);

        var overQuota = ArkAgentPlanParser.ParseDaily(CreateDailyPayload(quota: "10", used: "12.5"),
            identity, "Medium");
        Assert.AreEqual(125m, overQuota[2].Value!.Value.AsDecimal());
    }

    [TestMethod]
    public void DailyParserRejectsWrongActionRegionTierAndMalformedFields()
    {
        var identity = new ArkAgentPlanIdentity(
            "sso", false, "synthetic-user", "synthetic-account", "cn-beijing", "default", "synthetic-profile");
        var wrongScopeResponses = new[]
        {
            (CreateDailyPayload(action: "GetSeatAFPUsage"), "source_unverified"),
            (CreateDailyPayload(region: "cn-shanghai"), "identity_unverified"),
            (CreateDailyPayload(planType: "Small"), "scope_mismatch")
        };

        foreach (var (payload, errorCode) in wrongScopeResponses)
        {
            var points = ArkAgentPlanParser.ParseDaily(payload, identity, "Medium");
            Assert.IsTrue(points.All(point => point.State == MetricState.Error && point.ErrorCode == errorCode));
        }

        var malformed = ArkAgentPlanParser.ParseDaily(CreateDailyPayload(quota: "NaN"), identity, "Medium");
        Assert.AreEqual("invalid_quota", malformed[1].ErrorCode);
        Assert.AreEqual("invalid_quota", malformed[2].ErrorCode);

        var underflow = ArkAgentPlanParser.ParseDaily(CreateDailyPayload(quota: "1e-50"), identity, "Medium");
        Assert.AreEqual("invalid_quota", underflow[1].ErrorCode);

        var apiError = ArkAgentPlanParser.ParseDaily(CreateDailyPayload(apiError: true), identity, "Medium");
        Assert.IsTrue(apiError.All(point => point.State == MetricState.Error && point.ErrorCode == "api_failed"));

        var invalidReset = ArkAgentPlanParser.ParseDaily(CreateDailyPayload(resetTime: long.MaxValue), identity, "Medium");
        Assert.AreEqual("invalid_reset", invalidReset[3].ErrorCode);
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
            var dailyStartInfo = ArkAgentPlanCli.CreateDailyStartInfo(executablePath, "synthetic-profile");
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.IsTrue(startInfo.RedirectStandardInput);
            CollectionAssert.AreEqual(
                new[] { "usage", "plan", "--product", "agent-plan", "--format", "json" },
                startInfo.ArgumentList.ToArray());
            Assert.IsFalse(startInfo.Environment.ContainsKey("ARK_BASE_URL"));
            Assert.AreEqual("synthetic-profile", startInfo.Environment["ARK_PROFILE"]);
            Assert.IsFalse(startInfo.Environment.ContainsKey("ARK_API_KEY"));
            Assert.IsTrue(proxyVariables.All(variable => !startInfo.Environment.ContainsKey(variable)));
            CollectionAssert.AreEqual(
                new[] { "api", "usage.get_afp_usage", "--params", "{}", "--format", "json", "--profile=synthetic-profile" },
                dailyStartInfo.ArgumentList.ToArray());
            Assert.IsFalse(dailyStartInfo.Environment.ContainsKey("ARK_BASE_URL"));
            Assert.IsFalse(dailyStartInfo.Environment.ContainsKey("ARK_API_KEY"));
            Assert.IsTrue(proxyVariables.All(variable => !dailyStartInfo.Environment.ContainsKey(variable)));
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
            var rejectedProfile = false;
            try
            {
                ArkAgentPlanCli.CreateDailyStartInfo(executablePath,
                    new string('x', ArkAgentPlanCli.MaxProfileNameLength + 1));
            }
            catch (ArgumentException)
            {
                rejectedProfile = true;
            }
            Assert.IsTrue(rejectedProfile);
            var rejectedControlProfile = false;
            try
            {
                ArkAgentPlanCli.CreateDailyStartInfo(executablePath, "invalid\0profile");
            }
            catch (ArgumentException)
            {
                rejectedControlProfile = true;
            }
            Assert.IsTrue(rejectedControlProfile);
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
    public async Task DailyCliFailureLeavesIndependentPlanWindowsUsable()
    {
        var usagePayload = Encoding.UTF8.GetBytes(
            """
            {
              "viewer":{"auth_method":"sso","is_root":false,"user_id":"user-A","account_id":"account-A","profile":"profile-A","region":"cn-beijing","project_name":"default"},
              "items":[{"product":"agent-plan","tier":"medium","subscribed":true,"periods":[
                {"label":"5h","used":3,"total":10,"percent":30},
                {"label":"weekly","used":8,"total":40,"percent":20},
                {"label":"monthly","used":18,"total":100,"percent":18}
              ]}]
            }
            """);
        var identity = ArkAgentPlanParser.Parse(usagePayload).Identity!;
        using var harness = new Harness(_ => Task.FromResult(ArkCliResult.Success(
            usagePayload, null, "cli_failed", false, CreateUsagePayload(identity))));

        var snapshots = await ReadAfterPollAsync(harness);

        Assert.AreEqual(MetricState.Fresh, snapshots[0].State);
        Assert.AreEqual(3m, snapshots[0].Value!.Value.AsDecimal());
        Assert.IsTrue(snapshots.Skip(4).Take(4).All(snapshot => snapshot.State == MetricState.Error &&
            snapshot.Value is null && snapshot.ErrorCode == "cli_failed"));
        Assert.AreEqual(MetricState.Fresh, snapshots[8].State);
        Assert.AreEqual(8m, snapshots[8].Value!.Value.AsDecimal());
        Assert.AreEqual(MetricState.Fresh, snapshots[12].State);
        Assert.AreEqual(18m, snapshots[12].Value!.Value.AsDecimal());
    }

    [TestMethod]
    public async Task UnsubscribedPlanRemainsUnavailableWhenScopeIsStable()
    {
        var identity = new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-beijing",
            "default", "profile-A");
        var payload = CreateUsagePayload(identity, subscribed: false);
        using var harness = new Harness(_ => Task.FromResult(ArkCliResult.Success(payload)));

        var snapshots = await ReadAfterPollAsync(harness);

        Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Unavailable &&
            snapshot.Value is null && snapshot.ErrorCode is null));
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
    public async Task WithholdsQuotaWhenPostAcquisitionScopeProfileOrPlanDiffers()
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
        var currentScopes = new[]
        {
            (new ArkAgentPlanIdentity("sso", false, "user-A", "account-B", "cn-beijing", "default", "profile-A"), "medium", true),
            (new ArkAgentPlanIdentity("sso", false, "user-B", "account-A", "cn-beijing", "default", "profile-A"), "medium", true),
            (new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-shanghai", "default", "profile-A"), "medium", true),
            (new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-beijing", "other", "profile-A"), "medium", true),
            (new ArkAgentPlanIdentity("sts", false, "user-A", "account-A", "cn-beijing", "default", "profile-A"), "medium", true),
            (new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-beijing", "default", "profile-B"), "medium", true),
            (new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-beijing", "default", "profile-A"), "large", true),
            (new ArkAgentPlanIdentity("sso", false, "user-A", "account-A", "cn-beijing", "default", "profile-A"), "medium", false)
        };

        foreach (var (identity, tier, subscribed) in currentScopes)
        {
            using var harness = new Harness(_ => Task.FromResult(
                ArkCliResult.SuccessWithDaily(usagePayload, CreateDailyPayload())),
                _ => CreateUsagePayload(identity, tier, subscribed));

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

        var currentIdentityWithoutAccount = CreateUsagePayload(new ArkAgentPlanIdentity(
            "sso", false, "user-A", null, "cn-beijing", "default", "profile-A"));
        var currentIdentityWithoutProject = CreateUsagePayload(new ArkAgentPlanIdentity(
            "sso", false, "user-A", "account-A", "cn-beijing", null, "profile-A"));
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

    [TestMethod]
    public async Task ShutdownDoesNotWaitForNonCooperativeAcquisition()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisition = new TaskCompletionSource<ArkCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(_ =>
        {
            entered.TrySetResult();
            return acquisition.Task;
        });
        var readers = MetricIds.Select(id => harness.Bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pendingReads = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        using var stop = new CancellationTokenSource();
        var run = harness.Provider.RunAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(acquisition.Task.IsCompleted);
            await Task.WhenAll(pendingReads).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(readers.All(reader => reader.Current.State == MetricState.Stopped &&
                reader.Current.Value is null));
        }
        finally
        {
            stop.Cancel();
            acquisition.TrySetResult(ArkCliResult.Unavailable("cli_not_found"));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    private static MetricDescriptor FindDescriptor(MetricRegistry registry, string id)
    {
        Assert.IsTrue(registry.TryGet(new MetricId(id), out var descriptor));
        return descriptor!;
    }

    private static byte[] CreateUsagePayload(ArkAgentPlanIdentity identity, string? tier = "medium",
        bool subscribed = true) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            viewer = new
            {
                auth_method = identity.AuthMethod,
                is_root = identity.IsRoot,
                user_id = identity.UserId,
                account_id = identity.AccountId,
                profile = identity.Profile,
                region = identity.Region,
                project_name = identity.ProjectName
            },
            items = new[]
            {
                new
                {
                    product = "agent-plan",
                    tier,
                    subscribed,
                    periods = new[] { new { label = "5h", used = 2, total = 10, percent = 20 } }
                }
            }
        });

    private static byte[] CreateDailyPayload(string action = "GetAFPUsage", string service = "ark",
        string version = "2024-01-01", string region = "cn-beijing", string planType = "Medium",
        string quota = "100.0", string used = "22.5", long? resetTime = 1_800_000_000_000,
        bool includeDaily = true, bool apiError = false)
    {
        object? daily = includeDaily ? new { Quota = quota, Used = used, ResetTime = resetTime } : null;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            ResponseMetadata = new
            {
                Action = action, Service = service, Version = version, Region = region,
                Error = apiError ? new { Code = "synthetic-error" } : null
            },
            Result = new { PlanType = planType, AFPDaily = daily }
        });
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
            Func<ArkCliResult, byte[]?>? currentUsage = null) =>
            Provider = new ArkAgentPlanProvider(Registry, Bus, async token =>
            {
                var result = await acquire(token).ConfigureAwait(false);
                var currentUsagePayload = currentUsage is null
                    ? result.CurrentUsagePayload ?? result.Payload
                    : currentUsage(result);
                return result with { CurrentUsagePayload = currentUsagePayload };
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
