#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.TelemetryService.Providers.AI.GitHubCopilot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.GitHubCopilot;

[TestClass]
public sealed class CopilotQuotaAcquirerTests
{
    [TestMethod]
    public async Task BracketsQuotaReadWithSameAccountAndUsesOnlyExpectedCliRequests()
    {
        var requests = new List<CopilotGhRequest>();
        var responses = new Queue<string>([
            "{\"state\":\"success\",\"tokenSource\":\"keyring\"}",
            "{\"id\":24680,\"login\":\"synthetic-user\"}",
            QuotaPayload(24680),
            "{\"id\":24680,\"login\":\"synthetic-user\"}"
        ]);
        var acquirer = CreateAcquirer(requests, responses);

        var reading = await acquirer.AcquireAsync();

        CollectionAssert.AreEqual(new[]
        {
            CopilotGhRequest.AuthStatus,
            CopilotGhRequest.AuthenticatedUser,
            CopilotGhRequest.Quota,
            CopilotGhRequest.AuthenticatedUser
        }, requests);
        Assert.AreEqual(64, reading.StableAccountKey.Length);
        Assert.IsFalse(reading.StableAccountKey.Contains("24680", StringComparison.Ordinal));
        Assert.IsTrue(reading.TokenBasedBilling);
        Assert.AreEqual(1, reading.Buckets.Count);
    }

    [TestMethod]
    public async Task RejectsQuotaForDifferentAccountBeforeAfterIdentityCheck()
    {
        var requests = new List<CopilotGhRequest>();
        var responses = new Queue<string>([
            "{\"state\":\"success\"}",
            "{\"id\":24680}",
            QuotaPayload(13579)
        ]);

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(
            async () => await CreateAcquirer(requests, responses).AcquireAsync());

        Assert.AreEqual("identity_mismatch", exception.ErrorCode);
        CollectionAssert.AreEqual(new[]
        {
            CopilotGhRequest.AuthStatus,
            CopilotGhRequest.AuthenticatedUser,
            CopilotGhRequest.Quota
        }, requests);
    }

    [TestMethod]
    public async Task RejectsAccountSwitchAfterQuotaResponse()
    {
        var requests = new List<CopilotGhRequest>();
        var responses = new Queue<string>([
            "{\"state\":\"success\"}",
            "{\"id\":24680}",
            QuotaPayload(24680),
            "{\"id\":13579}"
        ]);

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(
            async () => await CreateAcquirer(requests, responses).AcquireAsync());

        Assert.AreEqual("identity_mismatch", exception.ErrorCode);
        Assert.AreEqual(4, requests.Count);
    }

    [TestMethod]
    public async Task MissingCliAuthenticationStopsBeforeReadingAccountOrQuota()
    {
        var requests = new List<CopilotGhRequest>();
        var responses = new Queue<string>(["{\"state\":\"failure\"}"]);

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(
            async () => await CreateAcquirer(requests, responses).AcquireAsync());

        Assert.AreEqual("auth_unavailable", exception.ErrorCode);
        CollectionAssert.AreEqual(new[] { CopilotGhRequest.AuthStatus }, requests);
    }

    [TestMethod]
    public void EnterpriseConfigurationFailsClosed()
    {
        Assert.IsTrue(CopilotQuotaAcquirer.IsDotComConfigurationSupported(null, false));
        Assert.IsTrue(CopilotQuotaAcquirer.IsDotComConfigurationSupported("github.com", false));
        Assert.IsFalse(CopilotQuotaAcquirer.IsDotComConfigurationSupported("ghe.example.com", false));
        Assert.IsFalse(CopilotQuotaAcquirer.IsDotComConfigurationSupported("github.com", true));
    }

    [TestMethod]
    public async Task ProcessOutputLimitTerminatesOversizedChildOutput()
    {
        var startInfo = CreatePowerShellStartInfo("[Console]::Out.Write('x' * 65536)");

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(async () =>
            await CopilotQuotaAcquirer.RunProcessAsync(startInfo, 1024, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.AreEqual("response_too_large", exception.ErrorCode);
    }

    [TestMethod]
    public async Task ProcessCancellationTerminatesChildPromptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var startInfo = CreatePowerShellStartInfo("Start-Sleep -Seconds 30");
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CopilotQuotaAcquirer.RunProcessAsync(startInfo, 1024, TimeSpan.FromSeconds(5), cancellation.Token));

        Assert.IsTrue(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    private static CopilotQuotaAcquirer CreateAcquirer(List<CopilotGhRequest> requests, Queue<string> responses) =>
        new((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            if (responses.Count == 0) throw new InvalidOperationException("Synthetic response queue exhausted.");
            return Task.FromResult(Encoding.UTF8.GetBytes(responses.Dequeue()));
        });

    private static string QuotaPayload(long accountId) =>
        "{\"id\":" + accountId.ToString(CultureInfo.InvariantCulture) +
        ",\"token_based_billing\":true,\"quota_snapshots\":{\"chat\":{\"token_based_billing\":true,\"entitlement\":50,\"credits_used\":10,\"quota_remaining\":40,\"percent_remaining\":80,\"quota_reset_at\":1893456000,\"unlimited\":false,\"has_quota\":true}}}";

    private static ProcessStartInfo CreatePowerShellStartInfo(string script)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }
}
