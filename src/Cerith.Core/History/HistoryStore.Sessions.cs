#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace Cerith.Core.History;

public sealed record HistorySessionStat(MetricId MetricId, long Count, double Min, double Max, double Average);

public sealed record HistorySession(long Id, string Label, DateTimeOffset Start, DateTimeOffset? End, IReadOnlyList<HistorySessionStat> Stats);

public sealed partial class HistoryStore
{
    /// <summary>The metric whose Fresh string value names the running game (D-022); its lifetime defines the session.</summary>
    public static MetricId SessionMetric { get; } = new("game.session.application");

    private sealed class Aggregate
    {
        public long Count;
        public double Min = double.PositiveInfinity;
        public double Max = double.NegativeInfinity;
        public double Sum;
    }

    private long? _sessionId;
    private string? _sessionLabel;
    private readonly Dictionary<long, Aggregate> _stats = new();
    private readonly HashSet<long> _dirtyStats = new();

    /// <summary>Sessions that overlap the range, newest first, with statistics over every Fresh numeric value recorded during each.</summary>
    public IReadOnlyList<HistorySession> ReadSessions(DateTimeOffset from, DateTimeOffset to)
    {
        var sessions = new List<(long Id, string Label, long Start, long? End)>();
        using (var statement = _db.Prepare("SELECT id, label, start, \"end\" FROM session WHERE start<?2 AND COALESCE(\"end\", 9223372036854775807)>=?1 ORDER BY start DESC"))
        {
            statement.Bind(1, from.ToUnixTimeMilliseconds());
            statement.Bind(2, to.ToUnixTimeMilliseconds());
            while (statement.Step())
                sessions.Add((statement.Int64(0), statement.Text(1)!, statement.Int64(2), statement.IsNull(3) ? null : statement.Int64(3)));
        }

        var result = new List<HistorySession>();
        foreach (var session in sessions)
        {
            var stats = new List<HistorySessionStat>();
            using var statement = _db.Prepare("SELECT m.name, s.n, s.min, s.max, s.sum FROM session_stat s JOIN metric m ON m.id=s.metric WHERE s.session=?1 ORDER BY m.name");
            statement.Bind(1, session.Id);
            while (statement.Step())
                stats.Add(new HistorySessionStat(new MetricId(statement.Text(0)!), statement.Int64(1), statement.Double(2), statement.Double(3), statement.Double(4) / statement.Int64(1)));
            result.Add(new HistorySession(session.Id, session.Label, DateTimeOffset.FromUnixTimeMilliseconds(session.Start),
                session.End is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(session.End.Value), stats));
        }

        return result;
    }

    private void TrackSession(MetricSnapshot snapshot)
    {
        var at = snapshot.ObservedAt.ToUnixTimeMilliseconds();
        if (snapshot.MetricId == SessionMetric)
        {
            var label = snapshot.State == MetricState.Fresh && snapshot.Value is { Kind: MetricValueKind.String } value ? value.AsString() : null;
            if (_sessionId is not null && label == _sessionLabel) return;
            if (_sessionId is not null) CloseSession(at);
            if (label is not null) OpenSession(label, at);
            return;
        }

        if (_sessionId is null || snapshot.State != MetricState.Fresh || snapshot.Value is not { } numeric) return;
        if (numeric.Kind is not (MetricValueKind.Double or MetricValueKind.Int64)) return;
        var metric = _metrics[snapshot.MetricId.Value].Id;
        if (!_stats.TryGetValue(metric, out var aggregate)) _stats[metric] = aggregate = new Aggregate();
        var number = numeric.Kind == MetricValueKind.Double ? numeric.AsDouble() : numeric.AsInt64();
        aggregate.Count++;
        aggregate.Min = Math.Min(aggregate.Min, number);
        aggregate.Max = Math.Max(aggregate.Max, number);
        aggregate.Sum += number;
        _dirtyStats.Add(metric);
    }

    private void OpenSession(string label, long at)
    {
        using var statement = _db.Prepare("INSERT INTO session(label, start) VALUES(?1, ?2)");
        statement.Bind(1, label);
        statement.Bind(2, at);
        statement.StepDone();
        _sessionId = _db.ScalarInt64("SELECT last_insert_rowid()");
        _sessionLabel = label;
    }

    private void CloseSession(long at)
    {
        PersistSessionStats();
        using var statement = _db.Prepare("UPDATE session SET \"end\"=max(?1, start) WHERE id=?2");
        statement.Bind(1, at);
        statement.Bind(2, _sessionId!.Value);
        statement.StepDone();
        ClearSession();
    }

    private void PersistSessionStats()
    {
        if (_sessionId is null || _dirtyStats.Count == 0) return;
        using var statement = _db.Prepare("INSERT OR REPLACE INTO session_stat(session, metric, n, min, max, sum) VALUES(?1,?2,?3,?4,?5,?6)");
        foreach (var metric in _dirtyStats)
        {
            var aggregate = _stats[metric];
            statement.Reset();
            statement.Bind(1, _sessionId.Value);
            statement.Bind(2, metric);
            statement.Bind(3, aggregate.Count);
            statement.Bind(4, aggregate.Min);
            statement.Bind(5, aggregate.Max);
            statement.Bind(6, aggregate.Sum);
            statement.StepDone();
        }

        _dirtyStats.Clear();
    }

    // A process that died without a final snapshot leaves its session open: end it at the last instant the store saw.
    private void CloseOpenSessions()
    {
        _db.Execute("BEGIN IMMEDIATE");
        try
        {
            using var statement = _db.Prepare("UPDATE session SET \"end\"=max(COALESCE((SELECT value FROM meta WHERE key='last_seen'), start), start) WHERE \"end\" IS NULL");
            statement.StepDone();
            _db.Execute("COMMIT");
        }
        catch
        {
            TryRollback();
            throw;
        }

        ClearSession();
    }

    private bool TryCloseOpenSessions()
    {
        try
        {
            CloseOpenSessions();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // After a rolled-back batch the in-memory session must match what is actually stored.
    private void RestoreSession()
    {
        ClearSession();
        using (var statement = _db.Prepare("SELECT id, label FROM session WHERE \"end\" IS NULL ORDER BY id DESC LIMIT 1"))
        {
            if (!statement.Step()) return;
            _sessionId = statement.Int64(0);
            _sessionLabel = statement.Text(1);
        }

        using var stats = _db.Prepare("SELECT metric, n, min, max, sum FROM session_stat WHERE session=?1");
        stats.Bind(1, _sessionId!.Value);
        while (stats.Step())
            _stats[stats.Int64(0)] = new Aggregate { Count = stats.Int64(1), Min = stats.Double(2), Max = stats.Double(3), Sum = stats.Double(4) };
    }

    private void ClearSession()
    {
        _sessionId = null;
        _sessionLabel = null;
        _stats.Clear();
        _dirtyStats.Clear();
    }

    private void PruneSessions(long cutoff)
    {
        using (var statement = _db.Prepare("DELETE FROM session_stat WHERE session IN (SELECT id FROM session WHERE \"end\" < ?1)"))
        {
            statement.Bind(1, cutoff);
            statement.StepDone();
        }

        using var sessions = _db.Prepare("DELETE FROM session WHERE \"end\" < ?1");
        sessions.Bind(1, cutoff);
        sessions.StepDone();
    }
}
