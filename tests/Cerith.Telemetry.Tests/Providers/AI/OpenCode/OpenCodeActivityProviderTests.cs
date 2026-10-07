#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Cerith.TelemetryService.Providers.AI.OpenCode;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.OpenCode;

[TestClass]
public sealed class OpenCodeActivityProviderTests
{
    private static readonly string[] MetricIds =
    [
        "ai.opencode.activity.latest.tokens.input",
        "ai.opencode.activity.latest.tokens.output",
        "ai.opencode.activity.latest.tokens.reasoning",
        "ai.opencode.activity.latest.tokens.cache.read",
        "ai.opencode.activity.latest.tokens.cache.write",
        "ai.opencode.activity.latest.model.id",
        "ai.opencode.activity.latest.model.provider",
        "ai.opencode.activity.latest.occurred_at"
    ];

    private const string PrivateCanary = "PRIVATE_TRANSCRIPT_CANARY_61B2";

    [TestMethod]
    public async Task ActivityTupleUsesMetricIdsAcrossNamedPipeWithoutTranscriptContent()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.Insert("assistant", 1, CreateMessage(1_790_000_000_000, PrivateCanary), "1.17.9");

        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var provider = new OpenCodeActivityProvider(registry, bus, new OpenCodeActivityReader(database.Path), TimeProvider.System);
        var pipeName = $"Cerith.OpenCodeActivity.{Guid.NewGuid():N}";
        var serverTask = new NamedPipeMetricServer(pipeName, bus, registry).RunAsync(stop.Token);
        var clients = new NamedPipeMetricClient[MetricIds.Length];
        var readers = new IAsyncEnumerator<MetricSnapshot>[MetricIds.Length];
        var pending = new Task<bool>[MetricIds.Length];

        try
        {
            for (var i = 0; i < MetricIds.Length; i++)
            {
                var id = new MetricId(MetricIds[i]);
                clients[i] = new NamedPipeMetricClient(pipeName);
                await clients[i].ConnectAsync(id, stop.Token);
                Assert.AreEqual(id, clients[i].Descriptor?.Id);
                Assert.AreEqual(KindFor(i), clients[i].Descriptor?.ValueKind);
                readers[i] = clients[i].ReadSnapshotsAsync(stop.Token).GetAsyncEnumerator();
                pending[i] = readers[i].MoveNextAsync().AsTask();
            }

            await provider.PollOnceAsync(stop.Token);
            Assert.IsTrue((await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5))).All(value => value));

            var snapshots = readers.Select(reader => reader.Current).ToArray();
            var observedAt = snapshots[0].ObservedAt;
            for (var i = 0; i < snapshots.Length; i++)
            {
                Assert.AreEqual(MetricIds[i], snapshots[i].MetricId.Value);
                Assert.AreEqual(observedAt, snapshots[i].ObservedAt);
                Assert.AreEqual(MetricState.Fresh, snapshots[i].State);
                Assert.AreEqual(MetricConfidence.Measured, snapshots[i].Confidence);
                Assert.AreEqual("OpenCode.Activity.Local.v1", snapshots[i].Source);
                Assert.AreEqual(KindFor(i), snapshots[i].Value!.Value.Kind);
            }

            Assert.AreEqual(123L, snapshots[0].Value!.Value.AsInt64());
            Assert.AreEqual(45L, snapshots[1].Value!.Value.AsInt64());
            Assert.AreEqual(6L, snapshots[2].Value!.Value.AsInt64());
            Assert.AreEqual(7L, snapshots[3].Value!.Value.AsInt64());
            Assert.AreEqual(8L, snapshots[4].Value!.Value.AsInt64());
            Assert.AreEqual("test-model", snapshots[5].Value!.Value.AsString());
            Assert.AreEqual("test-provider", snapshots[6].Value!.Value.AsString());
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000),
                snapshots[7].Value!.Value.AsDateTimeOffset());

            var exported = string.Join("|", snapshots.Select(snapshot => snapshot.Value!.Value.Kind switch
            {
                MetricValueKind.String => snapshot.Value.Value.AsString(),
                MetricValueKind.Int64 => snapshot.Value.Value.AsInt64().ToString(),
                MetricValueKind.DateTimeOffset => snapshot.Value.Value.AsDateTimeOffset().ToString("O"),
                _ => string.Empty
            }));
            Assert.IsFalse(exported.Contains(PrivateCanary, StringComparison.Ordinal));

            var repeatedPending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            await provider.PollOnceAsync(stop.Token);
            Assert.IsTrue((await Task.WhenAll(repeatedPending).WaitAsync(TimeSpan.FromSeconds(5))).All(value => value));
            var repeated = readers.Select(reader => reader.Current).ToArray();
            Assert.AreEqual(snapshots[7].Value!.Value.AsDateTimeOffset(),
                repeated[7].Value!.Value.AsDateTimeOffset());
            Assert.AreEqual(snapshots[0].Value!.Value.AsInt64(), repeated[0].Value!.Value.AsInt64());

            var nextPending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
            database.ClearMessages();
            await provider.PollOnceAsync(stop.Token);
            Assert.IsTrue((await Task.WhenAll(nextPending).WaitAsync(TimeSpan.FromSeconds(5))).All(value => value));
            var unavailable = readers.Select(reader => reader.Current).ToArray();
            Assert.IsTrue(unavailable.All(snapshot => snapshot.State == MetricState.Unavailable));
            Assert.IsTrue(unavailable.All(snapshot => snapshot.Value is null));
            Assert.IsTrue(unavailable.All(snapshot => snapshot.ObservedAt == unavailable[0].ObservedAt));
            Assert.IsTrue(unavailable.All(snapshot => snapshot.ErrorCode == "activity_absent"));
        }
        finally
        {
            stop.Cancel();
            var serverStopped = false;
            try
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
                serverStopped = true;
            }
            catch (OperationCanceledException) { serverStopped = true; }
            catch (IOException) { serverStopped = true; }
            Assert.IsTrue(serverStopped);
            foreach (var reader in readers)
            {
                if (reader is not null) await reader.DisposeAsync();
            }
            foreach (var client in clients)
            {
                if (client is not null) await client.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public void ReaderUsesNewestCreatedAssistantAndDoesNotFallbackFromIncompleteRecord()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.Insert("assistant", 10, CreateMessage(1_790_000_000_000, "older"));
        database.Insert("user", 20, CreateMessage(1_790_000_000_000, "user text"));
        database.Insert("assistant", 30, CreateMessage(1_790_000_000_100, "incomplete", includeCompleted: false));

        var result = new OpenCodeActivityReader(database.Path).Read();

        Assert.AreEqual(OpenCodeActivityReadStatus.Unavailable, result.Status);
        Assert.AreEqual("activity_incomplete", result.ErrorCode);
        Assert.IsNull(result.Sample);
    }

    [TestMethod]
    public void ReaderRequiresTheSelectedSessionsSupportedVersionAndNeverFallsBack()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.Insert("assistant", 10, CreateMessage(1_790_000_000_000, "older supported"), "1.17.9");
        database.Insert("assistant", 20, "not-json", "1.18.0");

        var unsupported = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Unavailable, unsupported.Status);
        Assert.AreEqual("source_version_unsupported", unsupported.ErrorCode);
        Assert.IsNull(unsupported.Sample);

        database.ClearMessages();
        database.InsertWithoutSession("assistant", 30, CreateMessage(1_790_000_000_002, "missing parent"));
        var missingVersion = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Error, missingVersion.Status);
        Assert.AreEqual("source_version_missing", missingVersion.ErrorCode);
        Assert.IsNull(missingVersion.Sample);

        database.ClearMessages();
        database.Insert("assistant", 40, CreateMessage(1_790_000_000_003, "empty version"), string.Empty);
        var unusableVersion = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Error, unusableVersion.Status);
        Assert.AreEqual("source_version_invalid", unusableVersion.ErrorCode);
        Assert.IsNull(unusableVersion.Sample);
    }

    [TestMethod]
    public void ReaderRequiresEveryUsageCategoryAndExplicitlyAcceptsZero()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.Insert("assistant", 10, CreateMessage(1_790_000_000_000, "missing", tokensJson: "{\"input\":1}"));

        var missing = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Unavailable, missing.Status);
        Assert.AreEqual("activity_usage_incomplete", missing.ErrorCode);

        database.ClearMessages();
        database.Insert("assistant", 20, CreateMessage(1_790_000_000_001, "zero",
            tokensJson: "{\"input\":0,\"output\":0,\"reasoning\":0,\"cache\":{\"read\":0,\"write\":0}}"));

        var zero = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Ready, zero.Status);
        Assert.AreEqual(0L, zero.Sample!.InputTokens);
        Assert.AreEqual(0L, zero.Sample.OutputTokens);
    }

    [TestMethod]
    public void FailedNewestAssistantIsUnavailableAndMalformedCountsAreErrors()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.Insert("assistant", 10, CreateMessage(1_790_000_000_000, "older"));
        database.Insert("assistant", 20, CreateMessage(1_790_000_000_001, "failed", failed: true));

        var failed = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Unavailable, failed.Status);
        Assert.AreEqual("activity_failed", failed.ErrorCode);
        Assert.IsNull(failed.Sample);

        database.ClearMessages();
        database.Insert("assistant", 30, CreateMessage(1_790_000_000_002, "invalid",
            tokensJson: "{\"input\":-1,\"output\":2,\"reasoning\":3,\"cache\":{\"read\":4,\"write\":5}}"));
        var invalid = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Error, invalid.Status);
        Assert.AreEqual("activity_usage_invalid", invalid.ErrorCode);
        Assert.IsNull(invalid.Sample);
    }

    [TestMethod]
    public void ReaderRejectsUnsafeIntegersStringsAndOversizedRecordsWithoutReturningValues()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.Insert("assistant", 10, CreateMessage(1_790_000_000_000, "large count",
            tokensJson: "{\"input\":9007199254740992,\"output\":2,\"reasoning\":3,\"cache\":{\"read\":4,\"write\":5}}"));
        var largeCount = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Error, largeCount.Status);
        Assert.IsNull(largeCount.Sample);

        database.ClearMessages();
        database.Insert("assistant", 20, CreateMessage(1_790_000_000_000, "long model", modelId: new string('m', 129)));
        var longModel = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Error, longModel.Status);
        Assert.AreEqual("activity_model_invalid", longModel.ErrorCode);
        Assert.IsNull(longModel.Sample);

        database.ClearMessages();
        database.Insert("assistant", 30, CreateMessage(1_790_000_000_000, new string('x', 80_000)));
        var oversized = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Error, oversized.Status);
        Assert.IsNull(oversized.Sample);
    }

    [TestMethod]
    public async Task BusyReadCanBeCancelledWithinTheBoundedReaderWindow()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.BeginExclusiveLock();
        using var cancellation = new CancellationTokenSource();
        var reader = new OpenCodeActivityReader(database.Path);
        var stopwatch = Stopwatch.StartNew();
        var readTask = Task.Run(() => reader.Read(cancellation.Token));
        await Task.Delay(20);
        cancellation.Cancel();

        OpenCodeActivityReadResult? result = null;
        var canceled = false;
        try { result = await readTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { canceled = true; }

        Assert.IsTrue(canceled || result?.Status == OpenCodeActivityReadStatus.Error);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task UnavailableActivityDoesNotBlockOtherMetricPublication()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        var provider = new OpenCodeActivityProvider(registry, bus,
            new OpenCodeActivityReader(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.db")), TimeProvider.System);
        var hardwareId = new MetricId("gpu.test.activity.coexistence");
        Assert.IsTrue(registry.TryRegister(new MetricDescriptor(hardwareId, "Test GPU", "test", MetricValueKind.Int64)));
        await using var activityReader = bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();
        await using var hardwareReader = bus.Subscribe(hardwareId).GetAsyncEnumerator();
        var activityPending = activityReader.MoveNextAsync().AsTask();
        var hardwarePending = hardwareReader.MoveNextAsync().AsTask();

        await provider.PollOnceAsync();
        await bus.PublishAsync(new MetricSnapshot(hardwareId, MetricValue.From(42L), DateTimeOffset.UtcNow,
            MetricState.Fresh, MetricConfidence.Measured, source: "Synthetic.Coexistence.Test"));

        Assert.IsTrue(await activityPending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, activityReader.Current.State);
        Assert.IsTrue(await hardwarePending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(42L, hardwareReader.Current.Value!.Value.AsInt64());
    }

    [TestMethod]
    public async Task MissingDatabaseCanAppearLaterAndAReplacementIsOpenedOnTheNextPoll()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cerith-opencode-appearance-{Guid.NewGuid():N}.db");
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        var provider = new OpenCodeActivityProvider(registry, bus, new OpenCodeActivityReader(path), TimeProvider.System);
        await using var reader = bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();

        var firstPending = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await firstPending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.IsNull(reader.Current.Value);

        using (var database = new SyntheticOpenCodeDatabase(path))
        {
            database.Insert("assistant", 10, CreateMessage(1_790_000_000_000, "appeared"));
            var appearedPending = reader.MoveNextAsync().AsTask();
            await provider.PollOnceAsync();
            Assert.IsTrue(await appearedPending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(MetricState.Fresh, reader.Current.State);
            Assert.AreEqual(123L, reader.Current.Value!.Value.AsInt64());
        }

        using var replacement = new SyntheticOpenCodeDatabase(path);
        replacement.Insert("assistant", 20, CreateMessage(1_790_000_000_001, "replacement",
            tokensJson: "{\"input\":9,\"output\":8,\"reasoning\":7,\"cache\":{\"read\":6,\"write\":5}}"));
        var replacementPending = reader.MoveNextAsync().AsTask();
        await provider.PollOnceAsync();
        Assert.IsTrue(await replacementPending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Fresh, reader.Current.State);
        Assert.AreEqual(9L, reader.Current.Value!.Value.AsInt64());
    }

    [TestMethod]
    public void ReaderBoundsTheCreatedRowWindowAndRequiresThePinnedTableIndex()
    {
        using var database = new SyntheticOpenCodeDatabase();
        database.Insert("assistant", 1, CreateMessage(1_790_000_000_000, "outside window"));
        for (var i = 0; i < OpenCodeActivityReader.WindowSize; i++)
            database.Insert("user", 2 + i, CreateMessage(1_790_000_000_000, "user"));

        var exhausted = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Unavailable, exhausted.Status);
        Assert.AreEqual("activity_window_exhausted", exhausted.ErrorCode);

        database.DropActivityIndex();
        var changedSchema = new OpenCodeActivityReader(database.Path).Read();
        Assert.AreEqual(OpenCodeActivityReadStatus.Error, changedSchema.Status);
        Assert.IsNull(changedSchema.Sample);
    }

    [TestMethod]
    public void PathResolverHonorsOnlyTheExplicitOverrideOrStandardDatabaseName()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencode-xdg"));
        Assert.AreEqual(Path.Combine(root, "opencode", "opencode.db"),
            OpenCodeActivityReader.ResolveDatabasePath(null, root));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "opencode", "custom.db")),
            OpenCodeActivityReader.ResolveDatabasePath("custom.db", root));
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "specific.db"));
        Assert.AreEqual(absolute, OpenCodeActivityReader.ResolveDatabasePath(absolute, null));
        Assert.IsNull(OpenCodeActivityReader.ResolveDatabasePath(":memory:", root));
        Assert.IsNull(OpenCodeActivityReader.ResolveDatabasePath("relative.db", null));
    }

    [TestMethod]
    public async Task CancellationStopsAllMetricIdsWithOneSharedTimestamp()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(1);
        var provider = new OpenCodeActivityProvider(registry, bus, new OpenCodeActivityReader("missing.db"), TimeProvider.System);
        var readers = MetricIds.Select(id => bus.Subscribe(new MetricId(id)).GetAsyncEnumerator()).ToArray();
        var pending = readers.Select(reader => reader.MoveNextAsync().AsTask()).ToArray();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        try
        {
            await provider.RunAsync(stop.Token);
            Assert.IsTrue((await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5))).All(value => value));
            var snapshots = readers.Select(reader => reader.Current).ToArray();
            Assert.IsTrue(snapshots.All(snapshot => snapshot.State == MetricState.Stopped));
            Assert.IsTrue(snapshots.All(snapshot => snapshot.Value is null));
            Assert.IsTrue(snapshots.All(snapshot => snapshot.ObservedAt == snapshots[0].ObservedAt));
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
    }

    private static MetricValueKind KindFor(int index) => index switch
    {
        <= 4 => MetricValueKind.Int64,
        <= 6 => MetricValueKind.String,
        _ => MetricValueKind.DateTimeOffset
    };

    private static string CreateMessage(long completed, string privateText, string? tokensJson = null,
        bool includeCompleted = true, bool failed = false, string modelId = "test-model")
    {
        var time = new Dictionary<string, object?> { ["created"] = completed - 10 };
        if (includeCompleted) time["completed"] = completed;
        var data = new Dictionary<string, object?>
        {
            ["id"] = "synthetic-message",
            ["sessionID"] = "synthetic-session",
            ["role"] = "assistant",
            ["time"] = time,
            ["model"] = new { providerID = "test-provider", id = modelId },
            ["finish"] = failed ? "error" : "stop",
            ["content"] = new[] { new { type = "text", text = privateText } }
        };
        if (tokensJson is not null)
        {
            using var tokens = JsonDocument.Parse(tokensJson);
            data["tokens"] = tokens.RootElement.Clone();
        }
        else if (includeCompleted)
        {
            data["tokens"] = new { input = 123, output = 45, reasoning = 6,
                cache = new { read = 7, write = 8 } };
        }
        if (failed) data["error"] = new { name = "SyntheticFailure", message = PrivateCanary };
        return JsonSerializer.Serialize(data);
    }

    private sealed class SyntheticOpenCodeDatabase : IDisposable
    {
        private const int SqliteOpenReadWrite = 0x00000002;
        private const int SqliteOpenCreate = 0x00000004;
        private IntPtr _database;

        public string Path { get; }

        public SyntheticOpenCodeDatabase(string? path = null)
        {
            Path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cerith-opencode-{Guid.NewGuid():N}.db");
            var result = SqliteOpen(Path, out _database, SqliteOpenReadWrite | SqliteOpenCreate, IntPtr.Zero);
            if (result != 0 || _database == IntPtr.Zero) throw new InvalidOperationException("Could not create synthetic database.");
            Execute("CREATE TABLE session (id TEXT PRIMARY KEY, version TEXT NOT NULL);");
            Execute("CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE, type TEXT NOT NULL, seq INTEGER NOT NULL, time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);");
            Execute("CREATE INDEX session_message_time_created_idx ON session_message(time_created DESC);");
            Execute("CREATE INDEX session_message_session_seq_idx ON session_message(session_id, seq);");
        }

        public void Insert(string type, long created, string data, string sessionVersion = "1.17.9")
        {
            var sessionId = $"synthetic-session-{Guid.NewGuid():N}";
            var escapedVersion = sessionVersion.Replace("'", "''", StringComparison.Ordinal);
            Execute($"INSERT INTO session(id, version) VALUES('{sessionId}', '{escapedVersion}');");
            InsertMessage(type, created, data, sessionId);
        }

        public void InsertWithoutSession(string type, long created, string data) =>
            InsertMessage(type, created, data, $"missing-parent-{Guid.NewGuid():N}");

        private void InsertMessage(string type, long created, string data, string sessionId)
        {
            var escaped = data.Replace("'", "''", StringComparison.Ordinal);
            Execute($"INSERT INTO session_message(id, session_id, type, seq, time_created, time_updated, data) " +
                $"VALUES('synthetic-{Guid.NewGuid():N}', '{sessionId}', '{type}', {created.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{created.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {created.ToString(System.Globalization.CultureInfo.InvariantCulture)}, '{escaped}');");
        }

        public void ClearMessages() => Execute("DELETE FROM session_message;");

        public void DropActivityIndex() => Execute("DROP INDEX session_message_time_created_idx;");

        public void BeginExclusiveLock() => Execute("BEGIN EXCLUSIVE;");

        public void Dispose()
        {
            if (_database != IntPtr.Zero)
            {
                _ = SqliteClose(_database);
                _database = IntPtr.Zero;
            }
            File.Delete(Path);
        }

        private void Execute(string sql)
        {
            var result = SqliteExec(_database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) SqliteFree(error);
            if (result != 0) throw new InvalidOperationException("Synthetic database setup failed.");
        }

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, out IntPtr database,
            int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SqliteExec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            IntPtr callback, IntPtr context, out IntPtr errorMessage);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SqliteFree(IntPtr memory);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SqliteClose(IntPtr database);
    }
}
