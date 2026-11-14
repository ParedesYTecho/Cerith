#nullable enable

using System;
using System.Collections.Generic;

namespace Cerith.Core.History;

public sealed record HistoryAiEvent(
    DateTimeOffset At, string Source, string Model, string Provider,
    long InputTokens, long OutputTokens, long ReasoningTokens, long CacheReadTokens, long CacheWriteTokens);

public sealed partial class HistoryStore
{
    private const string OpenCodePrefix = "ai.opencode.activity.latest.";
    private const string OpenCodeSource = "opencode";

    private static readonly string[] OpenCodeTokenFields =
        ["tokens.input", "tokens.output", "tokens.reasoning", "tokens.cache.read", "tokens.cache.write"];

    private readonly Dictionary<string, MetricSnapshot> _latestActivity = new(StringComparer.Ordinal);

    /// <summary>
    /// Activity events observed in the range, oldest first. An event is one distinct "latest activity" the OpenCode collector
    /// reported; activities that happened and were superseded between two polls are not observed and are not recorded.
    /// </summary>
    public IReadOnlyList<HistoryAiEvent> ReadAiEvents(DateTimeOffset from, DateTimeOffset to, int limit = 100_000)
    {
        var events = new List<HistoryAiEvent>();
        using var statement = _db.Prepare("SELECT at, source, model, provider, input, output, reasoning, cache_read, cache_write " +
                                          "FROM ai_event WHERE at>=?1 AND at<?2 ORDER BY at LIMIT ?3");
        statement.Bind(1, from.ToUnixTimeMilliseconds());
        statement.Bind(2, to.ToUnixTimeMilliseconds());
        statement.Bind(3, (long)limit);
        while (statement.Step())
            events.Add(new HistoryAiEvent(DateTimeOffset.FromUnixTimeMilliseconds(statement.Int64(0)), statement.Text(1)!, statement.Text(2)!,
                statement.Text(3)!, statement.Int64(4), statement.Int64(5), statement.Int64(6), statement.Int64(7), statement.Int64(8)));
        return events;
    }

    // The collector publishes its eight values in one round with one ObservedAt, occurred_at last. A new occurred_at with a complete
    // Fresh round is a new activity; (source, at) is unique, so polls that repeat the same activity add nothing, even across restarts.
    private void TrackAiEvent(MetricSnapshot snapshot)
    {
        var name = snapshot.MetricId.Value;
        if (!name.StartsWith(OpenCodePrefix, StringComparison.Ordinal)) return;
        if (snapshot.State != MetricState.Fresh)
        {
            _latestActivity.Remove(name);
            return;
        }

        _latestActivity[name] = snapshot;
        if (name != OpenCodePrefix + "occurred_at" || snapshot.Value is not { Kind: MetricValueKind.DateTimeOffset } occurred) return;

        var tokens = new long[OpenCodeTokenFields.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!TryRound(OpenCodePrefix + OpenCodeTokenFields[i], snapshot.ObservedAt, MetricValueKind.Int64, out var value)) return;
            tokens[i] = value!.Value.AsInt64();
        }

        if (!TryRound(OpenCodePrefix + "model.id", snapshot.ObservedAt, MetricValueKind.String, out var model) ||
            !TryRound(OpenCodePrefix + "model.provider", snapshot.ObservedAt, MetricValueKind.String, out var provider)) return;

        using var statement = _db.Prepare("INSERT OR IGNORE INTO ai_event(source, at, model, provider, input, output, reasoning, cache_read, cache_write) " +
                                          "VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)");
        statement.Bind(1, OpenCodeSource);
        statement.Bind(2, occurred.AsDateTimeOffset().ToUnixTimeMilliseconds());
        statement.Bind(3, model!.Value.AsString());
        statement.Bind(4, provider!.Value.AsString());
        for (var i = 0; i < tokens.Length; i++) statement.Bind(5 + i, tokens[i]);
        statement.StepDone();
    }

    private bool TryRound(string name, DateTimeOffset observedAt, MetricValueKind kind, out MetricValue? value)
    {
        value = null;
        if (!_latestActivity.TryGetValue(name, out var snapshot) || snapshot.ObservedAt != observedAt) return false;
        if (snapshot.Value is not { } present || present.Kind != kind) return false;
        value = present;
        return true;
    }

    private void PruneAiEvents(long cutoff)
    {
        using var statement = _db.Prepare("DELETE FROM ai_event WHERE at < ?1");
        statement.Bind(1, cutoff);
        statement.StepDone();
    }
}
