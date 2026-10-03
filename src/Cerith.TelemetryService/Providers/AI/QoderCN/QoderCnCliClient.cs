#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService;

internal sealed class QoderCnCliClient
{
    internal const string ExecutableFileName = "qoderclicn.exe";
    // The CN SDK declares qoderclicn first and qodercn as its command alias.
    private static readonly string[] ExecutableFileNames = [ExecutableFileName, "qodercn.exe"];
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
        cancellationToken.ThrowIfCancellationRequested();
        if (_executablePath is null)
            return QoderCnQuotaReading.Missing("cli_not_found");

        using var verifiedExecutable = QoderCnCliTrust.OpenVerifiedExecutable(_executablePath);
        if (verifiedExecutable is null)
            return QoderCnQuotaReading.Failure("cli_untrusted");

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
                try
                {
                    await StopProcessAsync(process).ConfigureAwait(false);
                    if (stderrDrain is not null)
                        await stderrDrain.WaitAsync(StopTimeout).ConfigureAwait(false);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    internal static string? FindExecutableFromPath(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return null;

        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var executableName in ExecutableFileNames)
        {
            foreach (var directory in directories)
            {
                if (!Path.IsPathFullyQualified(directory))
                    continue;

                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, executableName));
                    if (!QoderCnCliTrust.IsLocalFixedPath(candidate))
                        continue;
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
                catch (System.Security.SecurityException)
                {
                    continue;
                }
            }
        }

        return null;
    }

    internal static bool IsSupportedExecutableName(string fileName) =>
        ExecutableFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

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

internal static class QoderCnCliTrust
{
    private const string ExpectedPublisherName = "BRIGHT ZENITH PRIVATE LIMITED";
    internal const string ExpectedPublisherSubject = "CN=" + ExpectedPublisherName + ", O=" + ExpectedPublisherName;

    private const long MaximumExecutableBytes = 256L * 1024 * 1024;
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateVerify = 1;
    private const uint WinTrustStateClose = 2;
    private const uint WinTrustRevocationCheckWholeChain = 1;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x1000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static bool IsLocalFixedPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path))
            return false;

        var root = Path.GetPathRoot(path);
        if (root is null || root.Length != 3 || root[1] != Path.VolumeSeparatorChar)
            return false;

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool IsExpectedPublisherSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return false;

        string? commonName = null;
        string? organization = null;
        foreach (var attribute in subject.Split(','))
        {
            var separator = attribute.IndexOf('=');
            if (separator <= 0)
                return false;

            var key = attribute[..separator].Trim();
            var value = attribute[(separator + 1)..].Trim();
            if (key.Equals("CN", StringComparison.OrdinalIgnoreCase) || key.Equals("2.5.4.3", StringComparison.Ordinal))
            {
                if (commonName is not null)
                    return false;
                commonName = value;
            }
            else if (key.Equals("O", StringComparison.OrdinalIgnoreCase) || key.Equals("2.5.4.10", StringComparison.Ordinal))
            {
                if (organization is not null)
                    return false;
                organization = value;
            }
            else
            {
                return false;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Equals(commonName, ExpectedPublisherName) &&
            StringComparer.OrdinalIgnoreCase.Equals(organization, ExpectedPublisherName);
    }

    internal static FileStream? OpenVerifiedExecutable(string path) =>
        OpenVerifiedExecutable(path, VerifyAuthenticode, ReadSignerSubject);

    internal static FileStream? OpenVerifiedExecutable(string path, Func<string, IntPtr, bool> verifySignature,
        Func<string, string?> readSignerSubject)
    {
        ArgumentNullException.ThrowIfNull(verifySignature);
        ArgumentNullException.ThrowIfNull(readSignerSubject);

        FileStream? stream = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!QoderCnCliClient.IsSupportedExecutableName(Path.GetFileName(fullPath)) || !IsLocalFixedPath(fullPath))
                return null;

            var file = new FileInfo(fullPath);
            if (!file.Exists || file.Length <= 0 || file.Length > MaximumExecutableBytes)
                return null;

            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumExecutableBytes ||
                !verifySignature(fullPath, stream.SafeFileHandle.DangerousGetHandle()) ||
                !IsExpectedPublisherSubject(readSignerSubject(fullPath)))
            {
                stream.Dispose();
                return null;
            }

            return stream;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or ExternalException or ArgumentException or
            System.Security.SecurityException or NotSupportedException or InvalidOperationException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            stream?.Dispose();
            return null;
        }
    }

    private static string? ReadSignerSubject(string path)
    {
#pragma warning disable SYSLIB0057 // X509CertificateLoader accepts certificate files, not signer certificates embedded in PE files.
        using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        return signer.Subject;
    }

    private static bool VerifyAuthenticode(string path, IntPtr fileHandle)
    {
        IntPtr pathPointer = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        var data = new WinTrustData
        {
            UiChoice = WinTrustUiNone,
            RevocationChecks = WinTrustRevocationCheckWholeChain,
            UnionChoice = WinTrustChoiceFile,
            StateAction = WinTrustStateVerify,
            ProviderFlags = WinTrustCacheOnlyUrlRetrieval
        };

        try
        {
            pathPointer = Marshal.StringToHGlobalUni(path);
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                FileHandle = fileHandle
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            data.Size = (uint)Marshal.SizeOf<WinTrustData>();
            data.FileInfo = fileInfoPointer;

            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            try
            {
                if (data.StateData != IntPtr.Zero)
                {
                    data.StateAction = WinTrustStateClose;
                    var action = GenericVerifyV2;
                    _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                }
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(fileInfoPointer);
                if (pathPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(pathPointer);
            }
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
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
