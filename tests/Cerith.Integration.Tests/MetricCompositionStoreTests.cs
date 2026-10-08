#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class MetricCompositionStoreTests
{
    private const int SqliteOk = 0;
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr pointer);

    private static IntPtr Open(string path)
    {
        Assert.AreEqual(SqliteOk, sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out var db, 2 | 0x10000, IntPtr.Zero));
        return db;
    }

    private static void Sql(IntPtr db, string command)
    {
        var code = sqlite3_exec(db, Encoding.UTF8.GetBytes(command + "\0"), IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) sqlite3_free(error);
        Assert.AreEqual(SqliteOk, code, command);
    }
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "CerithCompositionTests", Guid.NewGuid().ToString("N"), "layout.db");

    [TestMethod]
    public async Task NativeBindingSurvivesGcAndPreservesUnicodeAndQuotes()
    {
        var path = TempPath();
        try
        {
            var store = new MetricCompositionStore(path);
            var original = new MetricComposition([new(CompositionKind.Text, "Case.'é.😀.e\u0301", "0.000")]);
            await store.SaveAsync(original, await store.LoadAsync(CancellationToken.None), CancellationToken.None);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.AreEqual(CompositionLoadState.Valid, loaded.State);
            Assert.AreEqual(original.Serialize(), loaded.Composition!.Serialize());
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task UnsupportedAndForeignSchemasArePreserved()
    {
        foreach (var alteration in new[] { "PRAGMA user_version=2", "PRAGMA application_id=123", "CREATE TRIGGER surprise AFTER UPDATE ON composition BEGIN SELECT 1; END" })
        {
            var path = TempPath();
            try
            {
                var store = new MetricCompositionStore(path);
                await store.SaveAsync(MetricComposition.Default, await store.LoadAsync(CancellationToken.None), CancellationToken.None);
                var db = Open(path);
                try { Sql(db, alteration); }
                finally { Assert.AreEqual(SqliteOk, sqlite3_close_v2(db)); }
                var before = await File.ReadAllBytesAsync(path);
                var loaded = await store.LoadAsync(CancellationToken.None);
                Assert.AreEqual(alteration.Contains("user_version") ? CompositionLoadState.Unsupported : CompositionLoadState.Unreadable, loaded.State);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(MetricComposition.Default, loaded, CancellationToken.None));
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            }
            finally { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    [TestMethod]
    public async Task CommitBusyRollsBackAndKeepsPreviousComposition()
    {
        var path = TempPath();
        try
        {
            var store = new MetricCompositionStore(path);
            var initial = MetricComposition.Default;
            await store.SaveAsync(initial, await store.LoadAsync(CancellationToken.None), CancellationToken.None);
            var baseline = await store.LoadAsync(CancellationToken.None);
            var reader = Open(path);
            try
            {
                Sql(reader, "BEGIN; SELECT payload FROM composition WHERE id=1;");
                var error = await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(new MetricComposition(initial.Rows.Reverse()), baseline, CancellationToken.None));
                StringAssert.Contains(error.Message, "(5): COMMIT");
                Sql(reader, "COMMIT");
            }
            finally { Assert.AreEqual(SqliteOk, sqlite3_close_v2(reader)); }
            var reloaded = await store.LoadAsync(CancellationToken.None);
            Assert.AreEqual(CompositionLoadState.Valid, reloaded.State);
            Assert.AreEqual(initial.Serialize(), reloaded.Composition!.Serialize());
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task ExclusiveFileLockIsUnreadableRatherThanMissing()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllBytesAsync(path, [9, 8, 7]);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.AreEqual(CompositionLoadState.Unreadable, (await new MetricCompositionStore(path).LoadAsync(CancellationToken.None)).State);
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task MissingSaveReloadAndOptimisticConflict()
    {
        var path = TempPath();
        try
        {
            var store = new MetricCompositionStore(path);
            var missing = await store.LoadAsync(CancellationToken.None);
            Assert.AreEqual(CompositionLoadState.Missing, missing.State);
            Assert.IsFalse(File.Exists(path));
            var first = MetricComposition.Default;
            await store.SaveAsync(first, missing, CancellationToken.None);
            Assert.IsTrue(File.Exists(path));
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.AreEqual(CompositionLoadState.Valid, loaded.State);
            Assert.AreEqual(first.Serialize(), loaded.Composition!.Serialize());
            var second = new MetricComposition(first.Rows.Reverse());
            await store.SaveAsync(second, loaded, CancellationToken.None);
            Assert.AreEqual(second.Serialize(), (await store.LoadAsync(CancellationToken.None)).Composition!.Serialize());
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(first, loaded, CancellationToken.None));
            Assert.AreEqual(second.Serialize(), (await store.LoadAsync(CancellationToken.None)).Composition!.Serialize());
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(first, missing, CancellationToken.None));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task ExistingEmptyAndCorruptBytesArePreserved()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 1, 4, 9, 12, 0, 22 } })
        {
            var path = TempPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await File.WriteAllBytesAsync(path, bytes);
                var store = new MetricCompositionStore(path);
                var load = await store.LoadAsync(CancellationToken.None);
                Assert.AreEqual(CompositionLoadState.Unreadable, load.State);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(MetricComposition.Default, load, CancellationToken.None));
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    [TestMethod]
    public async Task SidecarAndCancellationCannotInitializeStorage()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var store = new MetricCompositionStore(path);
            var missing = await store.LoadAsync(CancellationToken.None);
            await File.WriteAllBytesAsync(path + "-wal", [1, 2, 3]);
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(MetricComposition.Default, missing, CancellationToken.None));
            Assert.IsFalse(File.Exists(path));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path + "-wal"));
            Assert.AreEqual(CompositionLoadState.Unreadable, (await store.LoadAsync(CancellationToken.None)).State);
            File.Delete(path + "-wal");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(MetricComposition.Default, missing, cancelled.Token));
            Assert.IsFalse(File.Exists(path));
            await store.SaveAsync(MetricComposition.Default, missing, CancellationToken.None);
            Assert.AreEqual(CompositionLoadState.Valid, (await store.LoadAsync(CancellationToken.None)).State);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task FailedFirstPublicationPreservesUnknownFilesAndAllowsRetry()
    {
        var path = TempPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MetricCompositionStore(path);
            var missing = await store.LoadAsync(CancellationToken.None);
            var foreign = Path.Combine(directory, ".cerith-layout-foreign.db");
            await File.WriteAllBytesAsync(foreign, [4, 2, 1]);
            Directory.CreateDirectory(path);
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(MetricComposition.Default, missing, CancellationToken.None));
            Assert.IsFalse(File.Exists(path));
            Assert.IsTrue(Directory.Exists(path));
            CollectionAssert.AreEqual(new byte[] { 4, 2, 1 }, await File.ReadAllBytesAsync(foreign));
            CollectionAssert.AreEqual(new[] { foreign }, Directory.GetFiles(directory));
            Directory.Delete(path);
            Assert.AreEqual(CompositionLoadState.Missing, (await store.LoadAsync(CancellationToken.None)).State);
            await store.SaveAsync(MetricComposition.Default, missing, CancellationToken.None);
            Assert.AreEqual(MetricComposition.Default.Serialize(), (await store.LoadAsync(CancellationToken.None)).Composition!.Serialize());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task ConcurrentFirstSavesPublishExactlyOneCompleteDatabase()
    {
        var path = TempPath();
        try
        {
            var store = new MetricCompositionStore(path);
            var missing = await store.LoadAsync(CancellationToken.None);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Attempt(int index)
            {
                await start.Task;
                try
                {
                    await store.SaveAsync(new MetricComposition([new(CompositionKind.Text, $"Case.{index}", "0")]), missing, CancellationToken.None);
                    return true;
                }
                catch (IOException) { return false; }
            }
            var attempts = Enumerable.Range(0, 8).Select(Attempt).ToArray();
            start.SetResult();
            Assert.AreEqual(1, (await Task.WhenAll(attempts)).Count(success => success));
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.AreEqual(CompositionLoadState.Valid, loaded.State);
            Assert.AreEqual(1, loaded.Composition!.Rows.Length);
            CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task ReadOnlySaveFailurePreservesPayloadAndCanRetry()
    {
        var path = TempPath();
        try
        {
            var store = new MetricCompositionStore(path);
            await store.SaveAsync(MetricComposition.Default, await store.LoadAsync(CancellationToken.None), CancellationToken.None);
            var baseline = await store.LoadAsync(CancellationToken.None);
            var before = await File.ReadAllBytesAsync(path);
            var next = new MetricComposition(MetricComposition.Default.Rows.Reverse());
            File.SetAttributes(path, FileAttributes.ReadOnly);
            Assert.AreEqual(CompositionLoadState.Valid, (await store.LoadAsync(CancellationToken.None)).State);
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(next, baseline, CancellationToken.None));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            File.SetAttributes(path, FileAttributes.Normal);
            await store.SaveAsync(next, baseline, CancellationToken.None);
            Assert.AreEqual(next.Serialize(), (await store.LoadAsync(CancellationToken.None)).Composition!.Serialize());
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public async Task InvalidStoredPayloadsAndChangesAfterLoadArePreserved()
    {
        foreach (var change in new[]
        {
            "UPDATE composition SET payload='{}'",
            "UPDATE composition SET payload='{\"version\":2,\"rows\":[]}'",
            "UPDATE composition SET payload=x'7B7D'",
            "CREATE TABLE foreign_data (id INTEGER)",
            "PRAGMA user_version=2"
        })
        {
            var path = TempPath();
            try
            {
                var store = new MetricCompositionStore(path);
                await store.SaveAsync(MetricComposition.Default, await store.LoadAsync(CancellationToken.None), CancellationToken.None);
                var baseline = await store.LoadAsync(CancellationToken.None);
                var db = Open(path);
                try { Sql(db, change); }
                finally { Assert.AreEqual(SqliteOk, sqlite3_close_v2(db)); }
                var before = await File.ReadAllBytesAsync(path);
                var load = await store.LoadAsync(CancellationToken.None);
                Assert.AreEqual(change.Contains("version") ? CompositionLoadState.Unsupported : CompositionLoadState.Unreadable, load.State);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(MetricComposition.Default, load, CancellationToken.None));
                if (change == "PRAGMA user_version=2")
                    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => store.SaveAsync(MetricComposition.Default, baseline, CancellationToken.None));
                else if (change.StartsWith("CREATE", StringComparison.Ordinal) || change.Contains("x'7B7D'", StringComparison.Ordinal))
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(MetricComposition.Default, baseline, CancellationToken.None));
                else
                    await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(MetricComposition.Default, baseline, CancellationToken.None));
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    [TestMethod]
    public async Task AllOrphanSidecarNamesIncludingDirectoriesBlockFirstSave()
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            var path = TempPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                var store = new MetricCompositionStore(path);
                var missing = await store.LoadAsync(CancellationToken.None);
                Directory.CreateDirectory(path + suffix);
                await File.WriteAllBytesAsync(Path.Combine(path + suffix, "foreign"), [9]);
                Assert.AreEqual(CompositionLoadState.Unreadable, (await store.LoadAsync(CancellationToken.None)).State);
                await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(MetricComposition.Default, missing, CancellationToken.None));
                Assert.IsFalse(File.Exists(path));
                CollectionAssert.AreEqual(new byte[] { 9 }, await File.ReadAllBytesAsync(Path.Combine(path + suffix, "foreign")));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    [TestMethod]
    public async Task NativeOperationsKeepCallbackAliveDuringConcurrentCollections()
    {
        var path = TempPath();
        using var stop = new CancellationTokenSource();
        var collections = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                collections.TrySetResult();
                await Task.Yield();
            }
        });
        try
        {
            await collections.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var store = new MetricCompositionStore(path);
            var baseline = await store.LoadAsync(CancellationToken.None);
            for (int iteration = 0; iteration < 5; iteration++)
            {
                var composition = new MetricComposition(Enumerable.Range(0, 16).Select(index =>
                    new CompositionRow(CompositionKind.Text, $"Case.😀.{iteration}.{index}", "0.000")));
                await store.SaveAsync(composition, baseline, CancellationToken.None);
                baseline = await store.LoadAsync(CancellationToken.None);
                Assert.AreEqual(CompositionLoadState.Valid, baseline.State);
                Assert.AreEqual(composition.Serialize(), baseline.Composition!.Serialize());
            }
        }
        finally
        {
            stop.Cancel();
            await collector.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public void PrivateCleanupPreservesReplacementAndLockedFileWithoutMaskingOriginalError()
    {
        var path = TempPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        try
        {
            var identify = typeof(MetricCompositionStore).GetMethod("GetFileInformationByHandleEx", BindingFlags.Static | BindingFlags.NonPublic)!;
            var cleanup = typeof(MetricCompositionStore).GetMethod("DeleteOwnedTemporary", BindingFlags.Static | BindingFlags.NonPublic)!;
            object identity;
            using (var owned = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                object?[] arguments = [owned.SafeFileHandle, 18, null, 24u];
                Assert.AreEqual(true, identify.Invoke(null, arguments));
                identity = arguments[2]!;
            }
            var retained = Path.Combine(directory, "retained.db");
            File.Move(path, retained);
            File.WriteAllBytes(path, [7, 8, 9]);
            cleanup.Invoke(null, [path, identity]);
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(path));
            File.Delete(path);
            File.Move(retained, path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var original = new IOException("Original save failure.");
                var observed = Assert.ThrowsExactly<IOException>(() =>
                {
                    try { throw original; }
                    finally { cleanup.Invoke(null, [path, identity]); }
                });
                Assert.AreSame(original, observed);
                Assert.IsTrue(File.Exists(path));
            }
            cleanup.Invoke(null, [path, identity]);
            Assert.IsFalse(File.Exists(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
