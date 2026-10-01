#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerith.TelemetryService.Providers.AI.Qoder;

internal sealed record QoderOwnerKey(string AccountId, string? OrganizationId, int ProcessId,
    long ProcessStartTicks, int WindowsSessionId, long EndpointWriteTicks, string PipeFingerprint);

internal sealed record QoderQuotaAcquisition(QoderOwnerKey? Owner, QoderQuotaReading? Reading,
    MetricState FailureState, string? ErrorCode)
{
    public static QoderQuotaAcquisition Failure(string code, MetricState state, QoderOwnerKey? owner = null) =>
        new(owner, null, state, code);
}

internal enum QoderOwnerVerificationState
{
    Verified,
    Changed,
    Unverifiable
}

internal sealed record QoderOwnerVerification(QoderOwnerVerificationState State, QoderOwnerKey? Owner,
    string? ErrorCode = null);

internal sealed class QoderQuotaAcquirer : IDisposable
{
    internal static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(3);
    internal const int MaxInfoBytes = 8 * 1024;
    internal const int MaxHeaderBytes = 8 * 1024;
    internal const int MaxBodyBytes = 256 * 1024;
    private const string PipePrefix = @"\\.\pipe\";
    private const uint TokenQuery = 0x0008;
    private const uint TokenUser = 1;
    private const uint DriveTypeFixed = 3;
    private const uint WtdUiNone = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const uint WtdChoiceFile = 1;
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private int _disposed;

    public async Task<QoderQuotaAcquisition> AcquireOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!OperatingSystem.IsWindows())
            return QoderQuotaAcquisition.Failure("unsupported_platform", MetricState.Unavailable);
        if (!TryReadEndpoint(out var endpoint, out var endpointError))
            return QoderQuotaAcquisition.Failure(endpointError, MetricState.Unavailable);

        using var deadline = new CancellationTokenSource(AcquisitionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await _rpcLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var authBefore = await CallAsync(endpoint, "auth/status", linked.Token).ConfigureAwait(false);
            if (!QoderQuotaParser.TryParseAuthStatus(authBefore.Result, out var identityBefore) || identityBefore is null)
                return QoderQuotaAcquisition.Failure("identity_unavailable", MetricState.Unavailable);

            using var usage = await CallAsync(endpoint, "credit/usage", linked.Token).ConfigureAwait(false);
            var observedAt = DateTimeOffset.UtcNow;
            var reading = QoderQuotaParser.Parse(usage.Result, identityBefore, observedAt);

            using var authAfter = await CallAsync(endpoint, "auth/status", linked.Token).ConfigureAwait(false);
            if (!QoderQuotaParser.TryParseAuthStatus(authAfter.Result, out var identityAfter) || identityAfter is null)
                return QoderQuotaAcquisition.Failure("identity_unavailable", MetricState.Unavailable);

            if (!SameIdentity(identityBefore, identityAfter) ||
                !StringComparer.Ordinal.Equals(reading.AccountId, identityBefore.AccountId) ||
                !SameEndpoint(endpoint, ReadEndpoint()))
                return QoderQuotaAcquisition.Failure("source_changed", MetricState.Unavailable);

            if (!SamePeer(authBefore.Peer, usage.Peer) || !SamePeer(usage.Peer, authAfter.Peer))
                return QoderQuotaAcquisition.Failure("source_changed", MetricState.Unavailable);

            var owner = CreateOwnerKey(identityAfter, authAfter.Peer);
            return new QoderQuotaAcquisition(owner, reading, MetricState.Unavailable, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return QoderQuotaAcquisition.Failure("timeout", MetricState.Unavailable);
        }
        catch (QoderRpcException exception)
        {
            return QoderQuotaAcquisition.Failure(exception.ErrorCode, MetricState.Unavailable);
        }
        catch (IOException)
        {
            return QoderQuotaAcquisition.Failure("source_unavailable", MetricState.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return QoderQuotaAcquisition.Failure("source_unavailable", MetricState.Unavailable);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    public async Task<QoderOwnerVerification> VerifyOwnerAsync(QoderOwnerKey expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!OperatingSystem.IsWindows() || !TryReadEndpoint(out var endpoint, out _))
            return new QoderOwnerVerification(QoderOwnerVerificationState.Unverifiable, null, "identity_unavailable");

        using var deadline = new CancellationTokenSource(RpcTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await _rpcLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var auth = await CallAsync(endpoint, "auth/status", linked.Token).ConfigureAwait(false);
            if (!QoderQuotaParser.TryParseAuthStatus(auth.Result, out var identity) || identity is null)
                return new QoderOwnerVerification(QoderOwnerVerificationState.Unverifiable, null,
                    "identity_unavailable");

            if (!SameEndpoint(endpoint, ReadEndpoint()))
                return new QoderOwnerVerification(QoderOwnerVerificationState.Changed, null);

            var current = CreateOwnerKey(identity, auth.Peer);
            return expected.Equals(current)
                ? new QoderOwnerVerification(QoderOwnerVerificationState.Verified, current)
                : new QoderOwnerVerification(QoderOwnerVerificationState.Changed, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new QoderOwnerVerification(QoderOwnerVerificationState.Unverifiable, null, "timeout");
        }
        catch (QoderRpcException exception)
        {
            return new QoderOwnerVerification(QoderOwnerVerificationState.Unverifiable, null,
                exception.ErrorCode);
        }
        catch (IOException)
        {
            return new QoderOwnerVerification(QoderOwnerVerificationState.Unverifiable, null,
                "source_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new QoderOwnerVerification(QoderOwnerVerificationState.Unverifiable, null,
                "source_unavailable");
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    public void Dispose()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
    }

    internal static bool TryGetLocalPipeName(string? ipcServerPath, out string pipeName)
    {
        pipeName = string.Empty;
        if (string.IsNullOrWhiteSpace(ipcServerPath) ||
            !ipcServerPath.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = ipcServerPath[PipePrefix.Length..];
        if (candidate.Length is 0 or > 240 || candidate.StartsWith('\\') || candidate.EndsWith('\\'))
            return false;

        var segments = candidate.Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
            foreach (var character in segment)
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '{' or '}' or '(' or ')'))
                    return false;
            }
        }

        pipeName = candidate;
        return true;
    }

    internal static bool IsLocalDrivePath(string? path) =>
        path is { Length: >= 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
        path[2] is '\\' or '/';

    private async Task<QoderRpcResult> CallAsync(QoderEndpoint endpoint, string method,
        CancellationToken cancellationToken)
    {
        using var stream = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var peer = ReadTrustedPeer(stream.SafePipeHandle, endpoint);
        using var requestDeadline = new CancellationTokenSource(RpcTimeout);
        using var requestLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            requestDeadline.Token);
        try
        {
            var result = await QoderRpcFrame.SendAsync(stream, method, requestLinked.Token).ConfigureAwait(false);
            return new QoderRpcResult(result, peer);
        }
        catch (OperationCanceledException) when (requestDeadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new QoderRpcException("timeout");
        }
    }

    private static QoderPeerIdentity ReadTrustedPeer(SafePipeHandle pipeHandle, QoderEndpoint endpoint)
    {
        if (!GetNamedPipeServerProcessId(pipeHandle, out var serverProcessId) || serverProcessId == 0)
            throw new QoderRpcException("peer_unavailable");

        try
        {
            using var process = Process.GetProcessById(checked((int)serverProcessId));
            var currentSessionId = Process.GetCurrentProcess().SessionId;
            if (process.SessionId != currentSessionId)
                throw new QoderRpcException("peer_untrusted");

            var imagePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(imagePath) || !IsTrustedQoderImage(process.ProcessName, imagePath))
                throw new QoderRpcException("peer_untrusted");

            var currentSid = WindowsIdentity.GetCurrent().User?.Value;
            var serverSid = ReadProcessUserSid(process);
            if (currentSid is null || serverSid is null || !StringComparer.Ordinal.Equals(currentSid, serverSid))
                throw new QoderRpcException("peer_untrusted");

            var startTicks = process.StartTime.ToUniversalTime().Ticks;
            return new QoderPeerIdentity(checked((int)serverProcessId), startTicks, currentSessionId,
                endpoint.WriteTicks, endpoint.PipeFingerprint);
        }
        catch (ArgumentException)
        {
            throw new QoderRpcException("peer_unavailable");
        }
        catch (InvalidOperationException)
        {
            throw new QoderRpcException("peer_unavailable");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new QoderRpcException("peer_unavailable");
        }
    }

    private static bool IsTrustedQoderImage(string processName, string imagePath)
    {
        var fileName = Path.GetFileName(imagePath);
        var expectedName = StringComparer.OrdinalIgnoreCase.Equals(fileName, "Qoder IDE.exe") ||
            StringComparer.OrdinalIgnoreCase.Equals(fileName, "Qoder.exe");
        if (!expectedName || !(StringComparer.OrdinalIgnoreCase.Equals(processName, "Qoder IDE") ||
                StringComparer.OrdinalIgnoreCase.Equals(processName, "Qoder")))
            return false;

        try
        {
            var productName = FileVersionInfo.GetVersionInfo(imagePath).ProductName;
            if (!(StringComparer.OrdinalIgnoreCase.Equals(productName, "Qoder IDE") ||
                    StringComparer.OrdinalIgnoreCase.Equals(productName, "Qoder")))
                return false;
            return VerifyAuthenticode(imagePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool VerifyAuthenticode(string imagePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
            FilePath = imagePath
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevocationCheckNone,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                ProviderFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
            };
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
            var action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, trustDataPointer) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(trustDataPointer);
        }
    }

    private static string? ReadProcessUserSid(Process process)
    {
        if (!OpenProcessToken(process.Handle, TokenQuery, out var token))
            return null;
        try
        {
            _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var requiredBytes);
            if (requiredBytes == 0)
                return null;
            var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, requiredBytes, out _))
                    return null;
                var sidPointer = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sidPointer).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static bool TryReadEndpoint(out QoderEndpoint endpoint, out string errorCode)
    {
        endpoint = default;
        errorCode = "endpoint_unavailable";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            return false;

        string path;
        byte[]? bytes = null;
        try
        {
            appData = Path.GetFullPath(appData);
            var root = Path.GetPathRoot(appData);
            var endpointDirectory = Path.Combine(appData, "Qoder", "SharedClientCache");
            if (root is null || !IsLocalDrivePath(appData) || GetDriveType(root) != DriveTypeFixed ||
                !HasOnlyLocalDirectories(endpointDirectory, root))
                return false;

            path = Path.Combine(endpointDirectory, ".info.json");
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;

            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxInfoBytes)
            {
                errorCode = "endpoint_invalid";
                return false;
            }

            bytes = new byte[checked((int)info.Length)];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan))
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = file.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        errorCode = "endpoint_changed";
                        return false;
                    }
                    offset += read;
                }
                if (file.ReadByte() != -1)
                {
                    errorCode = "endpoint_invalid";
                    return false;
                }
            }

            if (!TryReadPipePath(bytes, out var pipePath) || !TryGetLocalPipeName(pipePath, out var pipeName))
            {
                errorCode = "endpoint_invalid";
                return false;
            }

            var pathBytes = Encoding.UTF8.GetBytes(pipeName);
            try
            {
                var fingerprint = Convert.ToHexString(SHA256.HashData(pathBytes));
                endpoint = new QoderEndpoint(pipeName, info.LastWriteTimeUtc.Ticks, fingerprint);
                errorCode = string.Empty;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pathBytes);
            }
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            errorCode = "endpoint_invalid";
            return false;
        }
        catch (ArgumentException)
        {
            errorCode = "endpoint_invalid";
            return false;
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool HasOnlyLocalDirectories(string path, string root)
    {
        var current = new DirectoryInfo(path);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (current is not null)
        {
            var attributes = current.Attributes;
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            var currentPath = current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (StringComparer.OrdinalIgnoreCase.Equals(currentPath, normalizedRoot))
                return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool TryReadPipePath(ReadOnlySpan<byte> json, out string pipePath)
    {
        pipePath = string.Empty;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        var found = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;
            var isPath = reader.ValueTextEquals("ipcServerPath");
            if (!reader.Read())
                return false;
            if (!isPath)
            {
                reader.Skip();
                continue;
            }
            if (found || reader.TokenType != JsonTokenType.String)
                return false;
            pipePath = reader.GetString() ?? string.Empty;
            found = true;
        }

        return found && !string.IsNullOrWhiteSpace(pipePath);
    }

    private static QoderEndpoint? ReadEndpoint() => TryReadEndpoint(out var endpoint, out _) ? endpoint : null;

    private static bool SameEndpoint(QoderEndpoint endpoint, QoderEndpoint? current) =>
        current is not null && endpoint.Equals(current.Value);

    private static bool SamePeer(QoderPeerIdentity left, QoderPeerIdentity right) => left.Equals(right);

    private static bool SameIdentity(QoderSessionIdentity left, QoderSessionIdentity right) =>
        StringComparer.Ordinal.Equals(left.AccountId, right.AccountId) &&
        StringComparer.Ordinal.Equals(left.OrganizationId, right.OrganizationId);

    private static QoderOwnerKey CreateOwnerKey(QoderSessionIdentity identity, QoderPeerIdentity peer) =>
        new(identity.AccountId, identity.OrganizationId, peer.ProcessId, peer.ProcessStartTicks,
            peer.WindowsSessionId, peer.EndpointWriteTicks, peer.PipeFingerprint);

    private readonly record struct QoderEndpoint(string PipeName, long WriteTicks, string PipeFingerprint);

    private readonly record struct QoderPeerIdentity(int ProcessId, long ProcessStartTicks, int WindowsSessionId,
        long EndpointWriteTicks, string PipeFingerprint);

    private sealed class QoderRpcResult(QoderRpcPayload result, QoderPeerIdentity peer) : IDisposable
    {
        public ReadOnlyMemory<byte> Result => result.Memory;
        public QoderPeerIdentity Peer { get; } = peer;
        public void Dispose() => result.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
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
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, uint tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, [In] ref Guid actionId, IntPtr trustData);

    internal sealed class QoderRpcException(string errorCode) : IOException("Qoder local RPC failed.")
    {
        public string ErrorCode { get; } = errorCode;
    }
}

internal sealed class QoderRpcPayload : IDisposable
{
    private byte[]? _buffer;
    private readonly int _offset;
    private readonly int _length;

    internal QoderRpcPayload(byte[] buffer, int offset, int length)
    {
        _buffer = buffer;
        _offset = offset;
        _length = length;
    }

    public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(QoderRpcPayload)))
        .AsMemory(_offset, _length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            CryptographicOperations.ZeroMemory(buffer);
    }
}

internal static class QoderRpcFrame
{
    private static readonly byte[] FrameTerminator = [13, 10, 13, 10];

    internal static async Task<QoderRpcPayload> SendAsync(Stream stream, string method,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = new { }
        });
        var header = Encoding.ASCII.GetBytes($"Content-Length: {request.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var body = await ReadBodyAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryFindResult(body, out var offset, out var length))
                throw new QoderQuotaAcquirer.QoderRpcException("rpc_response_invalid");
            return new QoderRpcPayload(body, offset, length);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(body);
            throw;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[QoderQuotaAcquirer.MaxHeaderBytes];
        var count = 0;
        while (count < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new QoderQuotaAcquirer.QoderRpcException("rpc_response_invalid");
            count += read;
            if (count >= FrameTerminator.Length && header.AsSpan(count - FrameTerminator.Length,
                    FrameTerminator.Length).SequenceEqual(FrameTerminator))
                break;
        }
        if (count == header.Length && !header.AsSpan(count - FrameTerminator.Length,
                FrameTerminator.Length).SequenceEqual(FrameTerminator))
            throw new QoderQuotaAcquirer.QoderRpcException("rpc_header_too_large");

        var bodyLength = ParseContentLength(header.AsSpan(0, count - FrameTerminator.Length));
        var body = new byte[bodyLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                CryptographicOperations.ZeroMemory(body);
                throw new QoderQuotaAcquirer.QoderRpcException("rpc_response_invalid");
            }
            offset += read;
        }
        return body;
    }

    private static int ParseContentLength(ReadOnlySpan<byte> header)
    {
        var text = Encoding.ASCII.GetString(header);
        var contentLength = -1;
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new QoderQuotaAcquirer.QoderRpcException("rpc_header_invalid");
            var name = line.AsSpan(0, separator).Trim();
            var value = line.AsSpan(separator + 1).Trim();
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                throw new QoderQuotaAcquirer.QoderRpcException("rpc_header_invalid");
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (contentLength >= 0 || value.IsEmpty)
                throw new QoderQuotaAcquirer.QoderRpcException("rpc_header_invalid");
            foreach (var character in value)
            {
                if (!char.IsAsciiDigit(character))
                    throw new QoderQuotaAcquirer.QoderRpcException("rpc_header_invalid");
            }
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                contentLength is <= 0 or > QoderQuotaAcquirer.MaxBodyBytes)
                throw new QoderQuotaAcquirer.QoderRpcException("rpc_body_too_large");
        }
        if (contentLength < 0)
            throw new QoderQuotaAcquirer.QoderRpcException("rpc_header_invalid");
        return contentLength;
    }

    private static bool TryFindResult(ReadOnlySpan<byte> body, out int resultOffset, out int resultLength)
    {
        resultOffset = 0;
        resultLength = 0;
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = QoderQuotaParser.MaxJsonDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            var isJsonRpc = false;
            var hasValidId = false;
            var hasError = false;
            var hasResult = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return false;
                var isVersion = reader.ValueTextEquals("jsonrpc");
                var isId = reader.ValueTextEquals("id");
                var isError = reader.ValueTextEquals("error");
                var isResult = reader.ValueTextEquals("result");
                if (!reader.Read())
                    return false;

                if (isVersion)
                {
                    isJsonRpc = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("2.0");
                    reader.Skip();
                }
                else if (isId)
                {
                    hasValidId = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var id) && id == 1;
                    reader.Skip();
                }
                else if (isError)
                {
                    hasError = reader.TokenType != JsonTokenType.Null;
                    reader.Skip();
                }
                else if (isResult)
                {
                    if (hasResult || reader.TokenType != JsonTokenType.StartObject)
                        return false;
                    hasResult = true;
                    resultOffset = checked((int)reader.TokenStartIndex);
                    reader.Skip();
                    resultLength = checked((int)reader.BytesConsumed - resultOffset);
                }
                else
                {
                    reader.Skip();
                }
            }

            return reader.TokenType == JsonTokenType.EndObject && !reader.Read() && isJsonRpc && hasValidId &&
                !hasError && hasResult && resultLength > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
