#nullable enable

using System;
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
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _identityKey = RandomNumberGenerator.GetBytes(32);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishSignal = new(0, 1);
    private readonly object _gate = new();
    private readonly CachedQuota?[] _cache = new CachedQuota?[MetricIds.Length];
    private readonly KiroMetricPoint[] _fallback = new KiroMetricPoint[MetricIds.Length];
    private byte[]? _identityFingerprint;
    private CancellationTokenSource? _activeAcquisitionStop;
    private CancellationTokenSource? _activePublicationStop;
    private string? _lastFailureCode;
    private DateTimeOffset _lastAttemptAt;
    private long _stateVersion;
    private bool _disposed;

    public KiroQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus,
            cancellationToken => KiroAcpUsageClient.ReadAsync(TimeProvider.System, cancellationToken),
            TimeProvider.System)
    {
    }

    internal KiroQuotaProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<KiroAcquisition>> readUsage, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _readUsage = readUsage ?? throw new ArgumentNullException(nameof(readUsage));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
        Task[] workers = [PollLoopAsync(stop.Token), PublishLoopAsync(stop.Token)];
        SignalPublish();
        Exception? workerFailure = null;

        try
        {
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            workerFailure = await ObserveWorkerAsync(completed, stop.Token).ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            CancelActiveOperations();
            try
            {
                workerFailure ??= await ObserveWorkersAsync(workers, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                await PublishStoppedAsync().ConfigureAwait(false);
            }
        }

        if (workerFailure is not null) ExceptionDispatchInfo.Capture(workerFailure).Throw();
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            KiroAcquisition acquisition;
            lock (_gate) _activeAcquisitionStop = admission;
            try
            {
                acquisition = await _readUsage(admission.Token).ConfigureAwait(false);
                admission.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (admission.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                acquisition = KiroAcquisition.Error("usage_query_failed");
            }

            if (acquisition.Status == KiroAcquisitionStatus.Ready && acquisition.Payload is not null)
            {
                try
                {
                    var observedAt = _timeProvider.GetUtcNow();
                    var parsed = KiroQuotaParser.Parse(acquisition.Payload, _identityKey, observedAt);
                    if (parsed.Reading is null)
                        ApplyFailure(MetricState.Error, parsed.ErrorCode ?? "schema_invalid", observedAt);
                    else
                        ApplyReading(parsed.Reading);
                }
                finally { CryptographicOperations.ZeroMemory(acquisition.Payload); }
            }
            else
            {
                var state = acquisition.Status is KiroAcquisitionStatus.Unavailable or KiroAcquisitionStatus.Unsupported
                    ? MetricState.Unavailable
                    : MetricState.Error;
                ApplyFailure(state, SafeErrorCode(acquisition.ErrorCode), _timeProvider.GetUtcNow());
            }
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
            var (delay, transitionDue) = GetNextPollDelay();
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            if (transitionDue) RefreshCachedState();
        }
    }

    private static async Task<Exception?> ObserveWorkerAsync(Task worker, CancellationToken stopToken)
    {
        try
        {
            await worker.ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { return null; }
        catch (Exception exception) { return exception; }
    }

    private static async Task<Exception?> ObserveWorkersAsync(Task[] workers, CancellationToken stopToken)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { return null; }
        catch (Exception exception) { return exception; }
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
            cancelPublication = _activePublicationStop;
        }

        CryptographicOperations.ZeroMemory(reading.IdentityFingerprint);
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

    private void RefreshCachedState()
    {
        CancellationTokenSource? cancelPublication;
        lock (_gate)
        {
            _stateVersion++;
            cancelPublication = _activePublicationStop;
        }

        TryCancel(cancelPublication);
        SignalPublish();
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
        if (age >= MaximumCacheAge) return KiroMetricPoint.Unavailable("cache_expired");

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

        var stale = age >= StaleAfter || expiryCrossed;
        return new KiroMetricPoint(stale ? MetricState.Stale : MetricState.Fresh, value,
            stale ? _lastFailureCode : null);
    }

    private (TimeSpan Delay, bool TransitionDue) GetNextPollDelay()
    {
        lock (_gate)
        {
            var nowTimestamp = _timeProvider.GetTimestamp();
            var shortest = PollInterval;
            var transitionDue = false;
            foreach (var cached in _cache)
            {
                if (cached is null) continue;
                var age = _timeProvider.GetElapsedTime(cached.Timestamp, nowTimestamp);
                Consider(StaleAfter - age);
                Consider(MaximumCacheAge - age);
                if (cached.ValidFor is TimeSpan validFor) Consider(validFor - age);
                if (cached.BonusAllocations is not null)
                {
                    foreach (var allocation in cached.BonusAllocations)
                    {
                        if (allocation.ExpiresAt is DateTimeOffset expiry)
                            Consider((expiry - cached.ObservedAt) - age);
                    }
                }
            }
            return (shortest, transitionDue);

            void Consider(TimeSpan candidate)
            {
                if (candidate <= TimeSpan.Zero || candidate > shortest) return;
                shortest = candidate;
                transitionDue = true;
            }
        }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(StoppedPublishTimeout, _timeProvider);
        var observedAt = _timeProvider.GetUtcNow();
        foreach (var descriptor in _descriptors)
        {
            try
            {
                await _bus.PublishAsync(new MetricSnapshot(descriptor.Id, null, observedAt, MetricState.Stopped,
                    MetricConfidence.Unknown, Source), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { return; }
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
