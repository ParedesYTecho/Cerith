#nullable enable

using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Kiro;

internal sealed class KiroQuotaProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StoppedPublishTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WorkerDrainTimeout = TimeSpan.FromSeconds(1);
    private const string Source = "Kiro.Usage.ACP.v3";
    private const int IncludedRemainingIndex = 0;
    private const int IncludedResetIndex = 1;
    private const int AddOnRemainingIndex = 2;
    private const int BonusRemainingIndex = 3;

    private static readonly string[] MetricIds =
    [
        "ai.kiro.credits.included.remaining",
        "ai.kiro.credits.included.reset",
        "ai.kiro.credits.addon.remaining",
        "ai.kiro.credits.bonus.remaining"
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly Func<CancellationToken, Task<KiroAcquisition>> _readUsage;
    private readonly Func<CancellationToken, Task<KiroIdentityObservation>> _readIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _maximumCacheAge;
    private readonly byte[] _identityKey = RandomNumberGenerator.GetBytes(32);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishSignal = new(0, 1);
    private readonly SemaphoreSlim _validitySignal = new(0, 1);
    private readonly object _gate = new();
    private readonly CachedQuota?[] _cache = new CachedQuota?[MetricIds.Length];
    private readonly KiroMetricPoint[] _fallback = new KiroMetricPoint[MetricIds.Length];
    private byte[]? _identityFingerprint;
    private byte[]? _identityMonitorFingerprint;
    private KiroIdentityStatus? _identityStatus;
    private CancellationTokenSource? _activeAcquisitionStop;
    private CancellationTokenSource? _activePublicationStop;
    private string? _lastFailureCode;
    private DateTimeOffset _lastAttemptAt;
    private long _stateVersion;
    private long _cacheVersion;
    private bool _acquisitionSuppressed;
    private bool _disposed;

    public KiroQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, cancellationToken => KiroAcpUsageClient.ReadAsync(TimeProvider.System,
            cancellationToken), TimeProvider.System)
    {
    }

    internal KiroQuotaProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<KiroAcquisition>> readUsage, TimeProvider timeProvider,
        Func<CancellationToken, Task<KiroIdentityObservation>>? readIdentity = null,
        TimeSpan? pollInterval = null, TimeSpan? staleAfter = null, TimeSpan? maximumCacheAge = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _readUsage = readUsage ?? throw new ArgumentNullException(nameof(readUsage));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pollInterval = pollInterval ?? PollInterval;
        _staleAfter = staleAfter ?? StaleAfter;
        _maximumCacheAge = maximumCacheAge ?? MaximumCacheAge;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_pollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_staleAfter, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(_maximumCacheAge, _staleAfter);
        _readIdentity = readIdentity ?? (cancellationToken => KiroAcpUsageClient.ReadIdentityAsync(
            _timeProvider, _identityKey, cancellationToken));

        var now = _timeProvider.GetUtcNow();
        _lastAttemptAt = now;
        _descriptors =
        [
            new MetricDescriptor(new MetricId(MetricIds[IncludedRemainingIndex]), "Kiro Included Credits Remaining",
                "quota_remaining", MetricValueKind.Decimal, unit: "credits"),
            new MetricDescriptor(new MetricId(MetricIds[IncludedResetIndex]), "Kiro Included Credits Reset",
                "timestamp", MetricValueKind.DateTimeOffset),
            new MetricDescriptor(new MetricId(MetricIds[AddOnRemainingIndex]), "Kiro Add-on Credits Remaining",
                "quota_remaining", MetricValueKind.Decimal, unit: "credits"),
            new MetricDescriptor(new MetricId(MetricIds[BonusRemainingIndex]), "Kiro Bonus Credits Remaining",
                "quota_remaining", MetricValueKind.Decimal, unit: "credits")
        ];

        for (var index = 0; index < _descriptors.Length; index++)
        {
            if (!registry.TryRegister(_descriptors[index]))
                throw new InvalidOperationException("Kiro quota metric registration failed.");
            _fallback[index] = KiroMetricPoint.Unavailable();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers = [PollLoopAsync(stop.Token), PublishLoopAsync(stop.Token), ValidityLoopAsync(stop.Token)];
        SignalPublish();
        Exception? failure = null;

        try
        {
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            if (completed.IsCanceled && !stop.IsCancellationRequested)
                failure = new TaskCanceledException("A Kiro provider worker stopped unexpectedly.");
        }
        finally
        {
            try { stop.Cancel(); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
            try { CancelActiveOperations(); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
            try { failure = CombineFailures(failure, await ObserveWorkersAsync(workers).ConfigureAwait(false)); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
            try { await PublishStoppedAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            KiroIdentityObservation identity;
            lock (_gate) _activeAcquisitionStop = admission;
            identity = await ReadIdentityObservationAsync(admission.Token).ConfigureAwait(false);
            var ownerBefore = identity.Status == KiroIdentityStatus.Ready && identity.Fingerprint is { Length: 32 }
                ? (byte[])identity.Fingerprint.Clone()
                : null;
            ApplyIdentityObservation(identity);
            try
            {
                if (ownerBefore is null) return;

                lock (_gate)
                {
                    if (_acquisitionSuppressed) return;
                }

                KiroAcquisition acquisition;
                byte[]? payload = null;
                try
                {
                    acquisition = await _readUsage(admission.Token).ConfigureAwait(false);
                    payload = acquisition.Payload;
                    admission.Token.ThrowIfCancellationRequested();

                    var ownerAfter = await ReadIdentityObservationAsync(admission.Token).ConfigureAwait(false);
                    var sameOwner = ownerAfter.Status == KiroIdentityStatus.Ready &&
                        ownerAfter.Fingerprint is { Length: 32 } &&
                        CryptographicOperations.FixedTimeEquals(ownerBefore, ownerAfter.Fingerprint);
                    ApplyIdentityObservation(ownerAfter);

                    if (acquisition.Status == KiroAcquisitionStatus.RetrySuppressed)
                    {
                        ApplyAcquisitionSuppression(_timeProvider.GetUtcNow());
                        return;
                    }
                    if (!sameOwner) return;

                    if (acquisition.Status == KiroAcquisitionStatus.Ready && payload is not null)
                    {
                        var observedAt = _timeProvider.GetUtcNow();
                        var parsed = KiroQuotaParser.Parse(payload, _identityKey, observedAt);
                        if (parsed.Reading is null)
                            ApplyFailure(MetricState.Error, parsed.ErrorCode ?? "schema_invalid", observedAt);
                        else
                            ApplyReading(parsed.Reading);
                    }
                    else
                    {
                        var state = acquisition.Status is KiroAcquisitionStatus.Unavailable or KiroAcquisitionStatus.Unsupported
                            ? MetricState.Unavailable
                            : MetricState.Error;
                        ApplyFailure(state, SafeErrorCode(acquisition.ErrorCode), _timeProvider.GetUtcNow());
                    }
                }
                finally { if (payload is not null) CryptographicOperations.ZeroMemory(payload); }
            }
            finally { if (ownerBefore is not null) CryptographicOperations.ZeroMemory(ownerBefore); }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeAcquisitionStop, admission)) _activeAcquisitionStop = null;
            }
            _pollLock.Release();
        }
    }

    internal Task PublishOnceAsync(CancellationToken cancellationToken = default) =>
        PublishCurrentAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        CancelActiveOperations();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            var delay = GetNextPollDelay();
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ValidityLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var transition = GetNextCacheTransition();
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var changed = _validitySignal.WaitAsync(wake.Token);
            var delay = transition.Delay is TimeSpan dueIn
                ? Task.Delay(dueIn, _timeProvider, wake.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, wake.Token);
            var completed = await Task.WhenAny(changed, delay).ConfigureAwait(false);
            if (ReferenceEquals(completed, changed))
            {
                await changed.ConfigureAwait(false);
                wake.Cancel();
                await ObserveCancellationAsync(delay).ConfigureAwait(false);
                continue;
            }

            await delay.ConfigureAwait(false);
            wake.Cancel();
            await ObserveCancellationAsync(changed).ConfigureAwait(false);
            if (transition.Delay.HasValue) RefreshCachedState(transition.CacheVersion);
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
    }

    private static async Task<Exception?> ObserveWorkersAsync(Task[] workers)
    {
        var allWorkers = Task.WhenAll(workers);
        try { await allWorkers.WaitAsync(WorkerDrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException timeout)
        {
            ObserveEventually(allWorkers);
            foreach (var worker in workers) ObserveEventually(worker);
            return CombineFailures(timeout, GetWorkerFailures(workers));
        }
        catch (Exception)
        {
            return GetWorkerFailures(workers);
        }

        return GetWorkerFailures(workers);
    }

    private static Exception? GetWorkerFailures(Task[] workers)
    {
        var failures = workers.Where(static worker => worker.IsFaulted)
            .SelectMany(static worker => worker.Exception!.Flatten().InnerExceptions).ToArray();
        return failures.Length switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };
    }

    private static void ObserveEventually(Task task)
    {
        _ = task.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Exception? CombineFailures(Exception? first, Exception? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return new AggregateException(first, second);
    }

    private async Task<KiroIdentityObservation> ReadIdentityObservationAsync(CancellationToken cancellationToken)
    {
        var observation = await _readIdentity(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return observation;
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _publishSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishCurrentAsync(CancellationToken cancellationToken)
    {
        MetricSnapshot[] snapshots;
        long version;
        CancellationTokenSource publicationStop;
        lock (_gate)
        {
            var timestamp = _timeProvider.GetTimestamp();
            snapshots = CreateSnapshotsLocked(timestamp);
            version = _stateVersion;
            publicationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activePublicationStop = publicationStop;
        }

        try
        {
            foreach (var snapshot in snapshots)
            {
                await _bus.PublishAsync(snapshot, publicationStop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (version != _stateVersion) return;
                }
            }
        }
        catch (OperationCanceledException) when (publicationStop.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activePublicationStop, publicationStop)) _activePublicationStop = null;
            }
            publicationStop.Dispose();
        }
    }

    private void ApplyReading(KiroUsageReading reading)
    {
        CancellationTokenSource? cancelPublication;
        lock (_gate)
        {
            var identityChanged = _identityFingerprint is null ||
                !CryptographicOperations.FixedTimeEquals(_identityFingerprint, reading.IdentityFingerprint);
            if (identityChanged)
            {
                if (_identityFingerprint is not null) CryptographicOperations.ZeroMemory(_identityFingerprint);
                _identityFingerprint = (byte[])reading.IdentityFingerprint.Clone();
                Array.Clear(_cache);
                _lastFailureCode = null;
            }

            var points = new[]
            {
                reading.IncludedRemaining,
                reading.IncludedReset,
                reading.AddOnRemaining,
                reading.BonusRemaining
            };
            DateTimeOffset? planResetAt = reading.IncludedReset.State == MetricState.Fresh &&
                reading.IncludedReset.Value is MetricValue resetValue
                ? resetValue.AsDateTimeOffset()
                : null;

            for (var index = 0; index < points.Length; index++)
            {
                var point = points[index];
                _fallback[index] = point;
                if (point.State != MetricState.Fresh || point.Value is not MetricValue value)
                {
                    _cache[index] = null;
                    continue;
                }

                TimeSpan? validFor = (index == IncludedRemainingIndex || index == IncludedResetIndex) &&
                    planResetAt.HasValue
                    ? planResetAt.Value - reading.ObservedAt
                    : null;
                var allocations = index == BonusRemainingIndex ? reading.BonusAllocations : null;
                if (index == BonusRemainingIndex && allocations is null)
                {
                    _fallback[index] = KiroMetricPoint.Error("bonus_schema_invalid");
                    _cache[index] = null;
                    continue;
                }

                _cache[index] = new CachedQuota(value, reading.ObservedAt, _timeProvider.GetTimestamp(),
                    validFor, allocations);
            }

            _lastAttemptAt = reading.ObservedAt;
            _lastFailureCode = null;
            _stateVersion++;
            _cacheVersion++;
            cancelPublication = _activePublicationStop;
        }

        CryptographicOperations.ZeroMemory(reading.IdentityFingerprint);
        TryCancel(cancelPublication);
        SignalValidityChanged();
        SignalPublish();
    }

    private void ApplyIdentityObservation(KiroIdentityObservation observation)
    {
        var fingerprint = observation.Fingerprint;
        CancellationTokenSource? cancelPublication = null;
        var stateChanged = false;
        var cacheInvalidated = false;
        lock (_gate)
        {
            var ready = observation.Status == KiroIdentityStatus.Ready && fingerprint is { Length: 32 };
            var observedStatus = ready ? KiroIdentityStatus.Ready :
                observation.Status == KiroIdentityStatus.Ready ? KiroIdentityStatus.Unavailable : observation.Status;
            var hasOwnedState = _identityFingerprint is not null || HasCachedValuesLocked();

            if (!ready)
            {
                var errorCode = _acquisitionSuppressed
                    ? "rate_limit_retry_suppressed"
                    : "identity_unavailable";
                var point = _acquisitionSuppressed
                    ? KiroMetricPoint.Error(errorCode)
                    : KiroMetricPoint.Unavailable(errorCode);
                stateChanged = _identityStatus != observedStatus ||
                    _identityMonitorFingerprint is not null || hasOwnedState || !FallbackMatchesLocked(point);
                cacheInvalidated = hasOwnedState;

                if (_identityMonitorFingerprint is not null)
                    CryptographicOperations.ZeroMemory(_identityMonitorFingerprint);
                _identityMonitorFingerprint = null;
                if (_identityFingerprint is not null) CryptographicOperations.ZeroMemory(_identityFingerprint);
                _identityFingerprint = null;
                Array.Clear(_cache);
                _identityStatus = observedStatus;

                if (stateChanged)
                {
                    Array.Fill(_fallback, point);
                    _lastFailureCode = point.ErrorCode;
                    _lastAttemptAt = _timeProvider.GetUtcNow();
                    _stateVersion++;
                    if (cacheInvalidated) _cacheVersion++;
                    cancelPublication = _activePublicationStop;
                }
            }
            else
            {
                var ownerChanged = _identityStatus == KiroIdentityStatus.Ready
                    ? _identityMonitorFingerprint is null ||
                      !CryptographicOperations.FixedTimeEquals(_identityMonitorFingerprint, fingerprint!)
                    : hasOwnedState;

                if (ownerChanged)
                {
                    if (_identityFingerprint is not null) CryptographicOperations.ZeroMemory(_identityFingerprint);
                    _identityFingerprint = null;
                    Array.Clear(_cache);
                    cacheInvalidated = hasOwnedState;
                    var errorCode = _acquisitionSuppressed
                        ? "rate_limit_retry_suppressed"
                        : "identity_changed";
                    var point = _acquisitionSuppressed
                        ? KiroMetricPoint.Error(errorCode)
                        : KiroMetricPoint.Unavailable(errorCode);
                    Array.Fill(_fallback, point);
                    _lastFailureCode = point.ErrorCode;
                    _lastAttemptAt = _timeProvider.GetUtcNow();
                    _stateVersion++;
                    _cacheVersion++;
                    cancelPublication = _activePublicationStop;
                    stateChanged = true;
                }

                if (_identityMonitorFingerprint is not null)
                    CryptographicOperations.ZeroMemory(_identityMonitorFingerprint);
                _identityMonitorFingerprint = (byte[])fingerprint!.Clone();
                _identityStatus = KiroIdentityStatus.Ready;
            }
        }

        if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
        if (!stateChanged) return;
        TryCancel(cancelPublication);
        if (cacheInvalidated) SignalValidityChanged();
        SignalPublish();
    }

    private bool FallbackMatchesLocked(KiroMetricPoint expected)
    {
        foreach (var point in _fallback)
        {
            if (point.State != expected.State || point.Value.HasValue ||
                !StringComparer.Ordinal.Equals(point.ErrorCode, expected.ErrorCode)) return false;
        }
        return true;
    }

    private bool HasCachedValuesLocked()
    {
        foreach (var cached in _cache)
        {
            if (cached is not null) return true;
        }
        return false;
    }

    private void ApplyAcquisitionSuppression(DateTimeOffset observedAt)
    {
        CancellationTokenSource? cancelPublication;
        lock (_gate)
        {
            _acquisitionSuppressed = true;
            var point = KiroMetricPoint.Error("rate_limit_retry_suppressed");
            Array.Fill(_fallback, point);
            _lastAttemptAt = observedAt;
            _lastFailureCode = point.ErrorCode;
            _stateVersion++;
            cancelPublication = _activePublicationStop;
        }

        TryCancel(cancelPublication);
        SignalPublish();
    }

    private void ApplyFailure(MetricState state, string? errorCode, DateTimeOffset observedAt)
    {
        CancellationTokenSource? cancelPublication;
        lock (_gate)
        {
            var point = state == MetricState.Unavailable
                ? KiroMetricPoint.Unavailable(errorCode)
                : KiroMetricPoint.Error(errorCode ?? "usage_query_failed");
            Array.Fill(_fallback, point);
            _lastAttemptAt = observedAt;
            _lastFailureCode = errorCode;
            _stateVersion++;
            cancelPublication = _activePublicationStop;
        }

        TryCancel(cancelPublication);
        SignalPublish();
    }

    private void RefreshCachedState(long expectedCacheVersion)
    {
        CancellationTokenSource? cancelPublication;
        lock (_gate)
        {
            if (_cacheVersion != expectedCacheVersion || !HasDueCacheTransitionLocked()) return;
            _stateVersion++;
            cancelPublication = _activePublicationStop;
        }

        TryCancel(cancelPublication);
        SignalPublish();
    }

    private void SignalValidityChanged()
    {
        try { _validitySignal.Release(); }
        catch (SemaphoreFullException) { return; }
    }

    private MetricSnapshot[] CreateSnapshotsLocked(long timestamp)
    {
        var snapshots = new MetricSnapshot[_descriptors.Length];
        for (var index = 0; index < snapshots.Length; index++)
        {
            var point = ResolvePointLocked(index, timestamp, out var observedAt);
            var confidence = point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown;
            snapshots[index] = new MetricSnapshot(_descriptors[index].Id, point.Value, observedAt, point.State,
                confidence, Source, point.ErrorCode);
        }
        return snapshots;
    }

    private KiroMetricPoint ResolvePointLocked(int index, long timestamp, out DateTimeOffset observedAt)
    {
        var cached = _cache[index];
        if (cached is null)
        {
            observedAt = _lastAttemptAt;
            return _fallback[index];
        }

        observedAt = cached.ObservedAt;
        var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
        if (age < TimeSpan.Zero) return KiroMetricPoint.Error("clock_invalid");
        if (cached.ValidFor is TimeSpan validFor && age >= validFor)
            return KiroMetricPoint.Unavailable("reset_elapsed");
        if (age >= _maximumCacheAge) return KiroMetricPoint.Unavailable("cache_expired");

        var value = cached.Value;
        var expiryCrossed = false;
        if (index == BonusRemainingIndex && cached.BonusAllocations is not null)
        {
            decimal remaining = 0;
            foreach (var allocation in cached.BonusAllocations)
            {
                if (allocation.ExpiresAt is DateTimeOffset expiry)
                {
                    var expiresAfter = expiry - cached.ObservedAt;
                    if (expiresAfter <= TimeSpan.Zero || age >= expiresAfter)
                    {
                        expiryCrossed |= expiresAfter > TimeSpan.Zero;
                        continue;
                    }
                }
                remaining += allocation.Remaining;
            }
            value = MetricValue.From(remaining);
        }

        var stale = age >= _staleAfter || expiryCrossed;
        return new KiroMetricPoint(stale ? MetricState.Stale : MetricState.Fresh, value,
            stale ? _lastFailureCode : null);
    }

    private TimeSpan GetNextPollDelay()
    {
        lock (_gate)
        {
            var next = GetNextCacheTransitionDelayLocked(_timeProvider.GetTimestamp());
            return next is TimeSpan delay && delay < _pollInterval ? delay : _pollInterval;
        }
    }

    private (TimeSpan? Delay, long CacheVersion) GetNextCacheTransition()
    {
        lock (_gate)
            return (GetNextCacheTransitionDelayLocked(_timeProvider.GetTimestamp()), _cacheVersion);
    }

    private TimeSpan? GetNextCacheTransitionDelayLocked(long timestamp)
    {
        TimeSpan? shortest = null;
        foreach (var cached in _cache)
        {
            if (cached is null) continue;
            var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
            Consider(_staleAfter - age);
            Consider(_maximumCacheAge - age);
            if (cached.ValidFor is TimeSpan validFor && validFor < _maximumCacheAge)
                Consider(validFor - age);
            if (cached.BonusAllocations is null) continue;
            foreach (var allocation in cached.BonusAllocations)
            {
                if (allocation.ExpiresAt is DateTimeOffset expiry)
                {
                    var expiresAfter = expiry - cached.ObservedAt;
                    if (expiresAfter > TimeSpan.Zero && expiresAfter < _maximumCacheAge)
                        Consider(expiresAfter - age);
                }
            }
        }
        return shortest;

        void Consider(TimeSpan candidate)
        {
            if (candidate <= TimeSpan.Zero || shortest is TimeSpan current && candidate >= current) return;
            shortest = candidate;
        }
    }

    private bool HasDueCacheTransitionLocked()
    {
        var timestamp = _timeProvider.GetTimestamp();
        foreach (var cached in _cache)
        {
            if (cached is null) continue;
            var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
            if (age < TimeSpan.Zero || age >= _staleAfter || age >= _maximumCacheAge ||
                cached.ValidFor is TimeSpan validFor && age >= validFor) return true;
            if (cached.BonusAllocations is null) continue;
            foreach (var allocation in cached.BonusAllocations)
            {
                if (allocation.ExpiresAt is DateTimeOffset expiry)
                {
                    var expiresAfter = expiry - cached.ObservedAt;
                    if (expiresAfter > TimeSpan.Zero && age >= expiresAfter) return true;
                }
            }
        }
        return false;
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(StoppedPublishTimeout);
        var observedAt = _timeProvider.GetUtcNow();
        foreach (var descriptor in _descriptors)
        {
            try
            {
                await _bus.PublishAsync(new MetricSnapshot(descriptor.Id, null, observedAt, MetricState.Stopped,
                    MetricConfidence.Unknown, Source), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return; }
        }
    }

    private void CancelActiveOperations()
    {
        CancellationTokenSource? acquisition;
        CancellationTokenSource? publication;
        lock (_gate)
        {
            acquisition = _activeAcquisitionStop;
            publication = _activePublicationStop;
        }
        TryCancel(acquisition);
        TryCancel(publication);
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { return; }
    }

    private void SignalPublish()
    {
        try { _publishSignal.Release(); }
        catch (SemaphoreFullException) { return; }
    }

    private static string? SafeErrorCode(string? errorCode)
    {
        if (errorCode is null || errorCode.Length is 0 or > 64) return "usage_query_failed";
        foreach (var character in errorCode)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
                return "usage_query_failed";
        }
        return errorCode;
    }

    private sealed record CachedQuota(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp,
        TimeSpan? ValidFor, KiroBonusAllocation[]? BonusAllocations);
}
