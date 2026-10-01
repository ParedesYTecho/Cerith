#nullable enable

using System;
using System.IO;
using System.IO.Pipes;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.TelemetryService.Providers.AI.Qoder;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.Qoder;

[TestClass]
public sealed class QoderQuotaAcquirerTests
{
    [TestMethod]
    public void PipeTargetAcceptsOnlyBoundedLocalPipeNames()
    {
        Assert.IsTrue(QoderQuotaAcquirer.TryGetLocalPipeName(@"\\.\pipe\qoder-server-1", out var name));
        Assert.AreEqual("qoder-server-1", name);

        AssertRejected(@"\\server\pipe\qoder-server-1");
        AssertRejected(@"\\?\pipe\qoder-server-1");
        AssertRejected(@"\\.\pipe\..\qoder-server-1");
        AssertRejected(@"\\.\pipe\qoder\name:invalid");
        AssertRejected(@"C:\Users\synthetic\qoder.sock");
        AssertRejected(null);
    }

    [TestMethod]
    public void EndpointRootMustBeAnAbsoluteDrivePath()
    {
        Assert.IsTrue(QoderQuotaAcquirer.IsLocalDrivePath(@"C:\Users\synthetic\AppData\Roaming"));
        Assert.IsFalse(QoderQuotaAcquirer.IsLocalDrivePath(@"\\server\profile\AppData\Roaming"));
        Assert.IsFalse(QoderQuotaAcquirer.IsLocalDrivePath(@"C:relative\AppData"));
        Assert.IsFalse(QoderQuotaAcquirer.IsLocalDrivePath(@"\rooted-without-drive\AppData"));
    }

    [TestMethod]
    public void QoderProductMetadataDoesNotTrustAnUnapprovedPublisher()
    {
        var unapprovedCertificateHash = SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-unapproved-publisher"));

        Assert.IsFalse(QoderQuotaAcquirer.IsTrustedQoderImageDetails("Qoder IDE", "Qoder IDE.exe", "Qoder IDE",
            unapprovedCertificateHash));
    }

    [TestMethod]
    public void PeerValidationRejectsForgedPidSidSessionStartAndPublisher()
    {
        const string currentSid = "S-1-5-21-1000";
        const string peerSid = "S-1-5-21-2000";

        Assert.IsFalse(QoderQuotaAcquirer.IsTrustedPeerIdentity(5172, 5173, 1, 1, currentSid, currentSid,
            638950000000000000, 638950000000000000, true));
        Assert.IsFalse(QoderQuotaAcquirer.IsTrustedPeerIdentity(5172, 5172, 1, 1, currentSid, peerSid,
            638950000000000000, 638950000000000000, true));
        Assert.IsFalse(QoderQuotaAcquirer.IsTrustedPeerIdentity(5172, 5172, 2, 1, currentSid, currentSid,
            638950000000000000, 638950000000000000, true));
        Assert.IsFalse(QoderQuotaAcquirer.IsTrustedPeerIdentity(5172, 5172, 1, 1, currentSid, currentSid,
            638950000000000000, 638950000000000001, true));
        Assert.IsFalse(QoderQuotaAcquirer.IsTrustedPeerIdentity(5172, 5172, 1, 1, currentSid, currentSid,
            638950000000000000, 638950000000000000, false));
    }

    [TestMethod]
    public async Task PipeClientConnectsAnonymouslyWithoutExposingCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows named-pipe SQOS is available only on Windows.");

        var pipeName = $"qoder-anonymous-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = QoderQuotaAcquirer.CreatePipeClient(pipeName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var serverConnected = server.WaitForConnectionAsync(deadline.Token);

        await client.ConnectAsync(deadline.Token);
        await serverConnected;

        var callerSid = WindowsIdentity.GetCurrent().User?.Value;
        string? serverObservedSid = null;
        var impersonationDenied = false;
        try
        {
            server.RunAsClient(() => serverObservedSid = WindowsIdentity.GetCurrent().User?.Value);
        }
        catch (SecurityException)
        {
            impersonationDenied = true;
        }
        catch (UnauthorizedAccessException)
        {
            impersonationDenied = true;
        }

        Assert.IsTrue(impersonationDenied || !StringComparer.Ordinal.Equals(callerSid, serverObservedSid));
    }

    [TestMethod]
    public async Task RpcFrameSendsFixedMethodAndReturnsOnlyTheResultObject()
    {
        var response = CreateWire("""{"jsonrpc":"2.0","id":1,"result":{"userId":"synthetic-user-1"},"debug":"synthetic-secret"}""");
        using var stream = new ReplyStream(response);

        using var payload = await QoderRpcFrame.SendAsync(stream, "credit/usage", CancellationToken.None);

        using var result = JsonDocument.Parse(payload.Memory);
        Assert.AreEqual("synthetic-user-1", result.RootElement.GetProperty("userId").GetString());
        Assert.IsFalse(result.RootElement.TryGetProperty("debug", out _));

        var requestText = Encoding.UTF8.GetString(stream.Written.Span);
        var separator = requestText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.IsTrue(separator > 0);
        var requestBody = requestText[(separator + 4)..];
        Assert.IsTrue(requestText[..separator].StartsWith("Content-Length: ", StringComparison.Ordinal));
        var declaredLength = int.Parse(requestText[16..separator], System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(requestBody), declaredLength);
        using var request = JsonDocument.Parse(requestBody);
        Assert.AreEqual("2.0", request.RootElement.GetProperty("jsonrpc").GetString());
        Assert.AreEqual(1, request.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual("credit/usage", request.RootElement.GetProperty("method").GetString());
        Assert.AreEqual(JsonValueKind.Object, request.RootElement.GetProperty("params").ValueKind);
    }

    [TestMethod]
    public async Task RpcResponsePayloadBufferIsClearedWhenDisposed()
    {
        var response = CreateWire("""{"jsonrpc":"2.0","id":1,"result":{"id":"synthetic-user-1"},"debug":"synthetic-secret"}""");
        using var stream = new ReplyStream(response);
        var payload = await QoderRpcFrame.SendAsync(stream, "auth/status", CancellationToken.None);
        Assert.IsTrue(MemoryMarshal.TryGetArray(payload.Memory, out var segment));

        payload.Dispose();

        Assert.IsNotNull(segment.Array);
        foreach (var value in segment.Array!)
            Assert.AreEqual((byte)0, value);
    }

    [TestMethod]
    public async Task RpcFrameRejectsDuplicateLengthAndUnexpectedResponseId()
    {
        var body = """{"jsonrpc":"2.0","id":2,"result":{}}""";
        using var duplicateLength = new ReplyStream(CreateWire(body,
            extraHeaders: "Content-Length: 1\r\n"));
        var headerError = await Assert.ThrowsExactlyAsync<QoderQuotaAcquirer.QoderRpcException>(
            () => QoderRpcFrame.SendAsync(duplicateLength, "auth/status", CancellationToken.None));
        Assert.AreEqual("rpc_header_invalid", headerError.ErrorCode);

        using var unexpectedId = new ReplyStream(CreateWire(body));
        var responseError = await Assert.ThrowsExactlyAsync<QoderQuotaAcquirer.QoderRpcException>(
            () => QoderRpcFrame.SendAsync(unexpectedId, "auth/status", CancellationToken.None));
        Assert.AreEqual("rpc_response_invalid", responseError.ErrorCode);
    }

    private static void AssertRejected(string? target) =>
        Assert.IsFalse(QoderQuotaAcquirer.TryGetLocalPipeName(target, out _), target);

    private static byte[] CreateWire(string body, string extraHeaders = "")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headerBytes = Encoding.ASCII.GetBytes(
            $"Content-Length: {bodyBytes.Length}\r\n{extraHeaders}\r\n");
        var wire = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, wire, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, wire, headerBytes.Length, bodyBytes.Length);
        return wire;
    }

    private sealed class ReplyStream(byte[] response) : Stream
    {
        private readonly MemoryStream _written = new();
        private int _readOffset;

        public ReadOnlyMemory<byte> Written => _written.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = Math.Min(count, response.Length - _readOffset);
            response.AsSpan(_readOffset, copied).CopyTo(buffer.AsSpan(offset, copied));
            _readOffset += copied;
            return copied;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copied = Math.Min(buffer.Length, response.Length - _readOffset);
            response.AsMemory(_readOffset, copied).CopyTo(buffer);
            _readOffset += copied;
            return ValueTask.FromResult(copied);
        }

        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
