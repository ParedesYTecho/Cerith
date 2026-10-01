#nullable enable

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService.Providers.AI.Kiro;

internal enum KiroAcquisitionStatus
{
    Ready,
    Unavailable,
    Unsupported,
    RetrySuppressed,
    Error
}

internal sealed record KiroAcquisition(KiroAcquisitionStatus Status, byte[]? Payload, string? ErrorCode)
{
    public static KiroAcquisition Ready(byte[] payload) => new(KiroAcquisitionStatus.Ready, payload, null);
    public static KiroAcquisition Unavailable(string? errorCode = null) =>
        new(KiroAcquisitionStatus.Unavailable, null, errorCode);
    public static KiroAcquisition Unsupported(string errorCode) =>
        new(KiroAcquisitionStatus.Unsupported, null, errorCode);
    public static KiroAcquisition RetrySuppressed() =>
        new(KiroAcquisitionStatus.RetrySuppressed, null, "rate_limit_timing_unknown");
    public static KiroAcquisition Error(string errorCode) =>
        new(KiroAcquisitionStatus.Error, null, errorCode);
}

internal enum KiroIdentityStatus
{
    Ready,
    SignedOut,
    Unavailable
}

internal sealed record KiroIdentityObservation(KiroIdentityStatus Status, byte[]? Fingerprint)
{
    public static KiroIdentityObservation Ready(byte[] fingerprint) =>
        new(KiroIdentityStatus.Ready, fingerprint);
    public static KiroIdentityObservation SignedOut() => new(KiroIdentityStatus.SignedOut, null);
    public static KiroIdentityObservation Unavailable() => new(KiroIdentityStatus.Unavailable, null);
}

internal static class KiroAcpUsageClient
{
    internal const string UsageMethod = "_kiro/account/getUsage";
    internal const string RateLimitMethod = "_kiro/error/rate_limit";
    internal const int MaxResponseBytes = 1024 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromMilliseconds(300);

    public static async Task<KiroAcquisition> ReadAsync(TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return await ReadAsync(timeProvider, cancellationToken, CreateStartInfo(), RequestTimeout)
            .ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        var startInfo = CreateCliStartInfo();
        startInfo.ArgumentList.Add("acp");
        startInfo.ArgumentList.Add("--agent-engine=v3");
        startInfo.ArgumentList.Add("--auth-method=cli");
        return startInfo;
    }

    internal static ProcessStartInfo CreateIdentityStartInfo()
    {
        var startInfo = CreateCliStartInfo();
        startInfo.ArgumentList.Add("whoami");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        return startInfo;
    }

    private static ProcessStartInfo CreateCliStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "kiro-cli",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["KIRO_LOG_LEVEL"] = "error";
        startInfo.Environment["KIRO_API_KEY"] = null;
        return startInfo;
    }

    internal static async Task<KiroAcquisition> ReadAsync(TimeProvider timeProvider,
        CancellationToken cancellationToken, ProcessStartInfo startInfo, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return KiroAcquisition.Unavailable("cli_unavailable");
        }
        catch (Win32Exception) { return KiroAcquisition.Unavailable("cli_unavailable"); }
        catch (FileNotFoundException) { return KiroAcquisition.Unavailable("cli_unavailable"); }
        catch (InvalidOperationException) { return KiroAcquisition.Unavailable("cli_unavailable"); }

        using var deadline = new CancellationTokenSource(requestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var stderrStop = new CancellationTokenSource();
        var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, stderrStop.Token);
        try
        {
            var reader = new BoundedLineReader(process.StandardOutput.BaseStream, MaxResponseBytes);
            await WriteRequestAsync(process.StandardInput.BaseStream, 1, "initialize", includeInitializeParams: true,
                linked.Token).ConfigureAwait(false);
            var initialize = await ReadResponseAsync(reader, 1, linked.Token).ConfigureAwait(false);
            if (initialize.RateLimitSignaled) return KiroAcquisition.RetrySuppressed();
            if (initialize.Error || initialize.Result is null)
                return KiroAcquisition.Unsupported("acp_initialize_failed");
            if (!AdvertisesUsageMethod(initialize.Result.Value))
                return KiroAcquisition.Unsupported("usage_method_unsupported");

            await WriteRequestAsync(process.StandardInput.BaseStream, 2, UsageMethod, includeInitializeParams: false,
                linked.Token).ConfigureAwait(false);
            var usage = await ReadResponseAsync(reader, 2, linked.Token).ConfigureAwait(false);
            if (usage.RateLimitSignaled) return KiroAcquisition.RetrySuppressed();
            if (usage.Error || usage.Result is null)
                return KiroAcquisition.Error("usage_query_failed");

            var payload = System.Text.Encoding.UTF8.GetBytes(usage.Result.Value.GetRawText());
            process.StandardInput.Close();
            await StopChildAsync(process, timeProvider, requireGracefulExit: true).ConfigureAwait(false);
            return KiroAcquisition.Ready(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return KiroAcquisition.Error("request_timeout");
        }
        catch (InvalidDataException)
        {
            return KiroAcquisition.Error("response_too_large");
        }
        catch (JsonException)
        {
            return KiroAcquisition.Error("protocol_invalid");
        }
        catch (IOException)
        {
            return KiroAcquisition.Unavailable("cli_io_error");
        }
        catch (InvalidOperationException)
        {
            return KiroAcquisition.Error("protocol_invalid");
        }
        finally
        {
            stderrStop.Cancel();
            if (!process.HasExited) TryKill(process);
            await StopChildAsync(process, timeProvider, requireGracefulExit: false).ConfigureAwait(false);
            await ObserveAsync(stderrDrain).ConfigureAwait(false);
        }
    }

    public static Task<KiroIdentityObservation> ReadIdentityAsync(TimeProvider timeProvider,
        byte[] identityKey, CancellationToken cancellationToken) =>
        ReadIdentityAsync(timeProvider, identityKey, cancellationToken, CreateIdentityStartInfo(), RequestTimeout);

    internal static async Task<KiroIdentityObservation> ReadIdentityAsync(TimeProvider timeProvider,
        byte[] identityKey, CancellationToken cancellationToken, ProcessStartInfo startInfo,
        TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(identityKey);
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return KiroIdentityObservation.Unavailable();
        }
        catch (Win32Exception) { return KiroIdentityObservation.Unavailable(); }
        catch (FileNotFoundException) { return KiroIdentityObservation.Unavailable(); }
        catch (InvalidOperationException) { return KiroIdentityObservation.Unavailable(); }

        using var deadline = new CancellationTokenSource(requestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var stderrStop = new CancellationTokenSource();
        var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, stderrStop.Token);
        byte[]? payload = null;
        try
        {
            process.StandardInput.Close();
            payload = await ReadBoundedOutputAsync(process.StandardOutput.BaseStream, linked.Token)
                .ConfigureAwait(false);
            await StopChildAsync(process, timeProvider, requireGracefulExit: true).ConfigureAwait(false);
            var observation = ParseIdentity(payload, identityKey);
            return process.ExitCode == 0 || observation.Status == KiroIdentityStatus.SignedOut
                ? observation
                : KiroIdentityObservation.Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return KiroIdentityObservation.Unavailable();
        }
        catch (InvalidDataException)
        {
            return KiroIdentityObservation.Unavailable();
        }
        catch (IOException)
        {
            return KiroIdentityObservation.Unavailable();
        }
        catch (InvalidOperationException)
        {
            return KiroIdentityObservation.Unavailable();
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            stderrStop.Cancel();
            if (!process.HasExited) TryKill(process);
            await StopChildAsync(process, timeProvider, requireGracefulExit: false).ConfigureAwait(false);
            await ObserveAsync(stderrDrain).ConfigureAwait(false);
        }
    }

    internal static KiroIdentityObservation ParseIdentity(ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> identityKey)
    {
        if (payload.Length is 0 or > MaxResponseBytes || identityKey.Length != 32)
            return KiroIdentityObservation.Unavailable();

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("account", out var account))
                return KiroIdentityObservation.Unavailable();
            if (account.ValueKind == JsonValueKind.Null) return KiroIdentityObservation.SignedOut();
            if (account.ValueKind != JsonValueKind.Object) return KiroIdentityObservation.Unavailable();

            return KiroIdentityObservation.Ready(HMACSHA256.HashData(identityKey, payload.Span));
        }
        catch (JsonException) { return KiroIdentityObservation.Unavailable(); }
    }

    internal static bool AdvertisesUsageMethod(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object ||
            !initializeResult.TryGetProperty("agentCapabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("_meta", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("kiro", out var kiro) || kiro.ValueKind != JsonValueKind.Object ||
            !kiro.TryGetProperty("extensionMethods", out var methods) || methods.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var method in methods.EnumerateArray())
        {
            if (method.ValueKind == JsonValueKind.String &&
                StringComparer.Ordinal.Equals(method.GetString(), UsageMethod)) return true;
        }

        return false;
    }

    private static async Task WriteRequestAsync(Stream input, int id, string method, bool includeInitializeParams,
        CancellationToken cancellationToken)
    {
        byte[] json;
        if (includeInitializeParams)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = new
                {
                    protocolVersion = 1,
                    clientCapabilities = new { },
                    clientInfo = new
                    {
                        name = "Cerith",
                        version = typeof(KiroAcpUsageClient).Assembly.GetName().Version?.ToString() ?? "unknown"
                    }
                }
            });
        }
        else
        {
            json = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method });
        }

        var message = new byte[json.Length + 1];
        json.CopyTo(message, 0);
        message[^1] = (byte)'\n';
        await input.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AcpResponse> ReadResponseAsync(BoundedLineReader reader, int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new IOException("ACP process closed its output.");
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(version.GetString(), "2.0"))
                throw new JsonException("ACP response is invalid.");

            if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String &&
                StringComparer.Ordinal.Equals(method.GetString(), RateLimitMethod))
                return new AcpResponse(false, null, true);
            if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out _))
                throw new JsonException("Unexpected ACP server request.");
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt32(out var responseId) || responseId != expectedId)
                continue;

            if (root.TryGetProperty("error", out _)) return new AcpResponse(true, null, false);
            if (!root.TryGetProperty("result", out var result)) return new AcpResponse(true, null, false);
            return new AcpResponse(false, result.Clone(), false);
        }
    }

    private static async Task StopChildAsync(Process process, TimeProvider timeProvider, bool requireGracefulExit)
    {
        if (process.HasExited) return;
        using var exitDeadline = new CancellationTokenSource(ExitTimeout, timeProvider);
        try
        {
            await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!requireGracefulExit)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return; }
        catch (Win32Exception) { return; }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (IOException) { return; }
        catch (ObjectDisposedException) { return; }
        catch (OperationCanceledException) { return; }
    }

    private static async Task<byte[]> ReadBoundedOutputAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                if (output.Length + count > MaxResponseBytes)
                    throw new InvalidDataException("CLI identity output exceeded its limit.");
                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(output.GetBuffer());
        }
    }

    private readonly record struct AcpResponse(bool Error, JsonElement? Result, bool RateLimitSignaled);

    internal sealed class BoundedLineReader(Stream stream, int maximumBytes)
    {
        private readonly byte[] _buffer = new byte[4096];
        private readonly MemoryStream _line = new();
        private int _offset;
        private int _count;

        public async Task<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_offset == _count)
                {
                    _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        if (_line.Length == 0) return null;
                        var final = _line.ToArray();
                        _line.SetLength(0);
                        return final;
                    }
                }

                var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
                var segmentLength = newline < 0 ? _count - _offset : newline - _offset;
                if (_line.Length + segmentLength > maximumBytes)
                    throw new InvalidDataException("ACP response exceeded its limit.");
                _line.Write(_buffer, _offset, segmentLength);
                _offset = newline < 0 ? _count : newline + 1;

                if (newline >= 0)
                {
                    var result = _line.ToArray();
                    _line.SetLength(0);
                    if (result.Length > 0 && result[^1] == (byte)'\r') Array.Resize(ref result, result.Length - 1);
                    return result;
                }
            }
        }
    }
}
