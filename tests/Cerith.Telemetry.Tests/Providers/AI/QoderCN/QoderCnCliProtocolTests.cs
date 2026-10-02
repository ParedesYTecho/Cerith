#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class QoderCnCliProtocolTests
{
    [TestMethod]
    public void CreatesTheSdkControlRequestEnvelopeAndLineTerminator()
    {
        var request = QoderCnCliProtocol.CreateControlRequest("request-1", "initialize",
            new Dictionary<string, object?>
            {
                ["modelPolicyProvider"] = false,
                ["supportsCatalogReadyInitialize"] = true,
                ["initializeTimeoutMs"] = 30_000
            });

        Assert.AreEqual((byte)'\n', request[^1]);
        using var document = JsonDocument.Parse(request.AsMemory(0, request.Length - 1));
        var root = document.RootElement;
        Assert.AreEqual("control_request", root.GetProperty("type").GetString());
        Assert.AreEqual("request-1", root.GetProperty("request_id").GetString());
        var body = root.GetProperty("request");
        Assert.AreEqual("initialize", body.GetProperty("type").GetString());
        Assert.AreEqual("initialize", body.GetProperty("subtype").GetString());
        Assert.IsFalse(body.GetProperty("modelPolicyProvider").GetBoolean());
        Assert.IsTrue(body.GetProperty("supportsCatalogReadyInitialize").GetBoolean());
        Assert.AreEqual(30_000, body.GetProperty("initializeTimeoutMs").GetInt32());
    }

    [TestMethod]
    public async Task ReadsMatchingControlResponseAndSkipsUnmatchedResponse()
    {
        var output = """
            {"type":"control_response","response":{"subtype":"success","request_id":"old","response":{"ignored":true}}}
            {"type":"control_response","response":{"subtype":"success","request_id":"request-2","response":{"account":{"userId":"cn-user-17"}}}}
            """ + "\n";
        var reader = new QoderCnCliOutputReader(new MemoryStream(Encoding.UTF8.GetBytes(output)));

        var response = await reader.ReadResponseAsync("request-2", CancellationToken.None);

        Assert.AreEqual("cn-user-17", response.GetProperty("account").GetProperty("userId").GetString());
    }

    [TestMethod]
    public async Task RejectsMalformedMissingAndOversizedFrames()
    {
        var malformed = new QoderCnCliOutputReader(new MemoryStream(Encoding.UTF8.GetBytes("{\n")));
        var malformedError = await Assert.ThrowsAsync<QoderCnCliProtocolException>(
            () => malformed.ReadResponseAsync("request-3", CancellationToken.None));
        Assert.AreEqual("protocol_invalid", malformedError.Code);

        var missing = new QoderCnCliOutputReader(new MemoryStream());
        var missingError = await Assert.ThrowsAsync<QoderCnCliProtocolException>(
            () => missing.ReadResponseAsync("request-4", CancellationToken.None));
        Assert.AreEqual("response_missing", missingError.Code);

        var oversizedLine = new byte[QoderCnCliOutputReader.MaxFrameBytes + 2];
        Array.Fill(oversizedLine, (byte)'x');
        oversizedLine[^1] = (byte)'\n';
        var oversized = new QoderCnCliOutputReader(new MemoryStream(oversizedLine));
        var oversizedError = await Assert.ThrowsAsync<QoderCnCliProtocolException>(
            () => oversized.ReadResponseAsync("request-5", CancellationToken.None));
        Assert.AreEqual("frame_too_large", oversizedError.Code);
    }

    [TestMethod]
    public async Task HonorsCancellationAndTimeoutWhileWaitingForAResponse()
    {
        var cancelledReader = new QoderCnCliOutputReader(new BlockingStream());
        using var cancelled = new CancellationTokenSource();
        var cancelledRead = cancelledReader.ReadResponseAsync("request-6", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledRead);

        var timedReader = new QoderCnCliOutputReader(new BlockingStream());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => timedReader.ReadResponseAsync("request-7", deadline.Token));
    }

    [TestMethod]
    public async Task BracketsUsageWithTheSameAccountOnOneControlPeer()
    {
        var peer = new ControlExchangeStream(switchAccountAfterUsage: false);

        var reading = await QoderCnCliClient.QueryUsageAsync(peer, peer, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "initialize", "account_info", "get_usage_info", "account_info" },
            peer.RequestTypes.ToArray());
        Assert.AreEqual(Cerith.Core.MetricState.Fresh, reading.Points[QoderCnQuotaParser.IncludedRemaining].State);
        Assert.AreEqual(80d, reading.Points[QoderCnQuotaParser.IncludedRemaining].Value!.Value.AsDouble());
    }

    [TestMethod]
    public async Task RejectsUsageWhenTheAuthenticatedAccountChangesAfterTheRead()
    {
        var peer = new ControlExchangeStream(switchAccountAfterUsage: true);

        var reading = await QoderCnCliClient.QueryUsageAsync(peer, peer, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "initialize", "account_info", "get_usage_info", "account_info" },
            peer.RequestTypes.ToArray());
        Assert.AreEqual(Cerith.Core.MetricState.Error, reading.Points[QoderCnQuotaParser.IncludedRemaining].State);
        Assert.IsNull(reading.Points[QoderCnQuotaParser.IncludedRemaining].Value);
        Assert.AreEqual("identity_changed", reading.Points[QoderCnQuotaParser.IncludedRemaining].ErrorCode);
    }

    [TestMethod]
    public async Task RejectsUnsignedPathExecutableBeforeStartingAProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"qoder-cn-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var executablePath = Path.Combine(directory, QoderCnCliClient.ExecutableFileName);
        try
        {
            await File.WriteAllTextAsync(executablePath, "unsigned candidate");
            var discoveredPath = QoderCnCliClient.FindExecutableFromPath(directory);
            Assert.AreEqual(Path.GetFullPath(executablePath), discoveredPath);
            var client = new QoderCnCliClient(discoveredPath);

            var reading = await client.GetUsageAsync(CancellationToken.None);

            Assert.IsTrue(reading.Points.All(point => point.State == Cerith.Core.MetricState.Error &&
                point.ErrorCode == "cli_untrusted" && point.Value is null));
        }
        finally
        {
            if (File.Exists(executablePath))
                File.Delete(executablePath);
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void PrefersTheInstalledCLINameThenResolvesTheDocumentedAlias()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"qoder-cn-entrypoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var aliasCandidate = Path.Combine(directory, "qodercn.exe");
            File.WriteAllText(aliasCandidate, "alias candidate");
            Assert.AreEqual(Path.GetFullPath(aliasCandidate), QoderCnCliClient.FindExecutableFromPath(directory));

            var officialNameCandidate = Path.Combine(directory, QoderCnCliClient.ExecutableFileName);
            File.WriteAllText(officialNameCandidate, "test candidate");
            Assert.AreEqual(Path.GetFullPath(officialNameCandidate),
                QoderCnCliClient.FindExecutableFromPath(directory));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
        }

        Assert.IsTrue(QoderCnCliTrust.IsExpectedPublisherSubject(QoderCnCliTrust.ExpectedPublisherSubject));
        Assert.IsFalse(QoderCnCliTrust.IsExpectedPublisherSubject("CN=Unknown Publisher, O=Unknown Publisher"));
        Assert.IsFalse(QoderCnCliTrust.IsLocalFixedPath(@"\\server\share\qoderclicn.exe"));
    }

    [TestMethod]
    public void UsesOnlyTheNativeCNCommandAndDisablesPersistenceAndAutomaticSideEffects()
    {
        var startInfo = QoderCnCliClient.CreateStartInfo(Path.Combine(Path.GetTempPath(), QoderCnCliClient.ExecutableFileName));
        var args = startInfo.ArgumentList;

        Assert.AreEqual(13, args.Count);
        Assert.AreEqual("--print", args[0]);
        Assert.AreEqual("--output-format", args[1]);
        Assert.AreEqual("stream-json", args[2]);
        Assert.AreEqual("--input-format", args[3]);
        Assert.AreEqual("stream-json", args[4]);
        Assert.AreEqual("--no-session-persistence", args[5]);
        Assert.AreEqual("--tools", args[6]);
        Assert.AreEqual(string.Empty, args[7]);
        Assert.AreEqual("--strict-mcp-config", args[8]);
        Assert.AreEqual("--settings", args[9]);
        Assert.AreEqual("--config-dir", args[11]);
        Assert.IsTrue(args[12].EndsWith(".qoder-cn", StringComparison.Ordinal));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Path.GetTempPath(), QoderCnCliClient.ExecutableFileName)), startInfo.FileName);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.IsTrue(startInfo.Environment["CI"] == "1");
        Assert.IsTrue(startInfo.Environment["QODERCN_CONFIG_DIR"]!.EndsWith(".qoder-cn", StringComparison.Ordinal));
        foreach (var name in startInfo.Environment.Keys)
        {
            if (name.StartsWith("QODER", StringComparison.OrdinalIgnoreCase))
                Assert.AreEqual("QODERCN_CONFIG_DIR", name);
            Assert.IsFalse(name.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase));
        }
        StringAssert.Contains(args[10], "\"enableAutoUpdate\":false");
        StringAssert.Contains(args[10], "\"usageStatisticsEnabled\":false");
    }

    private sealed class ControlExchangeStream(bool switchAccountAfterUsage) : Stream
    {
        private readonly Queue<byte[]> _responses = new();
        private readonly List<string> _requestTypes = new();
        private byte[]? _currentResponse;
        private int _currentOffset;
        private int _accountInfoCount;

        public IReadOnlyList<string> RequestTypes => _requestTypes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_currentResponse is null)
            {
                if (_responses.Count == 0)
                    return ValueTask.FromResult(0);
                _currentResponse = _responses.Dequeue();
                _currentOffset = 0;
            }

            var count = Math.Min(buffer.Length, _currentResponse.Length - _currentOffset);
            _currentResponse.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            if (_currentOffset == _currentResponse.Length)
                _currentResponse = null;
            return ValueTask.FromResult(count);
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = JsonDocument.Parse(buffer);
            var root = request.RootElement;
            var requestId = root.GetProperty("request_id").GetString()!;
            var type = root.GetProperty("request").GetProperty("type").GetString()!;
            _requestTypes.Add(type);

            object response = type switch
            {
                "initialize" => new { account = new { userId = "cn-user-17" } },
                "account_info" => new
                {
                    account = new
                    {
                        userId = switchAccountAfterUsage && ++_accountInfoCount > 1 ? "cn-user-18" : "cn-user-17"
                    }
                },
                "get_usage_info" => new
                {
                    usage = new
                    {
                        userId = "cn-user-17",
                        userQuota = new { total = 100, used = 20, remaining = 80, unit = "Credits" }
                    },
                    session = new { total_credits = 999 }
                },
                _ => throw new InvalidOperationException("Unexpected control request.")
            };

            var line = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "control_response",
                response = new { subtype = "success", request_id = requestId, response }
            });
            Array.Resize(ref line, line.Length + 1);
            line[^1] = (byte)'\n';
            _responses.Enqueue(line);
            return ValueTask.CompletedTask;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
