#nullable enable

using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Antigravity;

internal sealed class AntigravityQuotaProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan OwnerCheckInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumPollWaitChunk = TimeSpan.FromSeconds(30);
    internal const string Source = "Antigravity.LocalLanguageServer.RetrieveUserQuotaSummary.v1";

    private static readonly string[] BucketNames = ["3p-weekly", "3p-5h", "gemini-weekly", "gemini-5h"];
    private static readonly MetricValueKind[] ValueKinds =
    [
        MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double,
        MetricValueKind.DateTimeOffset, MetricValueKind.DateTimeOffset,
        MetricValueKind.DateTimeOffset, MetricValueKind.DateTimeOffset
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly Func<CancellationToken, Task<AntigravityQuotaAcquisition>> _acquire;
    private readonly Func<AntigravityOwnerKey, CancellationToken, Task<AntigravityOwnerVerification>> _verifyOwner;
    private readonly AntigravityQuotaAcquirer? _ownedAcquirer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CachedPoint?[] _cache = new CachedPoint?[AntigravityQuotaParser.MetricCount];
    private AntigravityQuotaPoint[]? _lastReadingPoints;
    private DateTimeOffset _lastReadingObservedAt;
    private AntigravityOwnerKey? _activeOwner;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private MetricState _failureState = MetricState.Unavailable;
    private string? _failureCode;
    private DateTimeOffset _failureObservedAt;
    private long _ownerGeneration;
    private long _publicationGeneration;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _runStarted;
    private int _disposed;

    public AntigravityQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new AntigravityQuotaAcquirer(), TimeProvider.System)
    {
    }

    private AntigravityQuotaProvider(MetricRegistry registry, MetricBus bus, AntigravityQuotaAcquirer acquirer,
        TimeProvider timeProvider)
        : this(registry, bus, acquirer.AcquireOnceAsync, acquirer.VerifyOwnerAsync, timeProvider, acquirer)
    {
    }

    internal AntigravityQuotaProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<AntigravityQuotaAcquisition>> acquire,
        Func<AntigravityOwnerKey, CancellationToken, Task<AntigravityOwnerVerification>> verifyOwner,
        TimeProvider timeProvider)
        : this(registry, bus, acquire, verifyOwner, timeProvider, null)
    {
    }

    private AntigravityQuotaProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<AntigravityQuotaAcquisition>> acquire,
        Func<AntigravityOwnerKey, CancellationToken, Task<AntigravityOwnerVerification>> verifyOwner,
        TimeProvider timeProvider, AntigravityQuotaAcquirer? ownedAcquirer)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        _verifyOwner = verifyOwner ?? throw new ArgumentNullException(nameof(verifyOwner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownedAcquirer = ownedAcquirer;
        _failureObservedAt = _timeProvider.GetUtcNow();

        var names = BucketNames.Concat(BucketNames).Select((bucket, index) =>
            index < BucketNames.Length ? $"Antigravity {bucket} Quota Remaining" : $"Antigravity {bucket} Reset At")
            .ToArray();
        _descriptors = new MetricDescriptor[AntigravityQuotaParser.MetricCount];
        for (var index = 0; index < _descriptors.Length; index++)
        {
            _descriptors[index] = new MetricDescriptor(new MetricId(AntigravityQuotaParser.MetricIds[index]),
                names[index], index < BucketNames.Length ? "quota_remaining" : "timestamp", ValueKinds[index],
                unit: index < BucketNames.Length ? "%" : null);
            if (!registry.TryRegister(_descriptors[index]))
                throw new InvalidOperationException("Antigravity quota metric registration failed.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("Antigravity quota provider is already running.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var workers = new Task[4];
        ExceptionDispatchInfo? workerFailure = null;
        try
        {
            workers[0] = PollLoopAsync(stop.Token);
            workers[1] = PublishLoopAsync(stop.Token);
            workers[2] = MonitorOwnerLoopAsync(stop.Token);
            workers[3] = MonitorValidityLoopAsync(stop.Token);
            await Task.WhenAny(workers).ConfigureAwait(false);
            stop.Cancel();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            workerFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            stop.Cancel();
            var startedWorkers = workers.Where(worker => worker is not null).ToArray();
            try
            {
                await Task.WhenAll(startedWorkers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                workerFailure ??= ExceptionDispatchInfo.Capture(exception);
            }

            try
            {
                await PublishStoppedAsync().ConfigureAwait(false);
            }
            catch (Exception stopFailure)
            {
                if (workerFailure is null)
                    throw;
                workerFailure.SourceException.Data["StoppedPublicationFailure"] = stopFailure;
            }
        }

        workerFailure?.Throw();
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? requestStop = null;
        long generation = 0;
        try
        {
            var timestamp = _timeProvider.GetTimestamp();
            await _stateLock.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                if (timestamp < _nextPollTimestamp)
                    return;
                generation = _ownerGeneration;
                _nextPollTimestamp = AddTimestamp(timestamp, PollInterval);
                requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = requestStop;
            }
            finally { _stateLock.Release(); }

            AntigravityQuotaAcquisition? acquisition = null;
            var ownerCancelled = false;
            try
            {
                acquisition = await _acquire(requestStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestStop.IsCancellationRequested && !admission.IsCancellationRequested)
            {
                ownerCancelled = true;
            }
            catch (OperationCanceledException) when (admission.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                acquisition = AntigravityQuotaAcquisition.Failure("source_unavailable", MetricState.Unavailable);
            }

            if (ownerCancelled)
                return;

            admission.Token.ThrowIfCancellationRequested();
            await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_ownerGeneration != generation)
                {
                    var currentTimestamp = _timeProvider.GetTimestamp();
                    if (currentTimestamp >= _retryNotBeforeTimestamp)
                        _nextPollTimestamp = currentTimestamp;
                    return;
                }
                ApplyAcquisition(acquisition!);
            }
            finally { _stateLock.Release(); }
        }
        finally
        {
            if (requestStop is not null)
            {
                await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activeRequestStop, requestStop))
                        _activeRequestStop = null;
                }
                finally { _stateLock.Release(); }
                requestStop.Dispose();
            }
            _pollLock.Release();
        }
    }

    internal async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _publishLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? publication = null;
        try
        {
            publication = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
            MetricSnapshot[] snapshots;
            long ownerGeneration;
            long publicationGeneration;
            await _stateLock.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                ExpireCachesLocked(now, timestamp);
                ownerGeneration = _ownerGeneration;
                publicationGeneration = _publicationGeneration;
                snapshots = new MetricSnapshot[_descriptors.Length];
                for (var index = 0; index < snapshots.Length; index++)
                    snapshots[index] = CreateSnapshot(index, now, timestamp);
                _activePublishStop?.Cancel();
                _activePublishStop = publication;
            }
            finally { _stateLock.Release(); }

            try
            {
                foreach (var snapshot in snapshots)
                {
                    if (!await IsPublicationCurrentAsync(ownerGeneration, publicationGeneration, publication.Token)
                            .ConfigureAwait(false))
                        return;
                    await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
                    if (!await IsPublicationCurrentAsync(ownerGeneration, publicationGeneration, publication.Token)
                            .ConfigureAwait(false))
                        return;
                }
            }
            catch (OperationCanceledException) when (!admission.IsCancellationRequested &&
                publication.IsCancellationRequested)
            {
                return;
            }
        }
        finally
        {
            if (publication is not null)
            {
                await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activePublishStop, publication))
                        _activePublishStop = null;
                }
                finally { _stateLock.Release(); }
                publication.Dispose();
            }
            _publishLock.Release();
        }
    }

    internal async Task VerifyOwnerOnceAsync(CancellationToken cancellationToken = default)
    {
        AntigravityOwnerKey? expected;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            expected = _activeOwner;
            if (expected is null || _timeProvider.GetTimestamp() < _retryNotBeforeTimestamp)
                return;
        }
        finally { _stateLock.Release(); }

        AntigravityOwnerVerification verification;
        try
        {
            verification = await _verifyOwner(expected, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            verification = new AntigravityOwnerVerification(AntigravityOwnerVerificationState.Unverifiable, null,
                "identity_unavailable");
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeOwner is null || !_activeOwner.Equals(expected) ||
                verification.State == AntigravityOwnerVerificationState.Verified)
                return;

            InvalidateOwnerWorkLocked();
            Array.Clear(_cache);
            _lastReadingPoints = null;
            _failureObservedAt = _timeProvider.GetUtcNow();

            if (verification.State == AntigravityOwnerVerificationState.Unverifiable)
            {
                _failureState = verification.FailureState;
                _failureCode = verification.ErrorCode ?? "identity_unavailable";
                var retryAt = AddTimestamp(_timeProvider.GetTimestamp(), verification.RetryAfter ?? PollInterval);
                _retryNotBeforeTimestamp = Math.Max(_retryNotBeforeTimestamp, retryAt);
                _nextPollTimestamp = Math.Max(_nextPollTimestamp, _retryNotBeforeTimestamp);
                return;
            }

            _activeOwner = verification.Owner;
            _failureState = MetricState.Unavailable;
            _failureCode = verification.Owner is null ? "identity_unavailable" : null;
            _retryNotBeforeTimestamp = 0;
            _nextPollTimestamp = _timeProvider.GetTimestamp();
        }
        finally { _stateLock.Release(); }
    }

    internal async Task ExpireValidityOnceAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExpireCachesLocked(_timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
        }
        finally { _stateLock.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _shutdown.Cancel();
        _ownedAcquirer?.Dispose();
        if (_stateLock.Wait(TimeSpan.FromMilliseconds(250)))
        {
            try
            {
                InvalidateOwnerWorkLocked();
                Array.Clear(_cache);
                _activeOwner = null;
                _lastReadingPoints = null;
                _failureCode = null;
            }
            finally { _stateLock.Release(); }
        }
    }

    private void ApplyAcquisition(AntigravityQuotaAcquisition acquisition)
    {
        var now = _timeProvider.GetUtcNow();
        InvalidatePublicationLocked();
        if (acquisition.Owner is null)
        {
            if (_activeOwner is not null)
                InvalidateOwnerWorkLocked();
            Array.Clear(_cache);
            _activeOwner = null;
            _lastReadingPoints = null;
            _failureState = acquisition.FailureState;
            _failureCode = acquisition.ErrorCode;
            _failureObservedAt = now;
        }
        else
        {
            if (_activeOwner is null || !_activeOwner.Equals(acquisition.Owner))
            {
                InvalidateOwnerWorkLocked();
                Array.Clear(_cache);
                _lastReadingPoints = null;
                _activeOwner = acquisition.Owner;
                _retryNotBeforeTimestamp = 0;
            }

            _failureState = acquisition.FailureState;
            _failureCode = acquisition.ErrorCode;
            _failureObservedAt = now;
            _lastReadingPoints = acquisition.Reading?.Points;
            if (acquisition.Reading is null && acquisition.FailureState == MetricState.Error)
                Array.Clear(_cache);
            if (acquisition.Reading is not null)
            {
                _lastReadingObservedAt = acquisition.Reading.ObservedAt;
                var timestamp = _timeProvider.GetTimestamp();
                for (var index = 0; index < _cache.Length; index++)
                {
                    var point = acquisition.Reading.Points[index];
                    _cache[index] = point.State == MetricState.Fresh && point.Value is MetricValue value
                        ? new CachedPoint(value, acquisition.Reading.ObservedAt, timestamp)
                        : null;
                }
                _failureCode = null;
            }
        }

        if (acquisition.RetryAfter is TimeSpan retryAfter)
        {
            var retryAt = AddTimestamp(_timeProvider.GetTimestamp(), retryAfter);
            _retryNotBeforeTimestamp = Math.Max(_retryNotBeforeTimestamp, retryAt);
            _nextPollTimestamp = Math.Max(_nextPollTimestamp, _retryNotBeforeTimestamp);
        }
    }

    private void InvalidateOwnerWorkLocked()
    {
        Interlocked.Increment(ref _ownerGeneration);
        _activeRequestStop?.Cancel();
        InvalidatePublicationLocked();
    }

    private void InvalidatePublicationLocked()
    {
        Interlocked.Increment(ref _publicationGeneration);
        _activePublishStop?.Cancel();
    }

    private bool ExpireCachesLocked(DateTimeOffset now, long timestamp)
    {
        var expiredAny = false;
        for (var bucketIndex = 0; bucketIndex < BucketNames.Length; bucketIndex++)
        {
            var remainingIndex = bucketIndex;
            var resetIndex = bucketIndex + BucketNames.Length;
            var resetElapsed = _cache[resetIndex]?.Value is MetricValue resetValue &&
                resetValue.AsDateTimeOffset() <= now;
            var cacheExpired = IsCacheExpired(_cache[remainingIndex], timestamp) ||
                IsCacheExpired(_cache[resetIndex], timestamp);
            if (!resetElapsed && !cacheExpired)
                continue;

            var reason = resetElapsed ? "reset_elapsed" : "cache_expired";
            _cache[remainingIndex] = null;
            _cache[resetIndex] = null;
            _lastReadingPoints ??= Enumerable.Repeat(AntigravityQuotaPoint.Unavailable(), _cache.Length).ToArray();
            _lastReadingPoints[remainingIndex] = AntigravityQuotaPoint.Unavailable(reason);
            _lastReadingPoints[resetIndex] = AntigravityQuotaPoint.Unavailable(reason);
            expiredAny = true;
        }

        if (expiredAny)
            InvalidatePublicationLocked();
        return expiredAny;
    }

    private bool IsCacheExpired(CachedPoint? cached, long timestamp) => cached is CachedPoint point &&
        _timeProvider.GetElapsedTime(point.Timestamp, timestamp) >= MaximumCacheAge;

    private async Task<bool> IsPublicationCurrentAsync(long ownerGeneration, long publicationGeneration,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ExpireCachesLocked(_timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
            return !cancellationToken.IsCancellationRequested &&
                ownerGeneration == _ownerGeneration && publicationGeneration == _publicationGeneration;
        }
        finally { _stateLock.Release(); }
    }

    private MetricSnapshot CreateSnapshot(int index, DateTimeOffset now, long timestamp)
    {
        var bucketIndex = index < BucketNames.Length ? index : index - BucketNames.Length;
        var resetIndex = bucketIndex + BucketNames.Length;
        var resetExpired = index < BucketNames.Length && _cache[resetIndex]?.Value is MetricValue resetValue &&
            resetValue.AsDateTimeOffset() <= now ||
            index >= BucketNames.Length && _cache[index]?.Value is MetricValue currentReset &&
            currentReset.AsDateTimeOffset() <= now;
        if (resetExpired)
            return Snapshot(index, null, now, MetricState.Unavailable, MetricConfidence.Unknown, "reset_elapsed");

        if (_cache[index] is CachedPoint cached)
        {
            var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
            if (age >= MaximumCacheAge)
                return Snapshot(index, null, now, MetricState.Unavailable, MetricConfidence.Unknown, "cache_expired");

            // This RPC can serve an upstream cache without a backend observation timestamp.
            return Snapshot(index, cached.Value, cached.ObservedAt, MetricState.Stale, MetricConfidence.Measured,
                _failureCode ?? "source_freshness_unknown");
        }

        if (_lastReadingPoints is not null)
        {
            var point = _lastReadingPoints[index];
            return Snapshot(index, point.Value, _lastReadingObservedAt, point.State,
                point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown, point.ErrorCode);
        }

        return Snapshot(index, null, _failureObservedAt, _failureState, MetricConfidence.Unknown, _failureCode);
    }

    private MetricSnapshot Snapshot(int index, MetricValue? value, DateTimeOffset observedAt, MetricState state,
        MetricConfidence confidence, string? errorCode) =>
        new(_descriptors[index].Id, value, observedAt, state, confidence, Source, errorCode);

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            var delay = await GetPollDelayAsync(cancellationToken).ConfigureAwait(false);
            var chunk = delay > MaximumPollWaitChunk ? MaximumPollWaitChunk : delay;
            if (chunk > TimeSpan.Zero)
                await Task.Delay(chunk, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PublishInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorOwnerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(OwnerCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await VerifyOwnerOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorValidityLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PublishInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ExpireValidityOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TimeSpan> GetPollDelayAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = _nextPollTimestamp - _timeProvider.GetTimestamp();
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)remaining / _timeProvider.TimestampFrequency);
        }
        finally { _stateLock.Release(); }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var publishLockAcquired = false;
        var stateLockAcquired = false;
        MetricSnapshot[]? snapshots = null;
        try
        {
            await _stateLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            stateLockAcquired = true;
            InvalidateOwnerWorkLocked();
            _stateLock.Release();
            stateLockAcquired = false;

            await _publishLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            publishLockAcquired = true;
            await _stateLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            stateLockAcquired = true;
            var now = _timeProvider.GetUtcNow();
            Array.Clear(_cache);
            _activeOwner = null;
            _lastReadingPoints = null;
            _failureCode = null;
            snapshots = _descriptors.Select(descriptor => new MetricSnapshot(descriptor.Id, null, now,
                MetricState.Stopped, MetricConfidence.Unknown, Source)).ToArray();
            _stateLock.Release();
            stateLockAcquired = false;

            foreach (var snapshot in snapshots)
                await _bus.PublishAsync(snapshot, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }
        finally
        {
            if (stateLockAcquired)
                _stateLock.Release();
            if (publishLockAcquired)
                _publishLock.Release();
        }
    }

    private long AddTimestamp(long timestamp, TimeSpan delay)
    {
        var ticks = delay.TotalSeconds * _timeProvider.TimestampFrequency;
        if (ticks >= long.MaxValue - timestamp)
            return long.MaxValue;
        if (ticks <= 0)
            return timestamp;
        return timestamp + (long)ticks;
    }

    private sealed record CachedPoint(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp);
}
