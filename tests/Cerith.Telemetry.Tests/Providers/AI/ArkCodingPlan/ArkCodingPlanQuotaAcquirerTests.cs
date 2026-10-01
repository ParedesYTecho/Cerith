#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class ArkCodingPlanQuotaAcquirerTests
{
    private const string IdentityA = """
        {"logged_in":true,"auth_method":"sso","account_id":"210000","user_id":"12345","trn":"trn:iam::210000:user/alice","is_root":false}
        """;
    private const string IdentityB = """
        {"logged_in":true,"auth_method":"sso","account_id":"999999","user_id":"12345","trn":"trn:iam::999999:user/alice","is_root":false}
        """;
    private const string UsagePlan = """
        {
          "viewer":{"auth_method":"sso","is_root":false,"user_id":"12345","user_name":"alice","account_id":"210000","profile":"personal-profile"},
          "items":[{"product":"coding-plan","edition":"personal","subscribed":true,"periods":[
            {"label":"session","percent":50,"reset_at":"2026-10-01T17:00:00+08:00"}
          ]}]
        }
        """;

    [TestMethod]
    public async Task PinsPersonalProductToStableProfileAndComparesIdentityBeforeAndAfter()
    {
        using var fixture = new Fixture([IdentityA, UsagePlan, IdentityA]);
        var owners = new List<string>();

        var reading = await fixture.Acquirer.AcquireAsync(
            new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), owners.Add, CancellationToken.None);

        Assert.AreEqual(3, fixture.Commands.Count);
        Assert.AreEqual(2, owners.Count);
        Assert.AreEqual(owners[0], owners[1]);
        CollectionAssert.AreEqual(new[] { "auth", "whoami", "--profile", "personal-profile", "--env", "prod", "--format", "json" },
            fixture.Commands[0].ArgumentList.ToArray());
        CollectionAssert.AreEqual(new[] { "usage", "plan", "--product", "coding-plan", "--profile", "personal-profile", "--env", "prod", "--format", "json" },
            fixture.Commands[1].ArgumentList.ToArray());
        CollectionAssert.AreEqual(fixture.Commands[0].ArgumentList.ToArray(), fixture.Commands[2].ArgumentList.ToArray());
        Assert.AreEqual(50d, reading.Points[ArkCodingPlanQuotaParser.SessionUsed].Value!.Value.AsDouble());
    }

    [TestMethod]
    public async Task RejectsStsWithoutProvenRefreshSourceBeforeQueryingQuota()
    {
        using var fixture = new Fixture(["""
            {"logged_in":true,"auth_method":"sts","account_id":"210000","user_id":"12345","trn":"trn:iam::210000:user/alice"}
            """]);

        var exception = await Assert.ThrowsExactlyAsync<ArkCodingPlanAcquisitionException>(() =>
            fixture.Acquirer.AcquireAsync(DateTimeOffset.UtcNow, _ => { }, CancellationToken.None));

        Assert.AreEqual("refresh_owner_unsupported", exception.ErrorCode);
        Assert.AreEqual(1, fixture.Commands.Count);
    }

    [TestMethod]
    public async Task RejectsAccountTransitionAcrossUsageRequest()
    {
        using var fixture = new Fixture([IdentityA, UsagePlan, IdentityB]);

        var exception = await Assert.ThrowsExactlyAsync<ArkCodingPlanAcquisitionException>(() =>
            fixture.Acquirer.AcquireAsync(DateTimeOffset.UtcNow, _ => { }, CancellationToken.None));

        Assert.AreEqual("identity_changed", exception.ErrorCode);
        Assert.AreEqual(3, fixture.Commands.Count);
    }

    [TestMethod]
    public async Task UnexpectedRunnerFaultIsNotConvertedToProviderFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerith-ark-acquirer-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cliPath = Path.Combine(root, "arkcli.exe");
            File.WriteAllBytes(cliPath, []);
            using var acquirer = new ArkCodingPlanQuotaAcquirer(cliPath, root,
                name => name == "ARK_PROFILE" ? "personal-profile" : null,
                (_, _, _, _) => throw new InvalidOperationException("synthetic runner fault"), new byte[32]);

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                acquirer.AcquireAsync(DateTimeOffset.UtcNow, _ => { }, CancellationToken.None));

            Assert.AreEqual("synthetic runner fault", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ResolvesOnlyExplicitEnvironmentOrConfiguredDefaultProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerith-ark-profile-" + Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(root, ".arkcli");
        Directory.CreateDirectory(configDirectory);
        try
        {
            File.WriteAllText(Path.Combine(configDirectory, "config.yaml"),
                "default_profile: \"personal-profile\"\napi_key: must-not-be-returned\n");
            Assert.AreEqual("personal-profile",
                ArkCodingPlanQuotaAcquirer.ReadDefaultProfile(Path.Combine(configDirectory, "config.yaml")));

            File.WriteAllText(Path.Combine(configDirectory, "config.yaml"), "profiles: []\n");
            Assert.IsNull(ArkCodingPlanQuotaAcquirer.ReadDefaultProfile(Path.Combine(configDirectory, "config.yaml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void UsesShellFreeArgumentsAndRemovesCredentialAndRoutingOverrides()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ARK_API_KEY"] = "synthetic-secret",
            ["ARK_BASE_URL"] = "https://attacker.invalid",
            ["ARK_PROFILE"] = "other-profile",
            ["VOLC_INIT_ACCESS_KEY"] = "synthetic-secret",
            ["VOLCENGINE_ENDPOINT"] = "attacker.invalid",
            ["HTTPS_PROXY"] = "http://attacker.invalid"
        };

        var startInfo = ArkCodingPlanQuotaAcquirer.CreateStartInfo(
            ArkCodingPlanCliCommand.UsagePlan, "personal-profile", "C:\\verified\\arkcli.exe", environment);

        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.IsTrue(new[] { "ARK_API_KEY", "ARK_BASE_URL", "ARK_PROFILE", "VOLC_INIT_ACCESS_KEY",
            "VOLCENGINE_ENDPOINT", "HTTPS_PROXY" }.All(name => !startInfo.Environment.ContainsKey(name)));
        CollectionAssert.AreEqual(new[] { "usage", "plan", "--product", "coding-plan", "--profile", "personal-profile",
            "--env", "prod", "--format", "json" }, startInfo.ArgumentList.ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly Queue<string> _responses;

        public List<ProcessStartInfo> Commands { get; } = [];
        public ArkCodingPlanQuotaAcquirer Acquirer { get; }

        public Fixture(IEnumerable<string> responses)
        {
            _root = Path.Combine(Path.GetTempPath(), "cerith-ark-acquirer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var cliPath = Path.Combine(_root, "arkcli.exe");
            File.WriteAllBytes(cliPath, []);
            _responses = new Queue<string>(responses);
            Acquirer = new ArkCodingPlanQuotaAcquirer(cliPath, _root,
                name => name == "ARK_PROFILE" ? "personal-profile" : null,
                (startInfo, _, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Commands.Add(startInfo);
                    if (_responses.Count == 0) throw new InvalidOperationException("Unexpected extra command.");
                    return Task.FromResult(Encoding.UTF8.GetBytes(_responses.Dequeue()));
                }, new byte[32]);
        }

        public void Dispose()
        {
            Acquirer.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
