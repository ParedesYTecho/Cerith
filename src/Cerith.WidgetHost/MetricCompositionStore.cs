#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Cerith.WidgetHost;

internal enum CompositionLoadState { Missing, Valid, Unsupported, Unreadable }
internal sealed record CompositionLoad(CompositionLoadState State, MetricComposition? Composition, byte[]? Payload);

internal sealed class MetricCompositionStore(string path)
{
    private const int Ok = 0, Row = 100, Done = 101, Text = 3;
    private const int ReadOnly = 1, ReadWrite = 2, FullMutex = 0x10000;
    private const int ApplicationId = 0x43455249;
    private const int LimitLength = 0, LimitSqlLength = 1, LimitColumn = 2, LimitExprDepth = 3;
    private const int FileIdInfo = 18, FileDispositionInfo = 4;
    private const uint DeleteAccess = 0x10000, ReadAttributes = 0x80, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, NormalFile = 0x80;
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cerith", "layout.db");

    internal Task<CompositionLoad> LoadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Load(cancellationToken), cancellationToken);

    internal Task SaveAsync(MetricComposition composition, CompositionLoad baseline, CancellationToken cancellationToken) =>
        Task.Run(() => Save(composition, baseline, cancellationToken), cancellationToken);

    private CompositionLoad Load(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { using var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) when (!HasSidecars(path)) { return new(CompositionLoadState.Missing, null, null); }
        catch (DirectoryNotFoundException) when (!HasSidecars(path)) { return new(CompositionLoadState.Missing, null, null); }
        catch (Exception) { return new(CompositionLoadState.Unreadable, null, null); }
        try
        {
            using var database = new Database(path, readOnly: true, token);
            CheckSchema(database);
            var payload = ReadPayload(database);
            if (payload is null) return new(CompositionLoadState.Unreadable, null, null);
            return new(CompositionLoadState.Valid, MetricComposition.Parse(payload), payload);
        }
        catch (NotSupportedException) { return new(CompositionLoadState.Unsupported, null, null); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(CompositionLoadState.Unreadable, null, null); }
    }

    private void Save(MetricComposition composition, CompositionLoad baseline, CancellationToken token)
    {
        if (baseline.State is not (CompositionLoadState.Missing or CompositionLoadState.Valid))
            throw new InvalidOperationException("Existing layout cannot be replaced.");
        byte[] payload = Encoding.UTF8.GetBytes(composition.Serialize());
        token.ThrowIfCancellationRequested();
        bool fresh = baseline.State == CompositionLoadState.Missing;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!fresh)
        {
            SaveOpened(path, payload, baseline, token, fresh: false);
            return;
        }
        if (HasSidecars(path)) throw new IOException("Orphaned layout journal exists; storage preserved.");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".cerith-layout-" + Guid.NewGuid().ToString("N") + ".db");
        var owned = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        FileIdentity? identity = null;
        try
        {
            if (!GetFileInformationByHandleEx(owned.SafeFileHandle, FileIdInfo, out var original, (uint)Marshal.SizeOf<FileIdentity>()))
                throw new IOException("Could not identify private layout storage.");
            identity = original;
            SaveOpened(temporary, payload, baseline, token, fresh: true);
            token.ThrowIfCancellationRequested();
            if (HasSidecars(path)) throw new IOException("Orphaned layout journal exists; storage preserved.");
            owned.Dispose();
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            owned.Dispose();
            try { if (identity is { } original) DeleteOwnedTemporary(temporary, original); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine($"Private layout cleanup failed: {error.GetType().Name}"); }
        }
    }

    private static void DeleteOwnedTemporary(string file, FileIdentity original)
    {
        // No sharing prevents replacement between checking identity and deleting this handle's file.
        using var handle = CreateFileW(file, DeleteAccess | ReadAttributes, 0, IntPtr.Zero, OpenExisting, OpenReparsePoint | NormalFile, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() != 2) System.Diagnostics.Debug.WriteLine("Private layout cleanup could not open its file.");
            return;
        }
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out var current, (uint)Marshal.SizeOf<FileIdentity>()) ||
            current.Volume != original.Volume || current.Low != original.Low || current.High != original.High)
        {
            System.Diagnostics.Debug.WriteLine("Private layout identity changed; file preserved.");
            return;
        }
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, sizeof(byte)))
            System.Diagnostics.Debug.WriteLine("Private layout cleanup could not delete its file.");
    }

    private static bool HasSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            try { _ = File.GetAttributes(databasePath + suffix); return true; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
        }
        return false;
    }

    private void SaveOpened(string databasePath, byte[] payload, CompositionLoad baseline, CancellationToken token, bool fresh)
    {
        using var database = new Database(databasePath, readOnly: false, token);
        token.ThrowIfCancellationRequested();
        if (fresh)
        {
            if (database.Scalar("PRAGMA journal_mode=DELETE") != "delete") throw new IOException("Unexpected journal mode.");
            database.Execute("PRAGMA synchronous=FULL");
            database.Execute("BEGIN IMMEDIATE");
            try
            {
                database.Execute("CREATE TABLE composition (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL)");
                database.Execute("PRAGMA user_version=1");
                database.Execute($"PRAGMA application_id={ApplicationId}");
                database.BindAndExecute("INSERT INTO composition (id,payload) VALUES (1,?1)", payload);
                token.ThrowIfCancellationRequested();
                database.Execute("COMMIT");
            }
            catch { database.Rollback(); throw; }
            return;
        }
        database.Execute("PRAGMA synchronous=FULL");
        database.Execute("BEGIN IMMEDIATE");
        try
        {
            CheckSchema(database);
            var actual = ReadPayload(database);
            if (actual is null || baseline.Payload is null || !actual.AsSpan().SequenceEqual(baseline.Payload))
                throw new IOException("Layout changed in another instance.");
            token.ThrowIfCancellationRequested();
            database.BindAndExecute("UPDATE composition SET payload=?1 WHERE id=1", payload);
            token.ThrowIfCancellationRequested();
            database.Execute("COMMIT");
        }
        catch { database.Rollback(); throw; }
    }

    private static void CheckSchema(Database db)
    {
        if (db.Scalar("PRAGMA application_id") != ApplicationId.ToString()) throw new InvalidDataException("Layout is not owned by Cerith.");
        if (db.Scalar("PRAGMA user_version") != "1") throw new NotSupportedException("Unsupported layout schema version.");
        if (db.Scalar("SELECT count(*) FROM sqlite_master WHERE type!='table' OR name NOT IN ('composition','sqlite_sequence')") != "0" ||
            db.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='composition' AND sql='CREATE TABLE composition (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL)'") != "1" ||
            db.Scalar("SELECT count(*) FROM sqlite_master WHERE name='sqlite_sequence'") != "0" ||
            db.Scalar("SELECT count(*) FROM pragma_table_info('composition')") != "2" ||
            db.Scalar("SELECT count(*) FROM composition") != "1")
            throw new InvalidDataException("Unexpected layout database schema or rows.");
    }

    private static byte[]? ReadPayload(Database db) => db.Bytes("SELECT payload FROM composition WHERE id=1");

    private sealed class Database : IDisposable
    {
        private IntPtr _handle;
        private readonly ProgressCallback _progress;
        internal Database(string file, bool readOnly, CancellationToken token)
        {
            _progress = _ => token.IsCancellationRequested ? 1 : 0;
            byte[] name = Encoding.UTF8.GetBytes(file + "\0");
            int rc = sqlite3_open_v2(name, out _handle, (readOnly ? ReadOnly : ReadWrite) | FullMutex, IntPtr.Zero);
            if (rc != Ok) { Dispose(); throw new IOException("Could not open layout storage."); }
            if (sqlite3_busy_timeout(_handle, 100) != Ok) { Dispose(); throw new IOException("Could not configure layout timeout."); }
            sqlite3_limit(_handle, LimitLength, 65536);
            sqlite3_limit(_handle, LimitSqlLength, 4096);
            sqlite3_limit(_handle, LimitColumn, 16);
            sqlite3_limit(_handle, LimitExprDepth, 32);
            sqlite3_progress_handler(_handle, 100, _progress, IntPtr.Zero);
        }

        internal void Execute(string sql)
        {
            WithStatement(sql, statement =>
            {
                int code = sqlite3_step(statement);
                if (code != Done) throw new IOException($"Layout SQL operation failed ({code}): {sql}.");
                return 0;
            });
        }
        internal void Rollback()
        {
            sqlite3_progress_handler(_handle, 0, null, IntPtr.Zero);
            try { Execute("ROLLBACK"); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Layout rollback failed: {error.GetType().Name}"); }
        }
        internal string? Scalar(string sql) => WithStatement(sql, statement =>
        {
            if (sqlite3_step(statement) != Row) throw new IOException("Layout SQL read failed.");
            int bytes = sqlite3_column_bytes(statement, 0);
            if (bytes < 0 || bytes > 65536) throw new InvalidDataException("Oversized layout SQL result.");
            var pointer = sqlite3_column_text(statement, 0);
            var result = pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer, bytes);
            if (sqlite3_step(statement) != Done) throw new InvalidDataException("Unexpected additional SQL rows.");
            return result;
        });
        internal byte[]? Bytes(string sql) => WithStatement(sql, statement =>
        {
            int rc = sqlite3_step(statement);
            if (rc == Done) return null;
            if (rc != Row || sqlite3_column_type(statement, 0) != Text) throw new InvalidDataException("Invalid layout payload type.");
            int count = sqlite3_column_bytes(statement, 0);
            if (count is < 1 or > 65536) throw new InvalidDataException("Invalid layout payload size.");
            var data = new byte[count];
            Marshal.Copy(sqlite3_column_text(statement, 0), data, 0, count);
            if (sqlite3_step(statement) != Done) throw new InvalidDataException("Duplicate layout rows.");
            return data;
        });
        internal void BindAndExecute(string sql, byte[] payload) => WithStatement(sql, statement =>
        {
            if (sqlite3_bind_text(statement, 1, payload, payload.Length, new IntPtr(-1)) != Ok || sqlite3_step(statement) != Done)
                throw new IOException("Layout write failed.");
            return 0;
        });
        private T WithStatement<T>(string sql, Func<IntPtr, T> action)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(sql + "\0");
            if (sqlite3_prepare_v2(_handle, bytes, -1, out var statement, IntPtr.Zero) != Ok)
                throw new IOException("Layout SQL preparation failed.");
            T result;
            try { result = action(statement); }
            catch { sqlite3_finalize(statement); throw; }
            if (sqlite3_finalize(statement) != Ok) throw new IOException("Layout SQL finalization failed.");
            return result;
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                sqlite3_progress_handler(_handle, 0, null, IntPtr.Zero);
                sqlite3_close_v2(_handle);
                _handle = IntPtr.Zero;
            }
            GC.KeepAlive(_progress);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong Volume, Low, High; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdentity identity, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte delete, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgressCallback(IntPtr context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] name, out IntPtr db, int flags, IntPtr vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int size, out IntPtr statement, IntPtr tail);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_progress_handler(IntPtr db, int instructions, ProgressCallback? callback, IntPtr context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_limit(IntPtr db, int id, int newLimit);
}
