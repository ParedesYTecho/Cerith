#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cerith.Core.History;

public sealed record HistoryPoint(DateTimeOffset At, MetricState State, MetricConfidence Confidence, MetricValue? Value);

public sealed record HistoryBucket(DateTimeOffset Start, long Count, double Min, double Max, double Average);

public sealed record HistoryRetention(TimeSpan Raw, TimeSpan Minute, TimeSpan Hour)
{
    public static HistoryRetention Default { get; } = new(TimeSpan.FromHours(12), TimeSpan.FromDays(90), TimeSpan.FromDays(730));
}

public readonly record struct HistoryAppendResult(int Written, int Rejected);

/// <summary>
/// Local time-series store over winsqlite3. One writer (<see cref="OpenWriter"/>), any number of read-only readers.
/// Tiers: raw samples, one-minute rollups, one-hour rollups (numeric Fresh values only).
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private const int SchemaVersion = 1, ApplicationId = 0x43455248;
    private const long MinuteMs = 60_000, HourMs = 3_600_000, RollupDelayMs = 5 * MinuteMs;
    private const int MaxTextBytes = 1024;
    private const string NumericKinds = "1,2"; // MetricValueKind.Double, Int64

    private readonly SqliteHandle _db;
    private readonly bool _writer;
    private readonly Dictionary<string, (long Id, MetricValueKind Kind)> _metrics = new(StringComparer.Ordinal);
    private readonly Dictionary<long, MetricState> _lastState = new();
    private SqliteHandle.Statement? _insert;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cerith", "history.db");

    private HistoryStore(SqliteHandle db, bool writer)
    {
        _db = db;
        _writer = writer;
    }

    public static HistoryStore OpenWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db = new SqliteHandle(path, readOnly: false, busyMilliseconds: 5000);
        try
        {
            var existing = db.ScalarInt64("PRAGMA user_version") ?? 0;
            var appId = db.ScalarInt64("PRAGMA application_id") ?? 0;
            var hasTables = db.ScalarInt64("SELECT count(*) FROM sqlite_master") > 0;
            if (hasTables && appId != ApplicationId) throw new InvalidDataException("Existing file is not a Cerith history store; preserved.");
            if (existing > SchemaVersion) throw new NotSupportedException("History store was written by a newer version; preserved.");

            _ = db.ScalarInt64("PRAGMA journal_mode=WAL");
            db.Execute("PRAGMA synchronous=NORMAL");
            db.Execute("BEGIN IMMEDIATE");
            db.Execute("CREATE TABLE IF NOT EXISTS metric(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, kind INTEGER NOT NULL)");
            db.Execute("CREATE TABLE IF NOT EXISTS sample(metric INTEGER NOT NULL, at INTEGER NOT NULL, state INTEGER NOT NULL, " +
                       "confidence INTEGER NOT NULL, num, txt TEXT, PRIMARY KEY(metric, at)) WITHOUT ROWID");
            db.Execute("CREATE TABLE IF NOT EXISTS rollup(bucket INTEGER NOT NULL, metric INTEGER NOT NULL, start INTEGER NOT NULL, " +
                       "n INTEGER NOT NULL, min REAL NOT NULL, max REAL NOT NULL, sum REAL NOT NULL, PRIMARY KEY(bucket, metric, start)) WITHOUT ROWID");
            db.Execute("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value INTEGER NOT NULL) WITHOUT ROWID");
            db.Execute($"PRAGMA application_id={ApplicationId}");
            db.Execute($"PRAGMA user_version={SchemaVersion}");
            db.Execute("COMMIT");

            var store = new HistoryStore(db, writer: true);
            store.LoadMetrics();
            return store;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public static HistoryStore OpenReader(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("History store does not exist.");
        var db = new SqliteHandle(path, readOnly: true, busyMilliseconds: 2000);
        try
        {
            if ((db.ScalarInt64("PRAGMA application_id") ?? 0) != ApplicationId) throw new InvalidDataException("Not a Cerith history store.");
            if ((db.ScalarInt64("PRAGMA user_version") ?? 0) > SchemaVersion) throw new NotSupportedException("History store is newer than this build.");
            return new HistoryStore(db, writer: false);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>Writes the batch in one transaction. Non-finite or oversized values and kind changes are rejected and counted.</summary>
    public HistoryAppendResult Append(IReadOnlyList<MetricSnapshot> batch)
    {
        EnsureWriter();
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return default;

        _insert ??= _db.Prepare("INSERT OR REPLACE INTO sample(metric, at, state, confidence, num, txt) VALUES(?1,?2,?3,?4,?5,?6)");
        int written = 0, rejected = 0;
        _db.Execute("BEGIN IMMEDIATE");
        try
        {
            foreach (var snapshot in batch)
            {
                if (TryWrite(snapshot)) written++; else rejected++;
            }

            _db.Execute("COMMIT");
        }
        catch
        {
            TryRollback();
            _lastState.Clear();
            throw;
        }

        return new HistoryAppendResult(written, rejected);
    }

    public IReadOnlyList<HistoryPoint> ReadRaw(MetricId id, DateTimeOffset from, DateTimeOffset to, int limit = 100_000)
    {
        ArgumentNullException.ThrowIfNull(id);
        var points = new List<HistoryPoint>();
        if (!TryLookup(id, out var metric)) return points;

        using var statement = _db.Prepare("SELECT at, state, confidence, num, txt FROM sample WHERE metric=?1 AND at>=?2 AND at<?3 ORDER BY at LIMIT ?4");
        statement.Bind(1, metric.Id);
        statement.Bind(2, from.ToUnixTimeMilliseconds());
        statement.Bind(3, to.ToUnixTimeMilliseconds());
        statement.Bind(4, (long)limit);
        while (statement.Step())
        {
            var state = (MetricState)statement.Int64(1);
            MetricValue? value = state is MetricState.Fresh or MetricState.Stale ? Decode(metric.Kind, statement) : null;
            points.Add(new HistoryPoint(DateTimeOffset.FromUnixTimeMilliseconds(statement.Int64(0)), state, (MetricConfidence)statement.Int64(2), value));
        }

        return points;
    }

    /// <summary>Rollup buckets of the given width (one minute or one hour) for numeric metrics.</summary>
    public IReadOnlyList<HistoryBucket> ReadRollup(MetricId id, TimeSpan width, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (width != TimeSpan.FromMinutes(1) && width != TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(width), "Rollup width must be one minute or one hour.");
        var buckets = new List<HistoryBucket>();
        if (!TryLookup(id, out var metric)) return buckets;

        using var statement = _db.Prepare("SELECT start, n, min, max, sum FROM rollup WHERE bucket=?1 AND metric=?2 AND start>=?3 AND start<?4 ORDER BY start");
        statement.Bind(1, (long)width.TotalMilliseconds);
        statement.Bind(2, metric.Id);
        statement.Bind(3, from.ToUnixTimeMilliseconds());
        statement.Bind(4, to.ToUnixTimeMilliseconds());
        while (statement.Step())
        {
            var count = statement.Int64(1);
            buckets.Add(new HistoryBucket(DateTimeOffset.FromUnixTimeMilliseconds(statement.Int64(0)), count,
                statement.Double(2), statement.Double(3), statement.Double(4) / count));
        }

        return buckets;
    }

    /// <summary>Rolls completed buckets up, then deletes data past retention. Raw data is never deleted before it has been rolled up.</summary>
    public void Maintain(DateTimeOffset now, HistoryRetention retention)
    {
        EnsureWriter();
        ArgumentNullException.ThrowIfNull(retention);
        var nowMs = now.ToUnixTimeMilliseconds();
        var safeNow = nowMs - RollupDelayMs;

        _db.Execute("BEGIN IMMEDIATE");
        try
        {
            RollUpMinutes(safeNow);
            RollUpHours(safeNow);
            Prune("sample", "at", nowMs - (long)retention.Raw.TotalMilliseconds, "minute_until", "");
            Prune("rollup", "start", nowMs - (long)retention.Minute.TotalMilliseconds, "hour_until", $"bucket={MinuteMs} AND ");
            Prune("rollup", "start", nowMs - (long)retention.Hour.TotalMilliseconds, null, $"bucket={HourMs} AND ");
            _db.Execute("COMMIT");
        }
        catch
        {
            TryRollback();
            throw;
        }
    }

    public void Dispose()
    {
        _insert?.Dispose();
        _insert = null;
        _db.Dispose();
    }

    private bool TryWrite(MetricSnapshot snapshot)
    {
        var hasValue = snapshot.Value.HasValue && snapshot.State is MetricState.Fresh or MetricState.Stale;
        if (!hasValue && _metrics.TryGetValue(snapshot.MetricId.Value, out var known) &&
            _lastState.TryGetValue(known.Id, out var last) && last == snapshot.State)
            return true; // state unchanged since the last stored row: nothing new to say

        var kind = hasValue ? snapshot.Value!.Value.Kind : MetricValueKind.None;
        if (!Resolve(snapshot.MetricId.Value, kind, out var metric)) return false;

        object? number = null;
        string? text = null;
        if (hasValue && !TryEncode(snapshot.Value!.Value, out number, out text)) return false;

        _insert!.Reset();
        _insert.Bind(1, metric.Id);
        _insert.Bind(2, snapshot.ObservedAt.ToUnixTimeMilliseconds());
        _insert.Bind(3, (long)snapshot.State);
        _insert.Bind(4, (long)snapshot.Confidence);
        switch (number)
        {
            case long integer: _insert.Bind(5, integer); break;
            case double real: _insert.Bind(5, real); break;
            default: _insert.BindNull(5); break;
        }

        if (text is null) _insert.BindNull(6); else _insert.Bind(6, text);
        _insert.StepDone();
        _lastState[metric.Id] = snapshot.State;
        return true;
    }

    private static bool TryEncode(MetricValue value, out object? number, out string? text)
    {
        number = null;
        text = null;
        switch (value.Kind)
        {
            case MetricValueKind.Double:
                var real = value.AsDouble();
                if (!double.IsFinite(real)) return false;
                number = real;
                return true;
            case MetricValueKind.Int64: number = value.AsInt64(); return true;
            case MetricValueKind.Boolean: number = value.AsBoolean() ? 1L : 0L; return true;
            case MetricValueKind.TimeSpan: number = value.AsTimeSpan().Ticks; return true;
            case MetricValueKind.DateTimeOffset: number = value.AsDateTimeOffset().ToUnixTimeMilliseconds(); return true;
            case MetricValueKind.Decimal: text = value.AsDecimal().ToString(CultureInfo.InvariantCulture); return true;
            case MetricValueKind.String:
                text = value.AsString();
                return Encoding.UTF8.GetByteCount(text) <= MaxTextBytes;
            default: return false;
        }
    }

    private static MetricValue? Decode(MetricValueKind kind, SqliteHandle.Statement row)
    {
        if (kind == MetricValueKind.None) return null;
        if (kind is MetricValueKind.Decimal or MetricValueKind.String)
        {
            var text = row.Text(4);
            if (text is null) return null;
            return kind == MetricValueKind.String ? MetricValue.From(text)
                : MetricValue.From(decimal.Parse(text, CultureInfo.InvariantCulture));
        }

        if (row.IsNull(3)) return null;
        return kind switch
        {
            MetricValueKind.Double => MetricValue.From(row.Double(3)),
            MetricValueKind.Int64 => MetricValue.From(row.Int64(3)),
            MetricValueKind.Boolean => MetricValue.From(row.Int64(3) != 0),
            MetricValueKind.TimeSpan => MetricValue.From(TimeSpan.FromTicks(row.Int64(3))),
            MetricValueKind.DateTimeOffset => MetricValue.From(DateTimeOffset.FromUnixTimeMilliseconds(row.Int64(3))),
            _ => null
        };
    }

    // A metric's value kind is fixed by its first valued sample; a later different kind is rejected.
    private bool Resolve(string name, MetricValueKind kind, out (long Id, MetricValueKind Kind) metric)
    {
        if (_metrics.TryGetValue(name, out metric))
        {
            if (kind == MetricValueKind.None || metric.Kind == kind) return true;
            if (metric.Kind != MetricValueKind.None) return false;
            using var update = _db.Prepare("UPDATE metric SET kind=?1 WHERE id=?2");
            update.Bind(1, (long)kind);
            update.Bind(2, metric.Id);
            update.StepDone();
            metric = (metric.Id, kind);
            _metrics[name] = metric;
            return true;
        }

        using var insert = _db.Prepare("INSERT INTO metric(name, kind) VALUES(?1, ?2)");
        insert.Bind(1, name);
        insert.Bind(2, (long)kind);
        insert.StepDone();
        metric = (_db.ScalarInt64("SELECT last_insert_rowid()")!.Value, kind);
        _metrics[name] = metric;
        return true;
    }

    private bool TryLookup(MetricId id, out (long Id, MetricValueKind Kind) metric)
    {
        if (_metrics.TryGetValue(id.Value, out metric)) return true;
        using var statement = _db.Prepare("SELECT id, kind FROM metric WHERE name=?1");
        statement.Bind(1, id.Value);
        if (!statement.Step()) return false;
        metric = (statement.Int64(0), (MetricValueKind)statement.Int64(1));
        return true;
    }

    private void LoadMetrics()
    {
        using var statement = _db.Prepare("SELECT id, name, kind FROM metric");
        while (statement.Step())
            _metrics[statement.Text(1)!] = (statement.Int64(0), (MetricValueKind)statement.Int64(2));
    }

    private void RollUpMinutes(long safeNow)
    {
        var until = safeNow / MinuteMs * MinuteMs;
        var from = Meta("minute_until") ?? _db.ScalarInt64("SELECT min(at) FROM sample") / MinuteMs * MinuteMs;
        if (from is null || from >= until) return;

        using var statement = _db.Prepare(
            $"INSERT OR REPLACE INTO rollup(bucket, metric, start, n, min, max, sum) " +
            $"SELECT {MinuteMs}, s.metric, s.at / {MinuteMs} * {MinuteMs}, count(*), min(s.num), max(s.num), sum(s.num) " +
            $"FROM sample s JOIN metric m ON m.id = s.metric " +
            $"WHERE s.at >= ?1 AND s.at < ?2 AND s.state = {(int)MetricState.Fresh} AND m.kind IN ({NumericKinds}) AND s.num IS NOT NULL " +
            $"GROUP BY s.metric, s.at / {MinuteMs}");
        statement.Bind(1, from.Value);
        statement.Bind(2, until);
        statement.StepDone();
        SetMeta("minute_until", until);
    }

    private void RollUpHours(long safeNow)
    {
        var minuteUntil = Meta("minute_until");
        if (minuteUntil is null) return;
        var until = Math.Min(safeNow, minuteUntil.Value) / HourMs * HourMs;
        var from = Meta("hour_until") ?? _db.ScalarInt64($"SELECT min(start) FROM rollup WHERE bucket={MinuteMs}") / HourMs * HourMs;
        if (from is null || from >= until) return;

        using var statement = _db.Prepare(
            $"INSERT OR REPLACE INTO rollup(bucket, metric, start, n, min, max, sum) " +
            $"SELECT {HourMs}, metric, start / {HourMs} * {HourMs}, sum(n), min(min), max(max), sum(sum) FROM rollup " +
            $"WHERE bucket = {MinuteMs} AND start >= ?1 AND start < ?2 GROUP BY metric, start / {HourMs}");
        statement.Bind(1, from.Value);
        statement.Bind(2, until);
        statement.StepDone();
        SetMeta("hour_until", until);
    }

    // Deletes rows older than the cutoff, but never past the watermark proving they were rolled up.
    private void Prune(string table, string column, long cutoff, string? watermarkKey, string prefix)
    {
        var limit = cutoff;
        if (watermarkKey is not null)
        {
            var watermark = Meta(watermarkKey);
            if (watermark is null) return;
            limit = Math.Min(limit, watermark.Value);
        }

        using var statement = _db.Prepare($"DELETE FROM {table} WHERE {prefix}{column} < ?1");
        statement.Bind(1, limit);
        statement.StepDone();
    }

    private long? Meta(string key)
    {
        using var statement = _db.Prepare("SELECT value FROM meta WHERE key=?1");
        statement.Bind(1, key);
        return statement.Step() ? statement.Int64(0) : null;
    }

    private void SetMeta(string key, long value)
    {
        using var statement = _db.Prepare("INSERT OR REPLACE INTO meta(key, value) VALUES(?1, ?2)");
        statement.Bind(1, key);
        statement.Bind(2, value);
        statement.StepDone();
    }

    private bool TryRollback()
    {
        try
        {
            _db.Execute("ROLLBACK");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void EnsureWriter()
    {
        if (!_writer) throw new InvalidOperationException("History store was opened read-only.");
    }
}
