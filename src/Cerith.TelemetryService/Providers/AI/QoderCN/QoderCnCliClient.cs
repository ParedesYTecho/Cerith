#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService;

internal sealed class QoderCnCliClient
{
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private const int InitializeTimeoutMilliseconds = 30_000;
    private const string SettingsOverride = "{\"general\":{\"enableAutoUpdate\":false,\"fileCheckpointing\":{\"enabled\":false}},\"privacy\":{\"usageStatisticsEnabled\":false},\"aiCodeStatistics\":{\"enabled\":false},\"hooks\":{\"PreToolUse\":[],\"PostToolUse\":[],\"PostToolUseFailure\":[],\"UserPromptSubmit\":[],\"SessionStart\":[],\"SessionEnd\":[],\"Stop\":[],\"SubagentStart\":[],\"SubagentStop\":[],\"PreCompact\":[],\"PostCompact\":[],\"CwdChanged\":[],\"InstructionsLoaded\":[],\"FileChanged\":[],\"PermissionRequest\":[],\"PermissionDenied\":[],\"WorktreeCreate\":[],\"WorktreeRemove\":[]}}";

    private readonly string? _executablePath;
    private readonly TimeSpan _processTimeout;

    public QoderCnCliClient()
        : this(FindExecutableFromPath(Environment.GetEnvironmentVariable("PATH")), ProcessTimeout)
    {
    }

    internal QoderCnCliClient(string? executablePath, TimeSpan? processTimeout = null)
    {
        _executablePath = executablePath;
        _processTimeout = processTimeout ?? ProcessTimeout;
        if (_processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
    }

    internal async Task<QoderCnQuotaReading> GetUsageAsync(CancellationToken cancellationToken)
    {
        if (_executablePath is null)
            return QoderCnQuotaReading.Missing("cli_not_found");

        Process? process = null;
        Task? stderrDrain = null;
        using var deadline = new CancellationTokenSource(_processTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            var startInfo = CreateStartInfo(_executablePath);
            var candidate = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!candidate.Start())
            {
                candidate.Dispose();
                return QoderCnQuotaReading.Failure("cli_start_failed");
            }

            process = candidate;
            stderrDrain = DrainStandardErrorAsync(process.StandardError.BaseStream);
            return await QueryUsageAsync(process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return QoderCnQuotaReading.Failure("cli_timeout");
        }
        catch (QoderCnCliProtocolException exception)
        {
            return QoderCnQuotaReading.Failure(exception.Code);
        }
        catch (Win32Exception)
        {
            return QoderCnQuotaReading.Failure("cli_start_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return QoderCnQuotaReading.Failure("cli_access_denied");
        }
        catch (IOException)
        {
            return QoderCnQuotaReading.Failure("cli_io");
        }
        catch (InvalidOperationException)
        {
            return QoderCnQuotaReading.Failure("cli_start_failed");
        }
        finally
        {
            if (process is not null)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                if (stderrDrain is not null)
                    await stderrDrain.WaitAsync(StopTimeout).ConfigureAwait(false);
                process.Dispose();
            }
        }
    }

    internal static string? FindExecutableFromPath(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;

            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, "qodercn.exe"));
                if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.Directory) == 0)
                    return candidate;
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var profileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profileDirectory))
            throw new InvalidOperationException("The current user profile is unavailable.");

        var configDirectory = Path.Combine(profileDirectory, ".qoder-cn");
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--input-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--no-session-persistence");
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("--strict-mcp-config");
        startInfo.ArgumentList.Add("--settings");
        startInfo.ArgumentList.Add(SettingsOverride);
        startInfo.ArgumentList.Add("--config-dir");
        startInfo.ArgumentList.Add(configDirectory);

        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (name.StartsWith("QODER", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase) || IsProxyOrCustomTrustVariable(name))
                startInfo.Environment.Remove(name);
        }

        startInfo.Environment["CI"] = "1";
        startInfo.Environment["QODERCN_CONFIG_DIR"] = configDirectory;
        return startInfo;
    }

    internal static async Task<QoderCnQuotaReading> QueryUsageAsync(Stream input, Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var reader = new QoderCnCliOutputReader(output);
        var initializeFields = new Dictionary<string, object?>
        {
            ["modelPolicyProvider"] = false,
            ["supportsCatalogReadyInitialize"] = true,
            ["supportsAvailableModelsUpdate"] = true,
            ["supportsCommandsChanged"] = true,
            ["initializeTimeoutMs"] = InitializeTimeoutMilliseconds
        };

        var initialize = await RequestAsync(input, reader, "initialize", initializeFields, cancellationToken)
            .ConfigureAwait(false);
        var initializedUserId = ReadAccountUserId(initialize);

        var accountBefore = await RequestAsync(input, reader, "account_info", null, cancellationToken)
            .ConfigureAwait(false);
        var accountBeforeUserId = ReadAccountUserId(accountBefore);
        if (string.IsNullOrWhiteSpace(accountBeforeUserId))
            return QoderCnQuotaReading.Missing("identity_missing");
        if (initializedUserId is not null &&
            !StringComparer.Ordinal.Equals(initializedUserId, accountBeforeUserId))
            return QoderCnQuotaReading.Failure("identity_changed");

        var usageResponse = await RequestAsync(input, reader, "get_usage_info", null, cancellationToken)
            .ConfigureAwait(false);
        var accountAfter = await RequestAsync(input, reader, "account_info", null, cancellationToken)
            .ConfigureAwait(false);
        var accountAfterUserId = ReadAccountUserId(accountAfter);

        if (string.IsNullOrWhiteSpace(accountAfterUserId) ||
            !StringComparer.Ordinal.Equals(accountBeforeUserId, accountAfterUserId))
            return QoderCnQuotaReading.Failure("identity_changed");

        if (usageResponse.ValueKind == JsonValueKind.Object &&
            usageResponse.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var usageUserId = ReadString(usage, "userId");
            if (string.IsNullOrWhiteSpace(usageUserId))
                return QoderCnQuotaReading.Failure("identity_missing");
            if (!StringComparer.Ordinal.Equals(accountBeforeUserId, usageUserId))
                return QoderCnQuotaReading.Failure("identity_changed");
        }

        var payload = Encoding.UTF8.GetBytes(usageResponse.GetRawText());
        return QoderCnQuotaParser.Parse(payload);
    }

    private static async Task<JsonElement> RequestAsync(Stream input, QoderCnCliOutputReader reader, string type,
        IReadOnlyDictionary<string, object?>? fields, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var frame = QoderCnCliProtocol.CreateControlRequest(requestId, type, fields);
        await input.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadAccountUserId(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(account, "userId");
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsProxyOrCustomTrustVariable(string name) => name.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NODE_EXTRA_CA_CERTS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SSL_CERT_FILE", StringComparison.OrdinalIgnoreCase);

    private static async Task DrainStandardErrorAsync(Stream stream)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                return;
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            TryKillProcess(process);
        }

        var exitedGracefully = false;
        using (var gracefulStop = new CancellationTokenSource(StopTimeout))
        {
            try
            {
                await process.WaitForExitAsync(gracefulStop.Token).ConfigureAwait(false);
                exitedGracefully = true;
            }
            catch (OperationCanceledException) when (gracefulStop.IsCancellationRequested)
            {
                exitedGracefully = false;
            }
        }

        if (exitedGracefully)
            return;

        TryKillProcess(process);
        using var forcedStop = new CancellationTokenSource(StopTimeout);
        try
        {
            await process.WaitForExitAsync(forcedStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (forcedStop.IsCancellationRequested)
        {
            throw new TimeoutException("Qoder CN CLI did not stop after its quota query.");
        }
    }

    private static bool TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

internal sealed class QoderCnCliProtocolException(string code) : Exception
{
    public string Code { get; } = code;
}

internal static class QoderCnCliProtocol
{
    public static byte[] CreateControlRequest(string requestId, string requestType,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "control_request");
            writer.WriteString("request_id", requestId);
            writer.WriteStartObject("request");
            writer.WriteString("type", requestType);
            writer.WriteString("subtype", requestType);
            if (fields is not null)
            {
                foreach (var (name, value) in fields)
                {
                    switch (value)
                    {
                        case bool boolean:
                            writer.WriteBoolean(name, boolean);
                            break;
                        case int integer:
                            writer.WriteNumber(name, integer);
                            break;
                        case string text:
                            writer.WriteString(name, text);
                            break;
                        default:
                            throw new ArgumentException("Unsupported Qoder CN control field type.", nameof(fields));
                    }
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var frame = buffer.ToArray();
        Array.Resize(ref frame, frame.Length + 1);
        frame[^1] = (byte)'\n';
        return frame;
    }
}

internal sealed class QoderCnCliOutputReader
{
    internal const int MaxFrameBytes = 1024 * 1024;
    internal const int MaxOutputBytes = 2 * 1024 * 1024;
    private const int ReadBufferBytes = 8192;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[ReadBufferBytes];
    private int _start;
    private int _end;
    private int _totalBytes;

    public QoderCnCliOutputReader(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public async Task<JsonElement> ReadResponseAsync(string requestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw new QoderCnCliProtocolException("response_missing");
            if (line.Length == 0)
                throw new QoderCnCliProtocolException("protocol_invalid");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            }
            catch (JsonException)
            {
                throw new QoderCnCliProtocolException("protocol_invalid");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String)
                    throw new QoderCnCliProtocolException("protocol_invalid");

                var typeName = type.GetString();
                if (typeName is "keep_alive" or "keepalive")
                    continue;
                if (typeName == "control_request")
                    throw new QoderCnCliProtocolException("unexpected_cli_request");
                if (typeName is "assistant" or "result" or "user")
                    throw new QoderCnCliProtocolException("unexpected_agent_activity");
                if (typeName != "control_response")
                    continue;

                if (!root.TryGetProperty("response", out var controlResponse) ||
                    controlResponse.ValueKind != JsonValueKind.Object ||
                    !controlResponse.TryGetProperty("request_id", out var responseId) ||
                    responseId.ValueKind != JsonValueKind.String)
                    throw new QoderCnCliProtocolException("protocol_invalid");
                if (!StringComparer.Ordinal.Equals(responseId.GetString(), requestId))
                    continue;

                if (!controlResponse.TryGetProperty("subtype", out var subtype) || subtype.ValueKind != JsonValueKind.String)
                    throw new QoderCnCliProtocolException("protocol_invalid");
                if (subtype.GetString() != "success")
                    throw new QoderCnCliProtocolException("control_request_failed");
                if (!controlResponse.TryGetProperty("response", out var response))
                    throw new QoderCnCliProtocolException("protocol_invalid");
                return response.Clone();
            }
        }
    }

    private async Task<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _start = 0;
                if (_end == 0)
                {
                    if (line.Length != 0)
                        throw new QoderCnCliProtocolException("protocol_invalid");
                    return null;
                }

                _totalBytes += _end;
                if (_totalBytes > MaxOutputBytes)
                    throw new QoderCnCliProtocolException("output_too_large");
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var count = newline >= 0 ? newline - _start : _end - _start;
            if (line.Length + count > MaxFrameBytes)
                throw new QoderCnCliProtocolException("frame_too_large");
            line.Write(_buffer, _start, count);
            _start += count;

            if (newline >= 0)
            {
                _start++;
                var result = line.ToArray();
                if (result.Length > 0 && result[^1] == (byte)'\r')
                    Array.Resize(ref result, result.Length - 1);
                return result;
            }
        }
    }
}
