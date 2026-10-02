#nullable enable

using System;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService.Providers.AI.ArkAgentPlan;

internal readonly record struct ArkCliResult(byte[]? Payload, string? FailureCode, bool IsUnavailable)
{
    public static ArkCliResult Success(byte[] payload) => new(payload, null, false);
    public static ArkCliResult Failure(string code) => new(null, code, false);
    public static ArkCliResult Unavailable(string code) => new(null, code, true);
}

internal static class ArkAgentPlanCli
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    internal const int MaxStandardOutputBytes = ArkAgentPlanParser.MaxPayloadBytes;
    internal const int MaxStandardErrorBytes = 16 * 1024;
    internal const string Source = "ArkCli.AgentPlanUsage.v1";
    private const string ExecutableRelativePath =
        "npm/node_modules/@volcengine/ark-cli/bin/arkcli-windows-amd64.exe";
    private static readonly string[] AmbientProxyVariables =
    [
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
        "http_proxy", "https_proxy", "all_proxy",
        "NO_PROXY", "no_proxy"
    ];

    public static async Task<ArkCliResult> ReadAsync(CancellationToken cancellationToken)
    {
        var executablePath = GetExecutablePath();
        if (executablePath is null) return ArkCliResult.Unavailable("unsupported_platform");
        if (!File.Exists(executablePath)) return ArkCliResult.Unavailable("cli_not_found");
        var verifiedExecutable = await ArkAuthenticodeVerifier.OpenVerifiedExecutableAsync(executablePath,
            cancellationToken).ConfigureAwait(false);
        if (verifiedExecutable is null)
            return ArkCliResult.Failure("executable_untrusted");

        using (verifiedExecutable)
            return await RunAsync(executablePath, cancellationToken).ConfigureAwait(false);
    }

    internal static string? GetExecutablePath()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(roaming)
            ? null
            : Path.Combine(roaming, ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var expectedPath = GetExecutablePath();
        if (expectedPath is null || !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(executablePath), Path.GetFullPath(expectedPath)))
            throw new ArgumentException("Ark CLI path is outside the verified package location.", nameof(executablePath));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(userProfile) ? userProfile : Environment.SystemDirectory
        };
        startInfo.ArgumentList.Add("usage");
        startInfo.ArgumentList.Add("plan");
        startInfo.ArgumentList.Add("--product");
        startInfo.ArgumentList.Add("agent-plan");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        startInfo.Environment["ARKCLI_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["ARKCLI_CALLER_TYPE"] = "ai_agent";
        startInfo.Environment["ARKCLI_CALLER_NAME"] = "codex";
        startInfo.Environment["ARKCLI_SKILL_NAME"] = "arkcli-usage";
        startInfo.Environment.Remove("ARK_PROFILE");
        startInfo.Environment.Remove("ARK_API_KEY");
        startInfo.Environment.Remove("ARK_BASE_URL");
        foreach (var variable in AmbientProxyVariables)
            startInfo.Environment.Remove(variable);
        return startInfo;
    }

    internal static async Task<byte[]> ReadBoundedOutputAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var output = new MemoryStream(Math.Min(maximumBytes, 8192));
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var remaining = maximumBytes - (int)output.Length;
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                if (read > remaining) throw new InvalidDataException("Bounded CLI output exceeded its limit.");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task DrainBoundedOutputAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var total = 0;
        try
        {
            while (true)
            {
                var remaining = maximumBytes - total;
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                if (read > remaining) throw new InvalidDataException("Bounded CLI output exceeded its limit.");
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<ArkCliResult> RunAsync(string executablePath, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executablePath) };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) return ArkCliResult.Failure("cli_start_failed");
            process.StandardInput.Close();

            using var deadline = new CancellationTokenSource(RequestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var standardOutput = StopOnReadFailureAsync(
                ReadBoundedOutputAsync(process.StandardOutput.BaseStream, MaxStandardOutputBytes, linked.Token), process);
            var standardError = StopOnReadFailureAsync(
                DrainBoundedOutputAsync(process.StandardError.BaseStream, MaxStandardErrorBytes, linked.Token), process);
            var exit = process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(exit, standardOutput, standardError).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) return ArkCliResult.Failure("cli_failed");
            return ArkCliResult.Success(await standardOutput.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (started) _ = TryTerminate(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (started) _ = TryTerminate(process);
            return ArkCliResult.Failure("timeout");
        }
        catch (InvalidDataException)
        {
            if (started) _ = TryTerminate(process);
            return ArkCliResult.Failure("cli_output_too_large");
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            if (started) _ = TryTerminate(process);
            return ArkCliResult.Failure("cli_io_failed");
        }
        finally
        {
            if (started && !HasExited(process)) _ = TryTerminate(process);
        }
    }

    private static async Task<T> StopOnReadFailureAsync<T>(Task<T> read, Process process)
    {
        try { return await read.ConfigureAwait(false); }
        catch
        {
            _ = TryTerminate(process);
            throw;
        }
    }

    private static async Task StopOnReadFailureAsync(Task read, Process process)
    {
        try { await read.ConfigureAwait(false); }
        catch
        {
            _ = TryTerminate(process);
            throw;
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static bool TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
