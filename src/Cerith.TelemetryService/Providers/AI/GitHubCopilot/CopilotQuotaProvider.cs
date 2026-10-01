#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.GitHubCopilot;

internal sealed class CopilotQuotaProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan RetryMaximumDelay = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan StopPublishTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumSleepChunk = TimeSpan.FromDays(1);
    private static readonly TimeSpan LifecycleObservationInterval = TimeSpan.FromSeconds(1);

    private const string Source = "GitHubCopilot.GhCli.ReadOnly.v1";

    private static readonly string[] Buckets = ["chat", "completions", "premium_interactions"];
    private static readonly MetricDefinition[] Definitions = CreateDefinitions();

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly CopilotQuotaAcquirer _acquirer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, CachedMetric> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MissingMetric> _missing = new(StringComparer.Ordinal);

    private string? _activeAccountKey;
    private MetricState _emptyState = MetricState.Unavailable;
    private string? _emptyErrorCode;
    private DateTimeOffset _emptyObservedAt;
    private DateTimeOffset? _lastUtcNow;
    private long _lastTimestamp;
    private long _accountGeneration;
    private long _publicationGeneration;
    private long _nextPollTimestamp;
    private int _consecutiveFailures;
    private int _runStarted;
    private int _disposed;
    private CancellationTokenSource? _activePublicationStop;

    internal CopilotQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new CopilotQuotaAcquirer(), TimeProvider.System)
    {
    }

    internal CopilotQuotaProvider(MetricRegistry registry, MetricBus bus,
        CopilotQuotaAcquirer acquirer, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _descriptors = Definitions.Select(definition => new MetricDescriptor(
            new MetricId(definition.Id), definition.DisplayName, definition.SemanticType,
            definition.ValueKind, definition.Description, definition.Unit)).ToArray();
        foreach (var descriptor in _descriptors)
        {
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("GitHub Copilot metric registration failed.");
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("GitHub Copilot provider can only be run once.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            var polling = RunPollingLoopAsync(stop.Token);
            var observing = RunLifecycleObservationLoopAsync(stop.Token);
            _ = await Task.WhenAny(polling, observing).ConfigureAwait(false);
            if (!stop.IsCancellationRequested) stop.Cancel();
            await Task.WhenAll(polling, observing).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            InvalidateForStop();
            _ = await PublishStoppedAsync().ConfigureAwait(false);
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            var delay = GetNextWakeDelay();
            if (delay > MaximumSleepChunk) delay = MaximumSleepChunk;
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunLifecycleObservationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(LifecycleObservationInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(stop.Token).ConfigureAwait(false);

        PendingPublication? publication;
        try
        {
            publication = await PollLockedAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _pollLock.Release();
        }

        if (publication is not null)
            await PublishAsync(publication, stop.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        InvalidateForStop();
    }

    private async Task<PendingPublication?> PollLockedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        bool timedChange;
        bool requestDue;
        long requestAccountGeneration;
        lock (_stateGate)
        {
            timedChange = ObserveClockAndExpire(now, timestamp);
            timedChange |= MarkCacheStale(timestamp);
            requestDue = timestamp >= _nextPollTimestamp;
            requestAccountGeneration = _accountGeneration;
            if (!requestDue)
                return timedChange ? BeginPublicationLocked(now, timestamp, cancellationToken) : null;
        }

        CopilotQuotaReading? reading = null;
        CopilotQuotaAcquisitionException? failure = null;
        try
        {
            reading = await _acquirer.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CopilotQuotaAcquisitionException exception)
        {
            failure = exception;
        }
        catch (Exception)
        {
            failure = new CopilotQuotaAcquisitionException("request_failed");
        }

        cancellationToken.ThrowIfCancellationRequested();
        now = _timeProvider.GetUtcNow();
        timestamp = _timeProvider.GetTimestamp();

        lock (_stateGate)
        {
            if (requestAccountGeneration != _accountGeneration || Volatile.Read(ref _disposed) != 0)
                return null;

            _ = ObserveClockAndExpire(now, timestamp);
            if (reading is not null)
            {
                if (_activeAccountKey is not null &&
                    !StringComparer.Ordinal.Equals(_activeAccountKey, reading.StableAccountKey))
                {
                    _accountGeneration++;
                    _activeAccountKey = reading.StableAccountKey;
                    ClearCacheLocked();
                    _missing.Clear();
                    _emptyState = MetricState.Unavailable;
                    _emptyErrorCode = null;
                    _emptyObservedAt = now;
                    _consecutiveFailures = 0;
                    ScheduleNextPoll(PollInterval, timestamp);
                    return BeginPublicationLocked(now, timestamp, cancellationToken);
                }

                _activeAccountKey = reading.StableAccountKey;
                ReplaceCacheLocked(reading, now, timestamp);
                _emptyState = MetricState.Unavailable;
                _emptyErrorCode = null;
                _emptyObservedAt = now;
                _consecutiveFailures = 0;
                ScheduleNextPoll(PollInterval, timestamp);
            }
            else
            {
                ApplyFailureLocked(failure!, now, timestamp);
            }

            _ = ObserveClockAndExpire(now, timestamp);
            _ = MarkCacheStale(timestamp);
            return BeginPublicationLocked(now, timestamp, cancellationToken);
        }
    }

    private void ApplyFailureLocked(CopilotQuotaAcquisitionException failure,
        DateTimeOffset now, long timestamp)
    {
        var priorAccountKey = _activeAccountKey;
        var sameVerifiedAccount = failure.StableAccountKey is not null && priorAccountKey is not null &&
            StringComparer.Ordinal.Equals(priorAccountKey, failure.StableAccountKey);
        var mayReuseCache = sameVerifiedAccount && failure.ErrorCode is not
            ("auth_required" or "auth_forbidden" or "auth_unavailable" or "identity_mismatch" or
             "identity_unavailable" or "unsupported_host");

        if (!mayReuseCache)
        {
            _cache.Clear();
            _missing.Clear();
            if (failure.StableAccountKey is not null)
            {
                _activeAccountKey = failure.StableAccountKey;
                if (priorAccountKey is not null &&
                    !StringComparer.Ordinal.Equals(priorAccountKey, failure.StableAccountKey))
                    _accountGeneration++;
            }
            else
            {
                _activeAccountKey = null;
                if (priorAccountKey is not null) _accountGeneration++;
            }
        }
        else
        {
            foreach (var id in _cache.Keys.ToArray())
                _cache[id] = _cache[id] with { ForceStale = true, ErrorCode = failure.ErrorCode };
        }

        _emptyState = IsUnavailable(failure.ErrorCode) ? MetricState.Unavailable : MetricState.Error;
        _emptyErrorCode = _emptyState == MetricState.Error ? failure.ErrorCode : null;
        _emptyObservedAt = now;
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 32);

        var retryDelay = GetRetryDelay(failure, now, _consecutiveFailures);
        ScheduleNextPoll(retryDelay > PollInterval ? retryDelay : PollInterval, timestamp);
    }

    private void ReplaceCacheLocked(CopilotQuotaReading reading, DateTimeOffset observedAt, long timestamp)
    {
        _cache.Clear();
        _missing.Clear();
        var values = CreateValues(reading);
        foreach (var pair in values)
        {
            var bucketName = GetBucketName(pair.Key);
            DateTimeOffset? resetAt = bucketName is not null &&
                reading.Buckets.TryGetValue(bucketName, out var bucket) ? bucket.ResetAt : null;
            _cache[pair.Key] = new CachedMetric(pair.Value, observedAt, timestamp, bucketName, resetAt);
        }
    }

    private bool ObserveClockAndExpire(DateTimeOffset utcNow, long timestamp)
    {
        var changed = false;
        if (_lastUtcNow is DateTimeOffset previousUtc)
        {
            var monotonicElapsed = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
            var utcElapsed = utcNow - previousUtc;
            if (utcElapsed < TimeSpan.Zero || Math.Abs((utcElapsed - monotonicElapsed).TotalSeconds) > 2)
            {
                ClearCacheLocked();
                MarkAllMissingLocked(MetricState.Unavailable, "clock_changed", utcNow);
                _activePublicationStop?.Cancel();
                changed = true;
            }
        }

        _lastUtcNow = utcNow;
        _lastTimestamp = timestamp;

        foreach (var bucket in Buckets)
        {
            var bucketCache = _cache.Where(pair => StringComparer.Ordinal.Equals(pair.Value.BucketName, bucket))
                .ToArray();
            if (bucketCache.Length == 0) continue;
            var resetAt = bucketCache.Select(pair => pair.Value.ResetAt).FirstOrDefault(value => value.HasValue);
            if (resetAt is DateTimeOffset reset && utcNow >= reset)
            {
                foreach (var pair in bucketCache) _cache.Remove(pair.Key);
                MarkBucketMissingLocked(bucket, "reset_elapsed", utcNow);
                _activePublicationStop?.Cancel();
                changed = true;
            }
        }

        foreach (var pair in _cache.ToArray())
        {
            if (_timeProvider.GetElapsedTime(pair.Value.Timestamp, timestamp) < MaximumCacheAge) continue;
            _cache.Remove(pair.Key);
            _missing[pair.Key] = new MissingMetric(MetricState.Unavailable, "cache_expired", utcNow);
            _activePublicationStop?.Cancel();
            changed = true;
        }

        return changed;
    }

    private bool MarkCacheStale(long timestamp)
    {
        var changed = false;
        foreach (var pair in _cache.ToArray())
        {
            var cached = pair.Value;
            if (cached.ForceStale || cached.StalePublished ||
                _timeProvider.GetElapsedTime(cached.Timestamp, timestamp) < StaleAfter)
                continue;
            _cache[pair.Key] = cached with { StalePublished = true };
            changed = true;
        }

        return changed;
    }

    private PendingPublication BeginPublicationLocked(DateTimeOffset now, long timestamp,
        CancellationToken cancellationToken)
    {
        _activePublicationStop?.Cancel();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        _activePublicationStop = stop;
        var generation = ++_publicationGeneration;
        var snapshots = BuildSnapshotsLocked(now, timestamp);
        return new PendingPublication(snapshots, stop, generation);
    }

    private MetricSnapshot[] BuildSnapshotsLocked(DateTimeOffset now, long timestamp)
    {
        var snapshots = new MetricSnapshot[_descriptors.Length];
        for (var i = 0; i < _descriptors.Length; i++)
        {
            var descriptor = _descriptors[i];
            if (_cache.TryGetValue(descriptor.Id.Value, out var cached))
            {
                var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
                var state = cached.ForceStale || age >= StaleAfter ? MetricState.Stale : MetricState.Fresh;
                snapshots[i] = new MetricSnapshot(descriptor.Id, cached.Value, cached.ObservedAt, state,
                    MetricConfidence.Measured, source: Source, errorCode: cached.ErrorCode);
            }
            else if (_missing.TryGetValue(descriptor.Id.Value, out var missing))
            {
                snapshots[i] = new MetricSnapshot(descriptor.Id, null, missing.ObservedAt,
                    missing.State, MetricConfidence.Unknown, source: Source, errorCode: missing.ErrorCode);
            }
            else
            {
                snapshots[i] = new MetricSnapshot(descriptor.Id, null, _emptyObservedAt == default ? now : _emptyObservedAt,
                    _emptyState, MetricConfidence.Unknown, source: Source, errorCode: _emptyErrorCode);
            }
        }

        return snapshots;
    }

    private async Task PublishAsync(PendingPublication publication, CancellationToken pollToken)
    {
        try
        {
            foreach (var snapshot in publication.Snapshots)
            {
                lock (_stateGate)
                    if (publication.Generation != _publicationGeneration) return;
                await _bus.PublishAsync(snapshot, publication.Stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when
            (publication.Stop.IsCancellationRequested && !pollToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_activePublicationStop, publication.Stop))
                    _activePublicationStop = null;
            }
            publication.Stop.Dispose();
        }
    }

    private async Task<bool> PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(StopPublishTimeout);
        var observedAt = _timeProvider.GetUtcNow();
        var tasks = _descriptors.Select(descriptor => _bus.PublishAsync(
            new MetricSnapshot(descriptor.Id, null, observedAt, MetricState.Stopped,
                MetricConfidence.Unknown, source: Source), deadline.Token).AsTask()).ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or
                                          ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private void InvalidateForStop()
    {
        lock (_stateGate)
        {
            _accountGeneration++;
            _publicationGeneration++;
            _activeAccountKey = null;
            _activePublicationStop?.Cancel();
            ClearCacheLocked();
            _missing.Clear();
        }
        _shutdown.Cancel();
    }

    private void ClearCacheLocked() => _cache.Clear();

    private void MarkAllMissingLocked(MetricState state, string? errorCode, DateTimeOffset observedAt)
    {
        foreach (var descriptor in _descriptors)
            _missing[descriptor.Id.Value] = new MissingMetric(state, errorCode, observedAt);
    }

    private void MarkBucketMissingLocked(string bucket, string errorCode, DateTimeOffset observedAt)
    {
        var prefix = $"ai.github_copilot.quota.{bucket}.";
        foreach (var descriptor in _descriptors)
            if (descriptor.Id.Value.StartsWith(prefix, StringComparison.Ordinal))
                _missing[descriptor.Id.Value] = new MissingMetric(MetricState.Unavailable, errorCode, observedAt);
    }

    private TimeSpan GetNextWakeDelay()
    {
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        lock (_stateGate)
        {
            var next = RemainingUntil(_nextPollTimestamp, timestamp);
            foreach (var cached in _cache.Values)
            {
                var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
                if (!cached.ForceStale && !cached.StalePublished && age < StaleAfter)
                    next = Min(next, StaleAfter - age);
                if (age < MaximumCacheAge)
                    next = Min(next, MaximumCacheAge - age);
                if (cached.ResetAt is DateTimeOffset resetAt && resetAt > now)
                    next = Min(next, resetAt - now);
            }
            return next < TimeSpan.Zero ? TimeSpan.Zero : next;
        }
    }

    private TimeSpan GetRetryDelay(CopilotQuotaAcquisitionException failure,
        DateTimeOffset now, int consecutiveFailures)
    {
        if (failure.RetryAfterDelay is TimeSpan delay)
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        if (failure.RetryAfterAt is DateTimeOffset retryAt)
            return retryAt <= now ? TimeSpan.Zero : retryAt - now;

        var exponent = Math.Min(consecutiveFailures - 1, 4);
        var seconds = RetryBaseDelay.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, RetryMaximumDelay.TotalSeconds));
    }

    private void ScheduleNextPoll(TimeSpan delay, long timestamp)
    {
        var ticks = delay.TotalSeconds * _timeProvider.TimestampFrequency;
        if (!double.IsFinite(ticks) || ticks >= long.MaxValue - (double)timestamp)
            _nextPollTimestamp = long.MaxValue;
        else
            _nextPollTimestamp = timestamp + Math.Max(0, (long)Math.Ceiling(ticks));
    }

    private TimeSpan RemainingUntil(long deadline, long timestamp)
    {
        if (deadline <= timestamp) return TimeSpan.Zero;
        var seconds = ((double)deadline - timestamp) / _timeProvider.TimestampFrequency;
        return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static string? GetBucketName(string metricId)
    {
        foreach (var bucket in Buckets)
            if (metricId.StartsWith($"ai.github_copilot.quota.{bucket}.", StringComparison.Ordinal))
                return bucket;
        return null;
    }

    private static Dictionary<string, MetricValue> CreateValues(CopilotQuotaReading reading)
    {
        var values = new Dictionary<string, MetricValue>(StringComparer.Ordinal)
        {
            ["ai.github_copilot.billing.token_based"] = MetricValue.From(reading.TokenBasedBilling)
        };

        foreach (var bucketName in Buckets)
        {
            if (!reading.Buckets.TryGetValue(bucketName, out var bucket)) continue;
            var prefix = $"ai.github_copilot.quota.{bucketName}.";
            if (bucket.PercentRemaining is double percent)
                values[prefix + "percent_remaining"] = MetricValue.From(percent);
            if (bucket.ResetAt is DateTimeOffset resetAt)
                values[prefix + "reset_at"] = MetricValue.From(resetAt);
            values[prefix + "unlimited"] = MetricValue.From(bucket.Unlimited);
            if (bucket.HasQuota is bool hasQuota)
                values[prefix + "has_quota"] = MetricValue.From(hasQuota);
        }

        return values;
    }

    private static bool IsUnavailable(string errorCode) => errorCode is
        "auth_unavailable" or "auth_required" or "auth_forbidden" or "gh_unavailable" or
        "unsupported_host" or "identity_mismatch" or "identity_unavailable";

    private static MetricDefinition[] CreateDefinitions()
    {
        // Per-bucket amounts remain parser-only until their units are documented by a first-party source.
        var definitions = new List<MetricDefinition>
        {
            new("ai.github_copilot.billing.token_based", "Copilot token-based billing", "billing_mode",
                MetricValueKind.Boolean, "Whether the source reports token-based billing.", null)
        };

        foreach (var bucket in Buckets)
        {
            var prefix = $"ai.github_copilot.quota.{bucket}.";
            var label = bucket.Replace('_', ' ');
            definitions.Add(new MetricDefinition(prefix + "percent_remaining", $"Copilot {label} quota remaining",
                "quota_percent_remaining", MetricValueKind.Double,
                "Direct source-reported percentage; no quota amount is inferred.", "%"));
            definitions.Add(new MetricDefinition(prefix + "reset_at", $"Copilot {label} quota reset",
                "timestamp", MetricValueKind.DateTimeOffset,
                "Direct source-reported per-bucket reset time.", null));
            definitions.Add(new MetricDefinition(prefix + "unlimited", $"Copilot {label} unlimited",
                "quota_unlimited", MetricValueKind.Boolean, "Direct source-reported unlimited flag.", null));
            definitions.Add(new MetricDefinition(prefix + "has_quota", $"Copilot {label} raw has-quota source flag",
                "quota_source_flag", MetricValueKind.Boolean,
                "Raw source flag, not an admission decision and not authoritative under token-based billing.", null));
        }

        return definitions.ToArray();
    }

    private sealed record CachedMetric(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp,
        string? BucketName, DateTimeOffset? ResetAt, bool ForceStale = false,
        bool StalePublished = false, string? ErrorCode = null);

    private sealed record MissingMetric(MetricState State, string? ErrorCode, DateTimeOffset ObservedAt);

    private sealed record PendingPublication(MetricSnapshot[] Snapshots,
        CancellationTokenSource Stop, long Generation);

    private sealed record MetricDefinition(string Id, string DisplayName, string SemanticType,
        MetricValueKind ValueKind, string? Description, string? Unit);
}
