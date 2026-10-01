#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService.Providers.AI.GitHubCopilot;

internal enum CopilotGhRequest
{
    AuthStatus,
    AuthenticatedUser,
    Quota
}

internal sealed class CopilotQuotaAcquirer
{
    internal const int MaxQuotaResponseBytes = CopilotQuotaParser.MaxResponseBytes;
    internal const int MaxIdentityResponseBytes = 64 * 1024;
    internal const int MaxAuthStatusBytes = 8 * 1024;
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] CredentialEnvironmentVariables =
    [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GH_ENTERPRISE_TOKEN",
        "GITHUB_ENTERPRISE_TOKEN"
    ];

    private readonly Func<CopilotGhRequest, CancellationToken, Task<byte[]>> _request;

    internal CopilotQuotaAcquirer()
        : this(RequestThroughGitHubCliAsync)
    {
    }

    internal CopilotQuotaAcquirer(Func<CopilotGhRequest, CancellationToken, Task<byte[]>> request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
    }

    internal async Task<CopilotQuotaReading> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDotComConfigurationSupported(
                Environment.GetEnvironmentVariable("GH_HOST"),
                CredentialEnvironmentVariables[2..].Any(IsEnvironmentVariableSet)))
            throw new CopilotQuotaAcquisitionException("unsupported_host");

        byte[]? authStatus = null;
        byte[]? beforeIdentity = null;
        byte[]? quotaResponse = null;
        byte[]? afterIdentity = null;
        try
        {
            authStatus = await _request(CopilotGhRequest.AuthStatus, cancellationToken).ConfigureAwait(false);
            if (!HasSuccessfulAuthStatus(authStatus))
                throw new CopilotQuotaAcquisitionException("auth_unavailable");

            beforeIdentity = await _request(CopilotGhRequest.AuthenticatedUser, cancellationToken).ConfigureAwait(false);
            var beforeAccountId = CopilotQuotaParser.ReadAuthenticatedAccountId(beforeIdentity);

            quotaResponse = await _request(CopilotGhRequest.Quota, cancellationToken).ConfigureAwait(false);
            var reading = CopilotQuotaParser.Parse(quotaResponse, beforeAccountId);

            afterIdentity = await _request(CopilotGhRequest.AuthenticatedUser, cancellationToken).ConfigureAwait(false);
            var afterAccountId = CopilotQuotaParser.ReadAuthenticatedAccountId(afterIdentity);
            if (beforeAccountId != afterAccountId)
                throw new CopilotQuotaAcquisitionException("identity_mismatch");

            return reading;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CopilotQuotaAcquisitionException)
        {
            throw;
        }
        catch (CopilotQuotaParseException exception)
        {
            throw new CopilotQuotaAcquisitionException(exception.ErrorCode);
        }
        finally
        {
            Zero(authStatus);
            Zero(beforeIdentity);
            Zero(quotaResponse);
            Zero(afterIdentity);
        }
    }

    internal static bool IsDotComConfigurationSupported(string? ghHost, bool enterpriseCredentialPresent) =>
        !enterpriseCredentialPresent &&
        (string.IsNullOrWhiteSpace(ghHost) ||
         StringComparer.OrdinalIgnoreCase.Equals(ghHost.Trim(), "github.com"));

    internal static async Task<byte[]> RunProcessAsync(ProcessStartInfo startInfo, int maximumOutputBytes,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new CopilotQuotaAcquisitionException("process_start_failed");
            if (startInfo.RedirectStandardInput)
                process.StandardInput.Close();
        }
        catch (CopilotQuotaAcquisitionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
                                          System.ComponentModel.Win32Exception)
        {
            throw new CopilotQuotaAcquisitionException("gh_unavailable");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<byte[]> stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutputBytes, deadline.Token);
        Task stderrTask = DrainBoundedAsync(process.StandardError.BaseStream, 8 * 1024, deadline.Token);
        Task exitTask = process.WaitForExitAsync(deadline.Token);

        try
        {
            while (!exitTask.IsCompleted)
            {
                if (stdoutTask.IsFaulted || stdoutTask.IsCanceled)
                    await stdoutTask.ConfigureAwait(false);
                if (stderrTask.IsFaulted || stderrTask.IsCanceled)
                    await stderrTask.ConfigureAwait(false);

                Task completed = stdoutTask.IsCompleted
                    ? await Task.WhenAny(stderrTask, exitTask).ConfigureAwait(false)
                    : stderrTask.IsCompleted
                        ? await Task.WhenAny(stdoutTask, exitTask).ConfigureAwait(false)
                        : await Task.WhenAny(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

                if (completed == stdoutTask) await stdoutTask.ConfigureAwait(false);
                if (completed == stderrTask) await stderrTask.ConfigureAwait(false);
                if (completed == exitTask) break;
            }

            await exitTask.ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryKill(process);
            if (!await FinishAfterKillAsync(process, stdoutTask, stderrTask).ConfigureAwait(false) &&
                !cancellationToken.IsCancellationRequested)
                throw new CopilotQuotaAcquisitionException("process_cleanup_timeout");

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (deadline.IsCancellationRequested)
                throw new CopilotQuotaAcquisitionException("request_timeout");
            if (exception is CopilotQuotaAcquisitionException acquisitionException)
                throw acquisitionException;
            throw new CopilotQuotaAcquisitionException("process_failed");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new CopilotQuotaAcquisitionException("request_failed");

        return await stdoutTask.ConfigureAwait(false);
    }

    private static async Task<byte[]> RequestThroughGitHubCliAsync(CopilotGhRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var variable in CredentialEnvironmentVariables)
            startInfo.Environment.Remove(variable);
        startInfo.Environment["GH_HOST"] = "github.com";
        startInfo.Environment["GH_PROMPT"] = "disabled";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        switch (request)
        {
            case CopilotGhRequest.AuthStatus:
                startInfo.ArgumentList.Add("auth");
                startInfo.ArgumentList.Add("status");
                startInfo.ArgumentList.Add("--active");
                startInfo.ArgumentList.Add("--hostname");
                startInfo.ArgumentList.Add("github.com");
                startInfo.ArgumentList.Add("--json");
                startInfo.ArgumentList.Add("hosts");
                startInfo.ArgumentList.Add("--jq");
                startInfo.ArgumentList.Add(".hosts[\"github.com\"][0] | {state, tokenSource}");
                break;
            case CopilotGhRequest.AuthenticatedUser:
                AddApiArguments(startInfo, "/user");
                break;
            case CopilotGhRequest.Quota:
                AddApiArguments(startInfo, "/copilot_internal/user");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        var maximumOutput = request switch
        {
            CopilotGhRequest.AuthStatus => MaxAuthStatusBytes,
            CopilotGhRequest.AuthenticatedUser => MaxIdentityResponseBytes,
            _ => MaxQuotaResponseBytes
        };
        return await RunProcessAsync(startInfo, maximumOutput, ProcessTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddApiArguments(ProcessStartInfo startInfo, string endpoint)
    {
        startInfo.ArgumentList.Add("api");
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add("github.com");
        startInfo.ArgumentList.Add("--method");
        startInfo.ArgumentList.Add("GET");
        startInfo.ArgumentList.Add(endpoint);
    }

    private static bool HasSuccessfulAuthStatus(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxAuthStatusBytes)
            throw new CopilotQuotaAcquisitionException("response_too_large");
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload,
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("state", out var state) && state.ValueKind == System.Text.Json.JsonValueKind.String &&
                StringComparer.Ordinal.Equals(state.GetString(), "success");
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool IsEnvironmentVariableSet(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var output = new byte[maximumBytes];
        var probe = new byte[1];
        var total = 0;
        try
        {
            while (true)
            {
                if (total == maximumBytes)
                {
                    if (await stream.ReadAsync(probe.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
                        throw new CopilotQuotaAcquisitionException("response_too_large");
                    return output.AsSpan(0, total).ToArray();
                }

                var count = await stream.ReadAsync(output.AsMemory(total, Math.Min(8192, maximumBytes - total)),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0) return output.AsSpan(0, total).ToArray();
                total += count;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
            CryptographicOperations.ZeroMemory(probe);
        }
    }

    private static async Task DrainBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var total = 0;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) return;
                total += count;
                if (total > maximumBytes)
                    throw new CopilotQuotaAcquisitionException("process_output_too_large");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> FinishAfterKillAsync(Process process, Task stdoutTask, Task stderrTask)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cleanup.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or CopilotQuotaAcquisitionException or
                                          IOException or InvalidOperationException)
        {
            return process.HasExited;
        }
    }

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
}

internal sealed class CopilotQuotaAcquisitionException(string errorCode) : Exception
{
    internal string ErrorCode { get; } = errorCode;
}
