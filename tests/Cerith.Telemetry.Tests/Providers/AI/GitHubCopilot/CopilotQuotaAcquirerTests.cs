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
    public async Task RejectsQuotaForDifferentAccountAfterCheckingAuthenticatedIdentity()
    {
        var requests = new List<CopilotGhRequest>();
        var responses = new Queue<string>([
            "{\"state\":\"success\"}",
            "{\"id\":24680}",
            QuotaPayload(13579),
            "{\"id\":24680}"
        ]);

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(
            async () => await CreateAcquirer(requests, responses).AcquireAsync());

        Assert.AreEqual("identity_mismatch", exception.ErrorCode);
        CollectionAssert.AreEqual(new[]
        {
            CopilotGhRequest.AuthStatus,
            CopilotGhRequest.AuthenticatedUser,
            CopilotGhRequest.Quota,
            CopilotGhRequest.AuthenticatedUser
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
    public async Task RateLimitPreservesRetryAfterOnlyAfterTheSameAccountIsRechecked()
    {
        var requests = new List<CopilotGhRequest>();
        var responses = new Queue<string>([
            "{\"state\":\"success\",\"tokenSource\":\"keyring\"}",
            "{\"id\":24680}",
            "HTTP/2 429 Too Many Requests\r\nRetry-After: 1200\r\nContent-Type: application/json\r\n\r\n{\"message\":\"synthetic\"}",
            "{\"id\":24680}"
        ]);

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(
            async () => await CreateAcquirer(requests, responses).AcquireAsync());

        Assert.AreEqual("rate_limited", exception.ErrorCode);
        Assert.AreEqual(TimeSpan.FromSeconds(1200), exception.RetryAfterDelay);
        Assert.IsNull(exception.RetryAfterAt);
        Assert.AreEqual(64, exception.StableAccountKey!.Length);
        Assert.IsFalse(exception.StableAccountKey.Contains("24680", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[]
        {
            CopilotGhRequest.AuthStatus,
            CopilotGhRequest.AuthenticatedUser,
            CopilotGhRequest.Quota,
            CopilotGhRequest.AuthenticatedUser
        }, requests);
    }

    [TestMethod]
    public void ParsesHttpDateRetryAfterWithoutUsingTheMachineClock()
    {
        var response = CopilotQuotaAcquirer.ParseApiResponse(Encoding.ASCII.GetBytes(
            "HTTP/1.1 503 Service Unavailable\r\nRetry-After: Thu, 01 Oct 2026 12:00:00 GMT\r\n\r\n{}"));

        Assert.AreEqual(503, response.StatusCode);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-01T12:00:00Z"), response.RetryAfterAt);
        Assert.IsNull(response.RetryAfterDelay);
        Assert.AreEqual("{}", Encoding.UTF8.GetString(response.Body.Span));
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
    public void ConstructedGitHubCliRequestDropsDebugCredentialAndAmbientProxyOverrides()
    {
        var syntheticEnvironment = new Dictionary<string, string?>
        {
            ["GH_DEBUG"] = "api",
            ["GH_TOKEN"] = "synthetic-token",
            ["HTTP_PROXY"] = "http://proxy.invalid",
            ["HTTPS_PROXY"] = "http://proxy.invalid",
            ["ALL_PROXY"] = "http://proxy.invalid",
            ["NO_PROXY"] = "github.com",
            ["http_proxy"] = "http://proxy.invalid",
            ["https_proxy"] = "http://proxy.invalid",
            ["all_proxy"] = "http://proxy.invalid",
            ["no_proxy"] = "github.com"
        };

        var startInfo = CopilotQuotaAcquirer.CreateStartInfo(CopilotGhRequest.Quota, syntheticEnvironment);

        foreach (var variable in syntheticEnvironment.Keys)
            Assert.IsFalse(startInfo.Environment.ContainsKey(variable), variable);
        Assert.AreEqual("github.com", startInfo.Environment["GH_HOST"]);
        Assert.AreEqual("disabled", startInfo.Environment["GH_PROMPT"]);
        Assert.AreEqual("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
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

    [TestMethod]
    public async Task ProcessDeadlineTerminatesChildAndReturnsOnlySanitizedTimeoutCode()
    {
        var startInfo = CreatePowerShellStartInfo("Start-Sleep -Seconds 30");
        var started = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(async () =>
            await CopilotQuotaAcquirer.RunProcessAsync(startInfo, 1024,
                TimeSpan.FromMilliseconds(150), CancellationToken.None));

        Assert.AreEqual("request_timeout", exception.ErrorCode);
        Assert.IsFalse(exception.Message.Contains("Start-Sleep", StringComparison.Ordinal));
        Assert.IsTrue(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [TestMethod]
    public async Task ProcessWaitsForChildAfterBothOutputStreamsCloseWithoutSpinning()
    {
        var startInfo = CreatePowerShellStartInfo(
            "[Console]::Out.Close(); [Console]::Error.Close(); Start-Sleep -Seconds 30");
        using var currentProcess = Process.GetCurrentProcess();
        var cpuBefore = currentProcess.TotalProcessorTime;
        var started = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<CopilotQuotaAcquisitionException>(async () =>
            await CopilotQuotaAcquirer.RunProcessAsync(startInfo, 1024,
                TimeSpan.FromMilliseconds(1000), CancellationToken.None));

        var cpuUsed = currentProcess.TotalProcessorTime - cpuBefore;
        Assert.AreEqual("request_timeout", exception.ErrorCode);
        Assert.IsTrue(started.Elapsed >= TimeSpan.FromMilliseconds(700));
        Assert.IsTrue(cpuUsed < TimeSpan.FromMilliseconds(800),
            "Waiting for a child with closed output streams should not consume a CPU core.");
    }

    [TestMethod]
    public async Task ProcessCanReturnBoundedHttpErrorHeadersForRetryParsing()
    {
        const string responseText = "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 45\r\n\r\n{}";
        var script = "[Console]::Out.Write('" + responseText.Replace("'", "''", StringComparison.Ordinal) + "'); exit 1";
        var startInfo = CreatePowerShellStartInfo(script);

        var output = await CopilotQuotaAcquirer.RunProcessAsync(startInfo,
            CopilotQuotaAcquirer.MaxQuotaProcessOutputBytes, TimeSpan.FromSeconds(5),
            CancellationToken.None, acceptNonZeroExitCode: true);
        var response = CopilotQuotaAcquirer.ParseApiResponse(output);

        Assert.AreEqual(429, response.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(45), response.RetryAfterDelay);
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
