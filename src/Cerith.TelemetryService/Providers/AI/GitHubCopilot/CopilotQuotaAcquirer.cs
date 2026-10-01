#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    internal const int MaxQuotaResponseHeaderBytes = 16 * 1024;
    internal const int MaxQuotaProcessOutputBytes = MaxQuotaResponseBytes + MaxQuotaResponseHeaderBytes;
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] CredentialEnvironmentVariables =
    [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GH_ENTERPRISE_TOKEN",
        "GITHUB_ENTERPRISE_TOKEN"
    ];

    private static readonly string[] UntrustedNetworkEnvironmentVariables =
    [
        "GH_DEBUG",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy"
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

            CopilotQuotaApiResponse? apiResponse = null;
            CopilotQuotaAcquisitionException? quotaFailure = null;
            try
            {
                quotaResponse = await _request(CopilotGhRequest.Quota, cancellationToken).ConfigureAwait(false);
                apiResponse = ParseApiResponse(quotaResponse);
                if (apiResponse.StatusCode is < 200 or >= 300)
                {
                    var errorCode = apiResponse.StatusCode switch
                    {
                        401 => "auth_required",
                        403 when apiResponse.RetryAfterDelay.HasValue || apiResponse.RetryAfterAt.HasValue => "rate_limited",
                        403 => "auth_forbidden",
                        429 => "rate_limited",
                        >= 300 and < 400 => "redirect_rejected",
                        >= 500 => "upstream_unavailable",
                        _ => "request_failed"
                    };
                    quotaFailure = new CopilotQuotaAcquisitionException(errorCode,
                        apiResponse.RetryAfterDelay, apiResponse.RetryAfterAt);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CopilotQuotaAcquisitionException exception)
            {
                quotaFailure = exception;
            }

            afterIdentity = await _request(CopilotGhRequest.AuthenticatedUser, cancellationToken).ConfigureAwait(false);
            var afterAccountId = CopilotQuotaParser.ReadAuthenticatedAccountId(afterIdentity);
            if (beforeAccountId != afterAccountId)
                throw new CopilotQuotaAcquisitionException("identity_mismatch");

            var stableAccountKey = CopilotQuotaParser.HashAccountId(beforeAccountId);
            if (quotaFailure is not null)
                throw new CopilotQuotaAcquisitionException(quotaFailure.ErrorCode,
                    quotaFailure.RetryAfterDelay, quotaFailure.RetryAfterAt, stableAccountKey);

            try
            {
                return CopilotQuotaParser.Parse(apiResponse!.Body, beforeAccountId);
            }
            catch (CopilotQuotaParseException exception)
            {
                throw new CopilotQuotaAcquisitionException(exception.ErrorCode,
                    stableAccountKey: exception.ErrorCode == "identity_mismatch" ? null : stableAccountKey);
            }
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
        TimeSpan timeout, CancellationToken cancellationToken, bool acceptNonZeroExitCode = false)
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

                if (stdoutTask.IsCompleted && stderrTask.IsCompleted)
                {
                    await exitTask.ConfigureAwait(false);
                    break;
                }

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
        if (!acceptNonZeroExitCode && process.ExitCode != 0)
            throw new CopilotQuotaAcquisitionException("request_failed");

        return await stdoutTask.ConfigureAwait(false);
    }

    private static async Task<byte[]> RequestThroughGitHubCliAsync(CopilotGhRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(request);

        var maximumOutput = request switch
        {
            CopilotGhRequest.AuthStatus => MaxAuthStatusBytes,
            CopilotGhRequest.AuthenticatedUser => MaxIdentityResponseBytes,
            _ => MaxQuotaProcessOutputBytes
        };
        try
        {
            return await RunProcessAsync(startInfo, maximumOutput, ProcessTimeout, cancellationToken,
                acceptNonZeroExitCode: request == CopilotGhRequest.Quota).ConfigureAwait(false);
        }
        catch (CopilotQuotaAcquisitionException exception) when
            (exception.ErrorCode == "request_failed" && request == CopilotGhRequest.AuthStatus)
        {
            throw new CopilotQuotaAcquisitionException("auth_unavailable");
        }
        catch (CopilotQuotaAcquisitionException exception) when
            (exception.ErrorCode == "request_failed" && request == CopilotGhRequest.AuthenticatedUser)
        {
            throw new CopilotQuotaAcquisitionException("identity_unavailable");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(CopilotGhRequest request,
        IReadOnlyDictionary<string, string?>? syntheticEnvironment = null)
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

        if (syntheticEnvironment is not null)
            foreach (var pair in syntheticEnvironment)
                startInfo.Environment[pair.Key] = pair.Value;

        foreach (var variable in CredentialEnvironmentVariables)
            startInfo.Environment.Remove(variable);
        foreach (var variable in UntrustedNetworkEnvironmentVariables)
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

        return startInfo;
    }

    private static void AddApiArguments(ProcessStartInfo startInfo, string endpoint)
    {
        startInfo.ArgumentList.Add("api");
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add("github.com");
        startInfo.ArgumentList.Add("--method");
        startInfo.ArgumentList.Add("GET");
        startInfo.ArgumentList.Add(endpoint);
        if (StringComparer.Ordinal.Equals(endpoint, "/copilot_internal/user"))
            startInfo.ArgumentList.Add("--include");
    }

    internal static CopilotQuotaApiResponse ParseApiResponse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxQuotaProcessOutputBytes)
            throw new CopilotQuotaAcquisitionException("response_too_large");

        var span = payload.Span;
        var first = 0;
        while (first < span.Length && span[first] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') first++;
        if (first < span.Length && span[first] == (byte)'{')
            return new CopilotQuotaApiResponse(200, payload[first..], null, null);

        var headerEnd = FindHeaderEnd(span);
        if (headerEnd < 0)
            throw new CopilotQuotaAcquisitionException(payload.Length > MaxQuotaResponseHeaderBytes
                ? "response_too_large"
                : "request_failed");

        var headerLength = headerEnd - 2;
        if (headerLength > MaxQuotaResponseHeaderBytes)
            throw new CopilotQuotaAcquisitionException("response_too_large");

        var headerBlock = span[..headerLength];
        var statusLineEnd = FindLineEnd(headerBlock);
        if (statusLineEnd < 0) statusLineEnd = headerBlock.Length;

        var statusParts = Encoding.ASCII.GetString(headerBlock[..statusLineEnd])
            .Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 || !statusParts[0].StartsWith("HTTP/", StringComparison.Ordinal) ||
            statusParts[1].Length != 3 || !int.TryParse(statusParts[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out var statusCode) || statusCode is < 100 or > 599)
            throw new CopilotQuotaAcquisitionException("request_failed");

        TimeSpan? retryAfterDelay = null;
        DateTimeOffset? retryAfterAt = null;
        var retryAfterSeen = false;
        var retryAfterAmbiguous = false;
        var lineStart = statusLineEnd + 2;
        while (lineStart < headerBlock.Length)
        {
            var lineEnd = FindLineEnd(headerBlock[lineStart..]);
            if (lineEnd < 0) lineEnd = headerBlock.Length - lineStart;
            var line = headerBlock.Slice(lineStart, lineEnd);
            var separator = line.IndexOf((byte)':');
            if (separator <= 0 || line[0] is (byte)' ' or (byte)'\t')
                throw new CopilotQuotaAcquisitionException("request_failed");
            if (!EqualsAsciiIgnoreCase(line[..separator], "Retry-After"))
            {
                lineStart += lineEnd + 2;
                continue;
            }
            if (retryAfterSeen)
            {
                retryAfterDelay = null;
                retryAfterAt = null;
                retryAfterAmbiguous = true;
                lineStart += lineEnd + 2;
                continue;
            }

            retryAfterSeen = true;
            var value = Encoding.ASCII.GetString(line[(separator + 1)..]).Trim();
            ParseRetryAfter(value, out retryAfterDelay, out retryAfterAt);
            lineStart += lineEnd + 2;
        }

        var bodyOffset = headerEnd + 2;
        return new CopilotQuotaApiResponse(statusCode, payload[bodyOffset..],
            retryAfterAmbiguous ? null : retryAfterDelay, retryAfterAmbiguous ? null : retryAfterAt);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> payload)
    {
        var limit = Math.Min(payload.Length - 3, MaxQuotaResponseHeaderBytes + 1);
        for (var i = 0; i < limit; i++)
            if (payload[i] == (byte)'\r' && payload[i + 1] == (byte)'\n' &&
                payload[i + 2] == (byte)'\r' && payload[i + 3] == (byte)'\n')
                return i + 2;
        return -1;
    }

    private static int FindLineEnd(ReadOnlySpan<byte> line)
    {
        for (var i = 0; i < line.Length - 1; i++)
            if (line[i] == (byte)'\r' && line[i + 1] == (byte)'\n')
                return i;
        return -1;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length != expected.Length) return false;
        for (var i = 0; i < value.Length; i++)
            if (value[i] > 0x7f || char.ToUpperInvariant((char)value[i]) != char.ToUpperInvariant(expected[i]))
                return false;
        return true;
    }

    private static void ParseRetryAfter(string value, out TimeSpan? delay, out DateTimeOffset? at)
    {
        delay = null;
        at = null;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            delay = seconds > TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromSeconds(seconds);
            return;
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var retryAt))
            at = retryAt;
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

internal sealed record CopilotQuotaApiResponse(int StatusCode, ReadOnlyMemory<byte> Body,
    TimeSpan? RetryAfterDelay, DateTimeOffset? RetryAfterAt);

internal sealed class CopilotQuotaAcquisitionException(string errorCode,
    TimeSpan? retryAfterDelay = null, DateTimeOffset? retryAfterAt = null, string? stableAccountKey = null) : Exception
{
    internal string ErrorCode { get; } = errorCode;
    internal TimeSpan? RetryAfterDelay { get; } = retryAfterDelay;
    internal DateTimeOffset? RetryAfterAt { get; } = retryAfterAt;
    internal string? StableAccountKey { get; } = stableAccountKey;
}
