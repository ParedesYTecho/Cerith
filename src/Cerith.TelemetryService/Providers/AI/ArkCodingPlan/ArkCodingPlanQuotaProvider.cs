#nullable enable

using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal sealed class ArkCodingPlanQuotaProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan IdentityCheckInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan StateCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StoppedPublishTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkerDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClockDiscrepancyTolerance = TimeSpan.FromSeconds(2);

    private static readonly (string Id, string Name, string SemanticType, MetricValueKind Kind, string? Unit)[] Definitions =
    [
        ("ai.ark-coding-plan.quota.session.used", "Session Usage", "quota_used", MetricValueKind.Double, "%"),
        ("ai.ark-coding-plan.quota.weekly.used", "Weekly Usage", "quota_used", MetricValueKind.Double, "%"),
        ("ai.ark-coding-plan.quota.monthly.used", "Monthly Usage", "quota_used", MetricValueKind.Double, "%"),
        ("ai.ark-coding-plan.quota.session.reset", "Session Reset At", "timestamp", MetricValueKind.DateTimeOffset, null),
        ("ai.ark-coding-plan.quota.weekly.reset", "Weekly Reset At", "timestamp", MetricValueKind.DateTimeOffset, null),
        ("ai.ark-coding-plan.quota.monthly.reset", "Monthly Reset At", "timestamp", MetricValueKind.DateTimeOffset, null)
    ];

    private readonly object _stateLock = new();
    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly ArkCodingPlanQuotaAcquirer _acquirer;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, int> _jitter;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _acquisitionGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly ArkCodingPlanQuotaPoint[] _points = new ArkCodingPlanQuotaPoint[ArkCodingPlanQuotaParser.PointCount];
    private readonly CachedPoint?[] _cache = new CachedPoint?[ArkCodingPlanQuotaParser.PointCount];
    private readonly ResetDeadline?[] _resetDeadlines = new ResetDeadline?[3];
    private readonly DateTimeOffset[] _pointObservedAt = new DateTimeOffset[ArkCodingPlanQuotaParser.PointCount];
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _ownerFingerprint;
    private long _ownerGeneration;
    private long _snapshotGeneration;
    private long _nextPollTimestamp = long.MinValue;
    private long _retryNotBeforeTimestamp = long.MinValue;
    private int _failureCount;
    private DateTimeOffset? _lastUtc;
    private long _lastTimestamp;
    private DateTimeOffset _trustedUtc;
    private long _trustedTimestamp;
    private bool _runActive;
    private bool _runStopped;
    private bool _disposed;

    public ArkCodingPlanQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new ArkCodingPlanQuotaAcquirer(), TimeProvider.System)
    {
    }

    internal ArkCodingPlanQuotaProvider(MetricRegistry registry, MetricBus bus,
        ArkCodingPlanQuotaAcquirer acquirer, TimeProvider timeProvider, Func<int, int>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitter = jitter ?? (static maximum => Random.Shared.Next(maximum + 1));
        if (_timeProvider.TimestampFrequency <= 0)
            throw new ArgumentException("Time provider must expose a positive timestamp frequency.", nameof(timeProvider));

        _descriptors = new MetricDescriptor[Definitions.Length];
        for (var i = 0; i < Definitions.Length; i++)
        {
            var definition = Definitions[i];
            var descriptor = new MetricDescriptor(new MetricId(definition.Id), $"Ark Coding Plan {definition.Name}",
                definition.SemanticType, definition.Kind, unit: definition.Unit);
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("Ark Coding Plan metric registration failed.");
            _descriptors[i] = descriptor;
        }

        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        _lastUtc = now;
        _lastTimestamp = timestamp;
        _trustedUtc = now;
        _trustedTimestamp = timestamp;
        for (var i = 0; i < _points.Length; i++)
        {
            _points[i] = ArkCodingPlanQuotaPoint.Unavailable();
            _pointObservedAt[i] = now;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            if (_runActive) throw new InvalidOperationException("Ark Coding Plan quota provider is already running.");
            if (_runStopped) throw new InvalidOperationException("Ark Coding Plan quota provider has stopped.");
            _runActive = true;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers =
        [
            PollLoopAsync(stop.Token),
            PublishLoopAsync(stop.Token),
            ObserveIdentityLoopAsync(stop.Token),
            MaintainStateLoopAsync(stop.Token)
        ];
        Exception? failure = null;

        try
        {
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (!stop.IsCancellationRequested)
                failure = new InvalidOperationException("Ark Coding Plan quota worker stopped unexpectedly.");
        }
        catch (OperationCanceledException exception) when (stop.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
                failure = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            stop.Cancel();
            CancelActiveOperations();
            try
            {
                await Task.WhenAll(workers).WaitAsync(WorkerDrainTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (stop.IsCancellationRequested)
            {
                if (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
                    failure ??= exception;
            }
            catch (TimeoutException)
            {
                ObserveDetachedWorkerFaults(workers);
                failure ??= new TimeoutException("Ark Coding Plan quota workers did not drain before shutdown.");
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            MarkStopped();
            try
            {
                await PublishStoppedAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                lock (_stateLock) _runActive = false;
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _acquisitionGate.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? ownerStop = null;
        long expectedGeneration = 0;
        var hasObservedOwner = false;

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var startedAt = _timeProvider.GetUtcNow();
            var startedTimestamp = _timeProvider.GetTimestamp();
            lock (_stateLock)
            {
                if (_runStopped || _disposed) return;
                ObserveClockLocked(startedAt, startedTimestamp);
                ExpireCachesLocked(startedAt, startedTimestamp);
                var dueTimestamp = Math.Max(_nextPollTimestamp, _retryNotBeforeTimestamp);
                if (startedTimestamp < dueTimestamp) return;
                _nextPollTimestamp = AddTimestamp(startedTimestamp, PollInterval);
                ownerStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = ownerStop;
            }

            try
            {
                var reading = await _acquirer.AcquireAsync(startedAt, fingerprint =>
                {
                    expectedGeneration = ObserveOwnerFingerprint(fingerprint, ownerStop);
                    hasObservedOwner = true;
                }, ownerStop.Token).ConfigureAwait(false);

                admission.Token.ThrowIfCancellationRequested();
                if (ownerStop.IsCancellationRequested) return;
                var receivedAt = _timeProvider.GetUtcNow();
                var receivedTimestamp = _timeProvider.GetTimestamp();
                lock (_stateLock)
                {
                    if (_runStopped || _disposed || !hasObservedOwner || _ownerGeneration != expectedGeneration ||
                        !StringComparer.Ordinal.Equals(_ownerFingerprint, reading.OwnerFingerprint))
                        return;
                    ObserveClockLocked(receivedAt, receivedTimestamp);
                    ApplyReadingLocked(reading, receivedAt, receivedTimestamp, expectedGeneration);
                }
            }
            catch (OperationCanceledException) when (admission.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (ownerStop.IsCancellationRequested)
            {
                return;
            }
            catch (ArkCodingPlanAcquisitionException exception)
            {
                var failedAt = _timeProvider.GetUtcNow();
                var failedTimestamp = _timeProvider.GetTimestamp();
                lock (_stateLock)
                {
                    if (_runStopped || _disposed) return;
                    ObserveClockLocked(failedAt, failedTimestamp);
                    if (IsOwnerFailure(exception.ErrorCode))
                        InvalidateOwnerLocked(exception.ErrorCode, failedAt);
                    else
                        ApplyFailureLocked(exception.ErrorCode, failedAt, failedTimestamp,
                            hasObservedOwner ? expectedGeneration : _ownerGeneration);
                }
            }
        }
        finally
        {
            if (ownerStop is not null)
            {
                lock (_stateLock)
                    if (ReferenceEquals(_activeRequestStop, ownerStop)) _activeRequestStop = null;
                ownerStop.Dispose();
            }
            _acquisitionGate.Release();
        }
    }

    internal async Task ObserveIdentityOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _acquisitionGate.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? observerStop = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateLock)
            {
                if (_runStopped || _disposed) return;
                observerStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = observerStop;
            }

            await _acquirer.ObserveIdentityAsync(fingerprint => ObserveOwnerFingerprint(fingerprint, observerStop),
                observerStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (admission.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (observerStop?.IsCancellationRequested == true)
        {
            return;
        }
        catch (ArkCodingPlanAcquisitionException exception)
        {
            var now = _timeProvider.GetUtcNow();
            lock (_stateLock)
            {
                if (!_runStopped && !_disposed)
                    InvalidateOwnerLocked(exception.ErrorCode, now);
            }
        }
        finally
        {
            if (observerStop is not null)
            {
                lock (_stateLock)
                    if (ReferenceEquals(_activeRequestStop, observerStop)) _activeRequestStop = null;
                observerStop.Dispose();
            }
            _acquisitionGate.Release();
        }
    }

    internal ValueTask MaintainStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        lock (_stateLock)
        {
            if (!_runStopped && !_disposed)
            {
                ObserveClockLocked(now, timestamp);
                ExpireCachesLocked(now, timestamp);
            }
        }
        return ValueTask.CompletedTask;
    }

    internal async ValueTask PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await MaintainStateAsync(admission.Token).ConfigureAwait(false);
        await PublishSnapshotsAsync(admission.Token).ConfigureAwait(false);
    }

    private long ObserveOwnerFingerprint(string fingerprint, CancellationTokenSource? observingRequest)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            if (_disposed || _runStopped) return _ownerGeneration;
            ObserveClockLocked(now, _timeProvider.GetTimestamp());
            if (!StringComparer.Ordinal.Equals(_ownerFingerprint, fingerprint))
            {
                _ownerFingerprint = fingerprint;
                _ownerGeneration++;
                ClearCachesLocked();
                _nextPollTimestamp = long.MinValue;
                _retryNotBeforeTimestamp = long.MinValue;
                _failureCount = 0;
                var point = ArkCodingPlanQuotaPoint.Unavailable("owner_changed");
                for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, point, now);
                _snapshotGeneration++;
                CancelActivePublicationLocked();
                if (!ReferenceEquals(_activeRequestStop, observingRequest)) _activeRequestStop?.Cancel();
            }
            return _ownerGeneration;
        }
    }

    private void ApplyReadingLocked(ArkCodingPlanQuotaReading reading, DateTimeOffset observedAt,
        long timestamp, long expectedGeneration)
    {
        if (_runStopped || _disposed || _ownerGeneration != expectedGeneration ||
            !StringComparer.Ordinal.Equals(_ownerFingerprint, reading.OwnerFingerprint))
            return;

        for (var bucket = 0; bucket < 3; bucket++)
        {
            var reset = reading.Points[bucket + 3];
            if (StringComparer.Ordinal.Equals(reset.ErrorCode, "reset_elapsed"))
            {
                InvalidateBucketLocked(bucket, observedAt, "reset_elapsed");
                continue;
            }

            DateTimeOffset? resetAt = reset.Value is MetricValue resetValue && reset.State == MetricState.Fresh
                ? resetValue.AsDateTimeOffset()
                : null;
            if (resetAt is DateTimeOffset sourceResetAt)
            {
                var currentDeadline = _resetDeadlines[bucket];
                var resetTimestamp = currentDeadline is ResetDeadline known &&
                                     known.OwnerGeneration == expectedGeneration && known.ResetAt == sourceResetAt
                    ? known.Timestamp
                    : AddTimestamp(timestamp, sourceResetAt - TrustedUtcAt(timestamp));
                _resetDeadlines[bucket] = new ResetDeadline(sourceResetAt, resetTimestamp, expectedGeneration);
            }

            var deadline = _resetDeadlines[bucket] is ResetDeadline ownerDeadline &&
                           ownerDeadline.OwnerGeneration == expectedGeneration
                ? ownerDeadline
                : (ResetDeadline?)null;

            for (var offset = 0; offset < 2; offset++)
            {
                var index = bucket + offset * 3;
                var point = reading.Points[index];
                _cache[index] = point.State == MetricState.Fresh && point.Value is MetricValue metricValue
                    ? new CachedPoint(metricValue, observedAt, timestamp)
                    : null;
                UpdatePointLocked(index, point, observedAt);
            }

            if (deadline is ResetDeadline hardExpiry &&
                (hardExpiry.ResetAt <= observedAt || timestamp >= hardExpiry.Timestamp))
                InvalidateBucketLocked(bucket, observedAt, "reset_elapsed");
        }

        ExpireCachesLocked(observedAt, timestamp);
        _retryNotBeforeTimestamp = long.MinValue;
        _failureCount = 0;
    }

    private void ApplyFailureLocked(string errorCode, DateTimeOffset observedAt, long timestamp,
        long expectedGeneration)
    {
        if (_runStopped || _disposed || _ownerGeneration != expectedGeneration) return;
        ExpireCachesLocked(observedAt, timestamp);
        for (var i = 0; i < _points.Length; i++)
        {
            var cached = _cache[i];
            var point = cached is not null
                ? new ArkCodingPlanQuotaPoint(MetricState.Stale, cached.Value, errorCode)
                : ArkCodingPlanQuotaPoint.Error(errorCode);
            UpdatePointLocked(i, point, cached?.ObservedAt ?? observedAt);
        }

        _failureCount = Math.Min(_failureCount + 1, 5);
        var seconds = _failureCount switch
        {
            1 => 60,
            2 => 120,
            3 => 240,
            4 => 480,
            _ => 900
        };
        var jitterMaximum = Math.Min(30, seconds / 10);
        var jitter = Math.Clamp(_jitter(jitterMaximum), 0, jitterMaximum);
        _retryNotBeforeTimestamp = AddTimestamp(timestamp, TimeSpan.FromSeconds(Math.Min(900, seconds + jitter)));
    }

    private void InvalidateOwnerLocked(string errorCode, DateTimeOffset observedAt)
    {
        _ownerFingerprint = null;
        _ownerGeneration++;
        ClearCachesLocked();
        _nextPollTimestamp = long.MinValue;
        _retryNotBeforeTimestamp = long.MinValue;
        _failureCount = 0;
        var point = ArkCodingPlanQuotaPoint.Error(errorCode);
        for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, point, observedAt);
        _snapshotGeneration++;
        CancelActivePublicationLocked();
    }

    private static bool IsOwnerFailure(string errorCode) => errorCode is
        "auth_required" or "refresh_owner_unsupported" or "identity_invalid" or "identity_changed" or
        "profile_unresolved";

    private void ObserveClockLocked(DateTimeOffset now, long timestamp)
    {
        if (_lastUtc is DateTimeOffset previousUtc)
        {
            var wallElapsed = now - previousUtc;
            var monotonicElapsed = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
            if (wallElapsed < TimeSpan.Zero || monotonicElapsed < TimeSpan.Zero ||
                Math.Abs((wallElapsed - monotonicElapsed).TotalSeconds) > ClockDiscrepancyTolerance.TotalSeconds)
            {
                ClearCachesLocked(clearResetDeadlines: false);
                var point = ArkCodingPlanQuotaPoint.Unavailable("clock_changed");
                for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, point, now);
                _snapshotGeneration++;
                CancelActivePublicationLocked();
            }
        }

        _lastUtc = now;
        _lastTimestamp = timestamp;
    }

    private DateTimeOffset TrustedUtcAt(long timestamp)
    {
        var elapsed = _timeProvider.GetElapsedTime(_trustedTimestamp, timestamp);
        return elapsed < TimeSpan.Zero ? _trustedUtc : _trustedUtc + elapsed;
    }

    private void ExpireCachesLocked(DateTimeOffset now, long timestamp)
    {
        for (var bucket = 0; bucket < 3; bucket++)
        {
            var deadline = _resetDeadlines[bucket];
            if (deadline is ResetDeadline hardExpiry &&
                (hardExpiry.OwnerGeneration != _ownerGeneration || hardExpiry.ResetAt <= now ||
                 timestamp >= hardExpiry.Timestamp))
            {
                InvalidateBucketLocked(bucket, now, "reset_elapsed");
                continue;
            }

            ExpireCachePointLocked(bucket, now, timestamp);
            ExpireCachePointLocked(bucket + 3, now, timestamp);
        }
    }

    private void ExpireCachePointLocked(int index, DateTimeOffset now, long timestamp)
    {
        var cached = _cache[index];
        if (cached is null) return;

        var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
        if (age < TimeSpan.Zero)
        {
            ClearCachesLocked(clearResetDeadlines: false);
            var point = ArkCodingPlanQuotaPoint.Unavailable("clock_changed");
            for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, point, now);
            _snapshotGeneration++;
            CancelActivePublicationLocked();
            return;
        }

        if (age >= MaximumCacheAge)
        {
            _cache[index] = null;
            UpdatePointLocked(index, ArkCodingPlanQuotaPoint.Unavailable("cache_expired"), now);
        }
        else if (age >= StaleAfter && _points[index].State == MetricState.Fresh)
        {
            UpdatePointLocked(index,
                new ArkCodingPlanQuotaPoint(MetricState.Stale, cached.Value, null), cached.ObservedAt);
        }
    }

    private void InvalidateBucketLocked(int bucket, DateTimeOffset now, string errorCode)
    {
        var expired = ArkCodingPlanQuotaPoint.Unavailable(errorCode);
        _resetDeadlines[bucket] = null;
        _cache[bucket] = null;
        _cache[bucket + 3] = null;
        UpdatePointLocked(bucket, expired, now);
        UpdatePointLocked(bucket + 3, expired, now);
    }

    private void UpdatePointLocked(int index, ArkCodingPlanQuotaPoint point, DateTimeOffset observedAt)
    {
        if (_points[index].Equals(point) && _pointObservedAt[index] == observedAt) return;
        _points[index] = point;
        _pointObservedAt[index] = observedAt;
        _snapshotGeneration++;
        CancelActivePublicationLocked();
    }

    private void ClearCachesLocked(bool clearResetDeadlines = true)
    {
        Array.Clear(_cache);
        if (clearResetDeadlines) Array.Clear(_resetDeadlines);
    }

    private long AddTimestamp(long timestamp, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return timestamp;
        var delta = delay.TotalSeconds * _timeProvider.TimestampFrequency;
        if (!double.IsFinite(delta) || delta >= long.MaxValue) return long.MaxValue;
        var ticks = Math.Max(1L, (long)Math.Ceiling(delta));
        return timestamp > long.MaxValue - ticks ? long.MaxValue : timestamp + ticks;
    }

    private void CancelActivePublicationLocked() => _activePublishStop?.Cancel();

    private void CancelActiveOperations()
    {
        lock (_stateLock)
        {
            _activeRequestStop?.Cancel();
            CancelActivePublicationLocked();
        }
    }

    private async Task PublishSnapshotsAsync(CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? publication = null;
        try
        {
            publication = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            MetricSnapshot[] snapshots;
            long version;
            lock (_stateLock)
            {
                if (!_runStopped && !_disposed)
                {
                    var now = _timeProvider.GetUtcNow();
                    var timestamp = _timeProvider.GetTimestamp();
                    ObserveClockLocked(now, timestamp);
                    ExpireCachesLocked(now, timestamp);
                }
                _activePublishStop = publication;
                snapshots = CreateSnapshotsLocked();
                version = _snapshotGeneration;
            }

            foreach (var snapshot in snapshots)
            {
                lock (_stateLock)
                    if (version != _snapshotGeneration) publication.Cancel();
                publication.Token.ThrowIfCancellationRequested();
                await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
                publication.Token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (publication is not null)
            {
                lock (_stateLock)
                    if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null;
                publication.Dispose();
            }
            _publishGate.Release();
        }
    }

    private MetricSnapshot[] CreateSnapshotsLocked()
    {
        var snapshots = new MetricSnapshot[_descriptors.Length];
        for (var i = 0; i < snapshots.Length; i++)
        {
            var point = _points[i];
            snapshots[i] = new MetricSnapshot(_descriptors[i].Id, point.Value, _pointObservedAt[i], point.State,
                point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                source: "ArkCodingPlan.Usage.Plan.ReadOnly.v1", errorCode: point.ErrorCode);
        }
        return snapshots;
    }

    private void MarkStopped()
    {
        lock (_stateLock)
        {
            if (!_runStopped)
            {
                _runStopped = true;
                _ownerGeneration++;
                _ownerFingerprint = null;
                _nextPollTimestamp = long.MinValue;
                _retryNotBeforeTimestamp = long.MinValue;
                _failureCount = 0;
                ClearCachesLocked();
                var now = _timeProvider.GetUtcNow();
                var stopped = new ArkCodingPlanQuotaPoint(MetricState.Stopped, null, null);
                for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, stopped, now);
                _snapshotGeneration++;
            }
            _activeRequestStop?.Cancel();
            CancelActivePublicationLocked();
        }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(StoppedPublishTimeout);
        try
        {
            await PublishSnapshotsAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        await PublishIfCurrentAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(PublishInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PublishIfCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishIfCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveIdentityLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(IdentityCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ObserveIdentityOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MaintainStateLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(StateCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await MaintainStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ObserveDetachedWorkerFaults(Task[] workers)
    {
        foreach (var worker in workers)
            _ = worker.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        CancelActiveOperations();
        _acquirer.Dispose();
        _shutdown.Dispose();
    }

    private sealed record CachedPoint(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp);
    private sealed record ResetDeadline(DateTimeOffset ResetAt, long Timestamp, long OwnerGeneration);
}
