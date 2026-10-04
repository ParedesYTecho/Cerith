#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.OpenCode;

internal enum OpenCodeActivityReadStatus
{
    Ready,
    Unavailable,
    Error
}

internal sealed record OpenCodeActivitySample(
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    string ModelId,
    string ProviderId,
    DateTimeOffset OccurredAt);

internal readonly record struct OpenCodeActivityReadResult(
    OpenCodeActivityReadStatus Status,
    OpenCodeActivitySample? Sample,
    string? ErrorCode)
{
    public static OpenCodeActivityReadResult Ready(OpenCodeActivitySample sample) =>
        new(OpenCodeActivityReadStatus.Ready, sample, null);

    public static OpenCodeActivityReadResult Unavailable(string code) =>
        new(OpenCodeActivityReadStatus.Unavailable, null, code);

    public static OpenCodeActivityReadResult Error(string code) =>
        new(OpenCodeActivityReadStatus.Error, null, code);
}

internal sealed class OpenCodeActivityReader
{
    internal const int WindowSize = 64;
    internal const int MaxJsonValueBytes = 64 * 1024;
    internal const int BusyTimeoutMilliseconds = 100;
    internal const int SqlProgressInterval = 1000;
    internal static readonly TimeSpan SqlExecutionLimit = TimeSpan.FromMilliseconds(250);
    internal const long MaxExactJsonInteger = 9_007_199_254_740_991;
    private const string SupportedOpenCodeVersion = "1.17.9";

    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteInterruptCode = 9;
    private const int SqliteTooBig = 18;
    private const int SqliteLimitLength = 0;
    private const int SqliteInteger = 1;
    private const int SqliteFloat = 2;
    private const int SqliteText = 3;

    private const int RowCountColumn = 0;
    private const int AssistantCountColumn = 1;
    private const int CompletedTypeColumn = 2;
    private const int CompletedValueColumn = 3;
    private const int HasErrorColumn = 4;
    private const int FinishTypeColumn = 5;
    private const int FinishIsErrorColumn = 6;
    private const int InputTypeColumn = 7;
    private const int InputValueColumn = 8;
    private const int OutputTypeColumn = 9;
    private const int OutputValueColumn = 10;
    private const int ReasoningTypeColumn = 11;
    private const int ReasoningValueColumn = 12;
    private const int CacheReadTypeColumn = 13;
    private const int CacheReadValueColumn = 14;
    private const int CacheWriteTypeColumn = 15;
    private const int CacheWriteValueColumn = 16;
    private const int ModelTypeColumn = 17;
    private const int ModelValueColumn = 18;
    private const int ProviderTypeColumn = 19;
    private const int ProviderValueColumn = 20;
    private const int SessionVersionTypeColumn = 21;
    private const int SessionVersionValueColumn = 22;

    private const string ProjectionSql = """
        WITH recent AS MATERIALIZED (
            SELECT rowid, session_id, type, time_created
            FROM session_message INDEXED BY session_message_time_created_idx
            ORDER BY time_created DESC, rowid DESC
            LIMIT 64
        ),
        stats AS (
            SELECT count(*) AS row_count,
                   coalesce(sum(CASE WHEN type = 'assistant' THEN 1 ELSE 0 END), 0) AS assistant_count
            FROM recent
        ),
        latest AS (
            SELECT rowid, session_id
            FROM recent
            WHERE type = 'assistant'
            ORDER BY time_created DESC, rowid DESC
            LIMIT 1
        ),
        selected AS MATERIALIZED (
            SELECT CASE WHEN source_session.id IS NULL OR source_session.version IS NULL THEN 0
                        WHEN typeof(source_session.version) = 'text' THEN 1 ELSE 2 END AS session_version_type,
                   source_session.version AS session_version,
                   CASE WHEN source_session.version = '1.17.9' THEN message.data END AS data
            FROM latest
            LEFT JOIN session_message AS message ON message.rowid = latest.rowid
            LEFT JOIN session AS source_session ON source_session.id = latest.session_id
        )
        SELECT stats.row_count,
               stats.assistant_count,
               CASE coalesce(json_type(selected.data, '$.time.completed'), 'missing')
                   WHEN 'missing' THEN 0
                   WHEN 'integer' THEN 1 WHEN 'real' THEN 2 WHEN 'null' THEN 3
                   WHEN 'text' THEN 4 WHEN 'true' THEN 5 WHEN 'false' THEN 6
                   WHEN 'object' THEN 7 WHEN 'array' THEN 8 ELSE 9 END,
               CASE WHEN json_type(selected.data, '$.time.completed') IN ('integer', 'real')
                    THEN json_extract(selected.data, '$.time.completed') END,
               CASE WHEN json_type(selected.data, '$.error') IS NOT NULL
                          AND json_type(selected.data, '$.error') <> 'null' THEN 1 ELSE 0 END,
               CASE WHEN json_type(selected.data, '$.finish') IS NULL THEN 0
                    WHEN json_type(selected.data, '$.finish') = 'null' THEN 1
                    WHEN json_type(selected.data, '$.finish') = 'text' THEN 2 ELSE 3 END,
               CASE WHEN json_type(selected.data, '$.finish') = 'text'
                          AND json_extract(selected.data, '$.finish') = 'error' THEN 1 ELSE 0 END,
               CASE coalesce(json_type(selected.data, '$.tokens.input'), 'missing')
                   WHEN 'missing' THEN 0
                   WHEN 'integer' THEN 1 WHEN 'real' THEN 2 WHEN 'null' THEN 3
                   WHEN 'text' THEN 4 WHEN 'true' THEN 5 WHEN 'false' THEN 6
                   WHEN 'object' THEN 7 WHEN 'array' THEN 8 ELSE 9 END,
               CASE WHEN json_type(selected.data, '$.tokens.input') IN ('integer', 'real')
                    THEN json_extract(selected.data, '$.tokens.input') END,
               CASE coalesce(json_type(selected.data, '$.tokens.output'), 'missing')
                   WHEN 'missing' THEN 0
                   WHEN 'integer' THEN 1 WHEN 'real' THEN 2 WHEN 'null' THEN 3
                   WHEN 'text' THEN 4 WHEN 'true' THEN 5 WHEN 'false' THEN 6
                   WHEN 'object' THEN 7 WHEN 'array' THEN 8 ELSE 9 END,
               CASE WHEN json_type(selected.data, '$.tokens.output') IN ('integer', 'real')
                    THEN json_extract(selected.data, '$.tokens.output') END,
               CASE coalesce(json_type(selected.data, '$.tokens.reasoning'), 'missing')
                   WHEN 'missing' THEN 0
                   WHEN 'integer' THEN 1 WHEN 'real' THEN 2 WHEN 'null' THEN 3
                   WHEN 'text' THEN 4 WHEN 'true' THEN 5 WHEN 'false' THEN 6
                   WHEN 'object' THEN 7 WHEN 'array' THEN 8 ELSE 9 END,
               CASE WHEN json_type(selected.data, '$.tokens.reasoning') IN ('integer', 'real')
                    THEN json_extract(selected.data, '$.tokens.reasoning') END,
               CASE coalesce(json_type(selected.data, '$.tokens.cache.read'), 'missing')
                   WHEN 'missing' THEN 0
                   WHEN 'integer' THEN 1 WHEN 'real' THEN 2 WHEN 'null' THEN 3
                   WHEN 'text' THEN 4 WHEN 'true' THEN 5 WHEN 'false' THEN 6
                   WHEN 'object' THEN 7 WHEN 'array' THEN 8 ELSE 9 END,
               CASE WHEN json_type(selected.data, '$.tokens.cache.read') IN ('integer', 'real')
                    THEN json_extract(selected.data, '$.tokens.cache.read') END,
               CASE coalesce(json_type(selected.data, '$.tokens.cache.write'), 'missing')
                   WHEN 'missing' THEN 0
                   WHEN 'integer' THEN 1 WHEN 'real' THEN 2 WHEN 'null' THEN 3
                   WHEN 'text' THEN 4 WHEN 'true' THEN 5 WHEN 'false' THEN 6
                   WHEN 'object' THEN 7 WHEN 'array' THEN 8 ELSE 9 END,
               CASE WHEN json_type(selected.data, '$.tokens.cache.write') IN ('integer', 'real')
                    THEN json_extract(selected.data, '$.tokens.cache.write') END,
               CASE WHEN json_type(selected.data, '$.model.id') IS NULL THEN 0
                    WHEN json_type(selected.data, '$.model.id') = 'text' THEN 1 ELSE 2 END,
               CASE WHEN json_type(selected.data, '$.model.id') = 'text'
                    THEN json_extract(selected.data, '$.model.id') END,
               CASE WHEN json_type(selected.data, '$.model.providerID') IS NULL THEN 0
                    WHEN json_type(selected.data, '$.model.providerID') = 'text' THEN 1 ELSE 2 END,
               CASE WHEN json_type(selected.data, '$.model.providerID') = 'text'
                    THEN json_extract(selected.data, '$.model.providerID') END,
               selected.session_version_type,
               selected.session_version
        FROM stats
        LEFT JOIN latest ON 1 = 1
        LEFT JOIN selected ON 1 = 1;
        """;

    private readonly string? _databasePath;
    public OpenCodeActivityReader(string? databasePath = null)
    {
        _databasePath = databasePath;
    }

    internal OpenCodeActivityReadResult Read(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return OpenCodeActivityReadResult.Error("platform_unsupported");

        string? path;
        try
        {
            var databaseOverride = Environment.GetEnvironmentVariable("OPENCODE_DB");
            if (_databasePath is null && StringComparer.Ordinal.Equals(databaseOverride, ":memory:"))
                return OpenCodeActivityReadResult.Unavailable("database_path_unavailable");
            path = _databasePath ?? ResolveDatabasePath(databaseOverride,
                ResolveDataRoot(Environment.GetEnvironmentVariable("XDG_DATA_HOME")));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OpenCodeActivityReadResult.Unavailable("database_path_unavailable");
        }

        if (path is null)
            return OpenCodeActivityReadResult.Unavailable("database_path_unavailable");
        if (!File.Exists(path))
            return OpenCodeActivityReadResult.Unavailable("database_missing");

        IntPtr database = IntPtr.Zero;
        int openResult;
        try
        {
            openResult = SqliteOpen(path, out database, SqliteOpenReadOnly, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return OpenCodeActivityReadResult.Error("sqlite_unavailable");
        }
        catch (EntryPointNotFoundException)
        {
            return OpenCodeActivityReadResult.Error("sqlite_unavailable");
        }
        if (openResult != SqliteOk || database == IntPtr.Zero)
        {
            if (database != IntPtr.Zero) _ = SqliteClose(database);
            return OpenCodeActivityReadResult.Error("database_open_failed");
        }

        try
        {
            _ = SqliteBusyTimeout(database, BusyTimeoutMilliseconds);
            _ = SqliteLimit(database, SqliteLimitLength, MaxJsonValueBytes);

            var startedAt = Stopwatch.GetTimestamp();
            SqliteProgressCallback progress = _ =>
                cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt) >= SqlExecutionLimit ? 1 : 0;
            using var cancellationRegistration = cancellationToken.Register(
                static state => SqliteInterrupt((IntPtr)state!), database);
            SqliteProgressHandler(database, SqlProgressInterval, progress, IntPtr.Zero);

            var transactionOpen = false;
            try
            {
                ExecuteNoRows(database, "BEGIN;");
                transactionOpen = true;
                var reading = ReadProjection(database, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ExecuteNoRows(database, "COMMIT;");
                transactionOpen = false;
                return reading;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SqliteReadException exception)
            {
                if (exception.ResultCode == SqliteInterruptCode && cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                return OpenCodeActivityReadResult.Error(
                    exception.ResultCode == SqliteTooBig ? "record_too_large" : "database_read_failed");
            }
            catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or ArgumentOutOfRangeException or OverflowException)
            {
                return OpenCodeActivityReadResult.Error("activity_record_invalid");
            }
            finally
            {
                SqliteProgressHandler(database, 0, null, IntPtr.Zero);
                if (transactionOpen) ExecuteNoRows(database, "ROLLBACK;");
                GC.KeepAlive(progress);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            return OpenCodeActivityReadResult.Error("sqlite_unavailable");
        }
        catch (EntryPointNotFoundException)
        {
            return OpenCodeActivityReadResult.Error("sqlite_unavailable");
        }
        catch (SqliteReadException exception)
        {
            return OpenCodeActivityReadResult.Error(
                exception.ResultCode == SqliteTooBig ? "record_too_large" : "database_read_failed");
        }
        catch (Exception)
        {
            return OpenCodeActivityReadResult.Error("database_read_failed");
        }
        finally
        {
            _ = SqliteClose(database);
        }
    }

    internal static string? ResolveDatabasePath(string? databaseOverride, string? dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(databaseOverride))
        {
            if (StringComparer.Ordinal.Equals(databaseOverride, ":memory:")) return null;
            if (Path.IsPathFullyQualified(databaseOverride)) return Path.GetFullPath(databaseOverride);
            if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot)) return null;
            return Path.GetFullPath(Path.Combine(dataRoot, "opencode", databaseOverride));
        }

        if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot)) return null;
        return Path.GetFullPath(Path.Combine(dataRoot, "opencode", "opencode.db"));
    }

    internal static string? ResolveDataRoot(string? xdgDataHome)
    {
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            return Path.IsPathFullyQualified(xdgDataHome) ? Path.GetFullPath(xdgDataHome) : null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)) return null;
        return Path.GetFullPath(Path.Combine(home, ".local", "share"));
    }

    private static OpenCodeActivityReadResult ReadProjection(IntPtr database, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = SqlitePrepare(database, ProjectionSql, -1, out var statement, IntPtr.Zero);
        if (result != SqliteOk || statement == IntPtr.Zero) throw new SqliteReadException(result);

        try
        {
            result = SqliteStep(statement);
            if (result != SqliteRow) throw new SqliteReadException(result);

            var reading = ReadProjectionRow(statement);
            cancellationToken.ThrowIfCancellationRequested();
            result = SqliteStep(statement);
            if (result != SqliteDone) throw new SqliteReadException(result);
            return reading;
        }
        finally
        {
            _ = SqliteFinalize(statement);
        }
    }

    private static OpenCodeActivityReadResult ReadProjectionRow(IntPtr statement)
    {
        var rowCount = SqliteColumnInt64(statement, RowCountColumn);
        var assistantCount = SqliteColumnInt64(statement, AssistantCountColumn);
        if (rowCount == 0)
            return OpenCodeActivityReadResult.Unavailable("activity_absent");
        if (assistantCount == 0)
            return OpenCodeActivityReadResult.Unavailable(rowCount == WindowSize
                ? "activity_window_exhausted"
                : "activity_absent");

        IdentifierRead version;
        try
        {
            version = ReadIdentifier(statement, SessionVersionTypeColumn, SessionVersionValueColumn);
        }
        catch (DecoderFallbackException)
        {
            return OpenCodeActivityReadResult.Error("source_version_invalid");
        }
        if (version.Status == IdentifierReadStatus.Missing)
            return OpenCodeActivityReadResult.Error("source_version_missing");
        if (version.Status == IdentifierReadStatus.Invalid)
            return OpenCodeActivityReadResult.Error("source_version_invalid");
        if (!StringComparer.Ordinal.Equals(version.Value, SupportedOpenCodeVersion))
            return OpenCodeActivityReadResult.Unavailable("source_version_unsupported");

        if (SqliteColumnInt64(statement, HasErrorColumn) != 0 ||
            SqliteColumnInt64(statement, FinishIsErrorColumn) != 0)
            return OpenCodeActivityReadResult.Unavailable("activity_failed");

        var completedType = SqliteColumnInt64(statement, CompletedTypeColumn);
        if (completedType is 0 or 3)
            return OpenCodeActivityReadResult.Unavailable("activity_incomplete");
        if (completedType != 1 || SqliteColumnType(statement, CompletedValueColumn) != SqliteInteger)
            return OpenCodeActivityReadResult.Error("activity_timestamp_invalid");

        var finishType = SqliteColumnInt64(statement, FinishTypeColumn);
        if (finishType == 3)
            return OpenCodeActivityReadResult.Error("activity_finish_invalid");

        var input = ReadTokenCount(statement, InputTypeColumn, InputValueColumn);
        var output = ReadTokenCount(statement, OutputTypeColumn, OutputValueColumn);
        var reasoning = ReadTokenCount(statement, ReasoningTypeColumn, ReasoningValueColumn);
        var cacheRead = ReadTokenCount(statement, CacheReadTypeColumn, CacheReadValueColumn);
        var cacheWrite = ReadTokenCount(statement, CacheWriteTypeColumn, CacheWriteValueColumn);
        if (input.Status == TokenReadStatus.Missing || output.Status == TokenReadStatus.Missing ||
            reasoning.Status == TokenReadStatus.Missing || cacheRead.Status == TokenReadStatus.Missing ||
            cacheWrite.Status == TokenReadStatus.Missing)
            return OpenCodeActivityReadResult.Unavailable("activity_usage_incomplete");
        if (input.Status != TokenReadStatus.Valid || output.Status != TokenReadStatus.Valid ||
            reasoning.Status != TokenReadStatus.Valid || cacheRead.Status != TokenReadStatus.Valid ||
            cacheWrite.Status != TokenReadStatus.Valid)
            return OpenCodeActivityReadResult.Error("activity_usage_invalid");

        var modelId = ReadIdentifier(statement, ModelTypeColumn, ModelValueColumn);
        var providerId = ReadIdentifier(statement, ProviderTypeColumn, ProviderValueColumn);
        if (modelId.Status == IdentifierReadStatus.Missing || providerId.Status == IdentifierReadStatus.Missing)
            return OpenCodeActivityReadResult.Unavailable("activity_model_incomplete");
        if (modelId.Status != IdentifierReadStatus.Valid || providerId.Status != IdentifierReadStatus.Valid)
            return OpenCodeActivityReadResult.Error("activity_model_invalid");

        var completedMilliseconds = SqliteColumnInt64(statement, CompletedValueColumn);
        var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(completedMilliseconds);
        return OpenCodeActivityReadResult.Ready(new OpenCodeActivitySample(
            input.Value, output.Value, reasoning.Value, cacheRead.Value, cacheWrite.Value,
            modelId.Value!, providerId.Value!, occurredAt));
    }

    private static TokenRead ReadTokenCount(IntPtr statement, int typeColumn, int valueColumn)
    {
        var type = SqliteColumnInt64(statement, typeColumn);
        if (type == 0) return new(TokenReadStatus.Missing, 0);
        if (type is not (1 or 2)) return new(TokenReadStatus.Invalid, 0);

        var sqliteType = SqliteColumnType(statement, valueColumn);
        if (sqliteType == SqliteInteger)
        {
            var value = SqliteColumnInt64(statement, valueColumn);
            return value is >= 0 and <= MaxExactJsonInteger
                ? new(TokenReadStatus.Valid, value)
                : new(TokenReadStatus.Invalid, 0);
        }

        if (sqliteType != SqliteFloat) return new(TokenReadStatus.Invalid, 0);
        var number = SqliteColumnDouble(statement, valueColumn);
        if (!double.IsFinite(number) || number < 0 || number > MaxExactJsonInteger || Math.Truncate(number) != number)
            return new(TokenReadStatus.Invalid, 0);
        return new(TokenReadStatus.Valid, checked((long)number));
    }

    private static IdentifierRead ReadIdentifier(IntPtr statement, int typeColumn, int valueColumn)
    {
        if (SqliteColumnInt64(statement, typeColumn) == 0)
            return new(IdentifierReadStatus.Missing, null);
        if (SqliteColumnInt64(statement, typeColumn) != 1 || SqliteColumnType(statement, valueColumn) != SqliteText)
            return new(IdentifierReadStatus.Invalid, null);

        var length = SqliteColumnBytes(statement, valueColumn);
        if (length is <= 0 or > 128) return new(IdentifierReadStatus.Invalid, null);
        var pointer = SqliteColumnText(statement, valueColumn);
        if (pointer == IntPtr.Zero) return new(IdentifierReadStatus.Invalid, null);

        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        var value = new UTF8Encoding(false, true).GetString(bytes);
        foreach (var character in value)
        {
            if (char.IsControl(character)) return new(IdentifierReadStatus.Invalid, null);
        }
        return new(IdentifierReadStatus.Valid, value);
    }

    private static void ExecuteNoRows(IntPtr database, string sql)
    {
        var result = SqlitePrepare(database, sql, -1, out var statement, IntPtr.Zero);
        if (result != SqliteOk || statement == IntPtr.Zero) throw new SqliteReadException(result);
        try
        {
            result = SqliteStep(statement);
            if (result != SqliteDone) throw new SqliteReadException(result);
        }
        finally
        {
            _ = SqliteFinalize(statement);
        }
    }

    private static long SqliteColumnInt64(IntPtr statement, int column) => SqliteColumnInt64Native(statement, column);

    private sealed class SqliteReadException(int resultCode) : Exception
    {
        public int ResultCode { get; } = resultCode & 0xff;
    }

    private enum TokenReadStatus { Valid, Missing, Invalid }
    private readonly record struct TokenRead(TokenReadStatus Status, long Value);
    private enum IdentifierReadStatus { Valid, Missing, Invalid }
    private readonly record struct IdentifierRead(IdentifierReadStatus Status, string? Value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteProgressCallback(IntPtr context);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, out IntPtr database,
        int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqlitePrepare(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteStep(IntPtr statement);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteColumnType(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    private static extern long SqliteColumnInt64Native(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_double", CallingConvention = CallingConvention.Cdecl)]
    private static extern double SqliteColumnDouble(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteColumnBytes(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SqliteColumnText(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteFinalize(IntPtr statement);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteClose(IntPtr database);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteBusyTimeout(IntPtr database, int milliseconds);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_limit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteLimit(IntPtr database, int id, int newValue);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SqliteProgressHandler(IntPtr database, int instructionInterval,
        SqliteProgressCallback? callback, IntPtr context);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_interrupt", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SqliteInterrupt(IntPtr database);
}
