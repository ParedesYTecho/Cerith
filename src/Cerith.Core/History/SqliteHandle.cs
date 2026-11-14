#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Cerith.Core.History;

internal sealed class SqliteHandle : IDisposable
{
    internal const int Ok = 0, Row = 100, Done = 101;
    internal const int TypeNull = 5;
    private const int OpenReadOnly = 1, OpenReadWrite = 2, OpenCreate = 4, OpenFullMutex = 0x10000;
    private static readonly IntPtr Transient = new(-1);
    private IntPtr _db;

    internal SqliteHandle(string file, bool readOnly, int busyMilliseconds)
    {
        var flags = (readOnly ? OpenReadOnly : OpenReadWrite | OpenCreate) | OpenFullMutex;
        if (sqlite3_open_v2(Encoding.UTF8.GetBytes(file + "\0"), out _db, flags, IntPtr.Zero) != Ok)
        {
            Dispose();
            throw new IOException("Could not open history storage.");
        }

        if (sqlite3_busy_timeout(_db, busyMilliseconds) != Ok)
        {
            Dispose();
            throw new IOException("Could not configure history storage.");
        }
    }

    internal void Execute(string sql)
    {
        using var statement = Prepare(sql);
        statement.StepDone();
    }

    internal long? ScalarInt64(string sql)
    {
        using var statement = Prepare(sql);
        if (!statement.Step()) return null;
        return statement.IsNull(0) ? null : statement.Int64(0);
    }

    internal Statement Prepare(string sql)
    {
        if (sqlite3_prepare_v2(_db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var statement, IntPtr.Zero) != Ok)
            throw new IOException("History SQL preparation failed.");
        return new Statement(statement);
    }

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    internal sealed class Statement(IntPtr handle) : IDisposable
    {
        private IntPtr _handle = handle;

        internal bool Step()
        {
            var code = sqlite3_step(_handle);
            if (code == Row) return true;
            if (code == Done) return false;
            throw new IOException($"History SQL step failed ({code}).");
        }

        internal void StepDone()
        {
            if (Step()) throw new IOException("History SQL returned unexpected rows.");
        }

        internal void Reset()
        {
            sqlite3_reset(_handle);
            sqlite3_clear_bindings(_handle);
        }

        internal void Bind(int index, long value) => Check(sqlite3_bind_int64(_handle, index, value));
        internal void Bind(int index, double value) => Check(sqlite3_bind_double(_handle, index, value));
        internal void BindNull(int index) => Check(sqlite3_bind_null(_handle, index));

        internal void Bind(int index, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Check(sqlite3_bind_text(_handle, index, bytes, bytes.Length, Transient));
        }

        internal bool IsNull(int column) => sqlite3_column_type(_handle, column) == TypeNull;
        internal long Int64(int column) => sqlite3_column_int64(_handle, column);
        internal double Double(int column) => sqlite3_column_double(_handle, column);

        internal string? Text(int column)
        {
            var pointer = sqlite3_column_text(_handle, column);
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer, sqlite3_column_bytes(_handle, column));
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            sqlite3_finalize(_handle);
            _handle = IntPtr.Zero;
        }

        private static void Check(int code)
        {
            if (code != Ok) throw new IOException($"History SQL bind failed ({code}).");
        }
    }

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
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_reset(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_clear_bindings(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr statement, int index);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern double sqlite3_column_double(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
}
