#nullable enable

using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Qoder;

internal sealed class QoderQuotaProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan OwnerCheckInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan WorkerDrainTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan StoppedPublishTimeout = TimeSpan.FromMilliseconds(500);
    internal const string Source = "Qoder.International.LocalClientRPC.credit/usage.v1";

    private static readonly string[] DisplayNames =
    [
        "Qoder Personal Quota Used",
        "Qoder Personal Credits Used",
        "Qoder Personal Credits Remaining",
        "Qoder Personal Credits Limit",
        "Qoder Add-on Quota Used",
        "Qoder Add-on Credits Used",
        "Qoder Add-on Credits Remaining",
        "Qoder Add-on Credits Limit",
        "Qoder Organization Quota Used",
        "Qoder Organization Credits Used",
        "Qoder Organization Credits Remaining",
        "Qoder Organization Credits Limit",
        "Qoder Organization Quota Available",
        "Qoder Overall Quota Used",
        "Qoder Quota Exhausted",
        "Qoder Plan Expires At"
    ];

    private static readonly MetricValueKind[] ValueKinds =
    [
        MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double,
        MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double,
        MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.Double,
        MetricValueKind.Boolean, MetricValueKind.Double, MetricValueKind.Boolean, MetricValueKind.DateTimeOffset
    ];

    private static readonly string[] SemanticTypes =
    [
        "quota_used", "quota_used", "quota_remaining", "quota_limit",
        "quota_used", "quota_used", "quota_remaining", "quota_limit",
        "quota_used", "quota_used", "quota_remaining", "quota_limit",
        "boolean", "quota_used", "boolean", "timestamp"
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly Func<CancellationToken, Task<QoderQuotaAcquisition>> _acquire;
    private readonly Func<QoderOwnerKey, CancellationToken, Task<QoderOwnerVerification>> _verifyOwner;
    private readonly QoderQuotaAcquirer? _ownedAcquirer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private QoderQuotaReading? _reading;
    private QoderOwnerKey? _activeOwner;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private MetricState _failureState = MetricState.Unavailable;
    private string? _failureCode;
    private DateTimeOffset _failureObservedAt;
    private long _ownerGeneration;
    private long _publicationGeneration;
    private int _runStarted;
    private int _disposed;

    public QoderQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new QoderQuotaAcquirer(), TimeProvider.System)
    {
    }

    private QoderQuotaProvider(MetricRegistry registry, MetricBus bus, QoderQuotaAcquirer acquirer,
        TimeProvider timeProvider)
        : this(registry, bus, acquirer.AcquireOnceAsync, acquirer.VerifyOwnerAsync, timeProvider, acquirer)
    {
    }

    internal QoderQuotaProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<QoderQuotaAcquisition>> acquire,
        Func<QoderOwnerKey, CancellationToken, Task<QoderOwnerVerification>> verifyOwner,
        TimeProvider timeProvider)
        : this(registry, bus, acquire, verifyOwner, timeProvider, null)
    {
    }

    private QoderQuotaProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<QoderQuotaAcquisition>> acquire,
        Func<QoderOwnerKey, CancellationToken, Task<QoderOwnerVerification>> verifyOwner,
        TimeProvider timeProvider, QoderQuotaAcquirer? ownedAcquirer)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        _verifyOwner = verifyOwner ?? throw new ArgumentNullException(nameof(verifyOwner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownedAcquirer = ownedAcquirer;
        _failureObservedAt = _timeProvider.GetUtcNow();

        _descriptors = new MetricDescriptor[QoderQuotaParser.MetricCount];
        for (var index = 0; index < _descriptors.Length; index++)
        {
            var unit = index switch
            {
                0 or 4 or 8 or 13 => "%",
                1 or 2 or 3 or 5 or 6 or 7 or 9 or 10 or 11 => "credits",
                _ => null
            };
            _descriptors[index] = new MetricDescriptor(new MetricId(QoderQuotaParser.MetricIds[index]),
                DisplayNames[index], SemanticTypes[index], ValueKinds[index], unit: unit);
            if (!registry.TryRegister(_descriptors[index]))
                throw new InvalidOperationException("Qoder quota metric registration failed.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("Qoder quota provider is already running.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var workers = new Task[] { PollLoopAsync(stop.Token), MonitorOwnerLoopAsync(stop.Token) };
        ExceptionDispatchInfo? workerFailure = null;
        try
        {
            await Task.WhenAny(workers).ConfigureAwait(false);
            stop.Cancel();
            workerFailure = await DrainWorkersAsync(workers, stop.Token).ConfigureAwait(false);
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
            workerFailure ??= await DrainWorkersAsync(workers, stop.Token).ConfigureAwait(false);
            try
            {
                await PublishStoppedAsync().ConfigureAwait(false);
            }
            catch (Exception stopFailure)
            {
                workerFailure ??= ExceptionDispatchInfo.Capture(stopFailure);
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
        var generation = 0L;
        try
        {
            await _stateLock.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                generation = _ownerGeneration;
                requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = requestStop;
            }
            finally { _stateLock.Release(); }

            QoderQuotaAcquisition acquisition;
            try
            {
                acquisition = await _acquire(requestStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestStop.IsCancellationRequested &&
                !admission.IsCancellationRequested)
            {
                return;
            }

            admission.Token.ThrowIfCancellationRequested();
            await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_ownerGeneration != generation)
                    return;
                ApplyAcquisitionLocked(acquisition);
            }
            finally { _stateLock.Release(); }

            await PublishCurrentAsync(admission.Token).ConfigureAwait(false);
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

    internal async Task VerifyOwnerOnceAsync(CancellationToken cancellationToken = default)
    {
        QoderOwnerKey? expected;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { expected = _activeOwner; }
        finally { _stateLock.Release(); }
        if (expected is null)
            return;

        QoderOwnerVerification verification;
        try
        {
            verification = await _verifyOwner(expected, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        var changed = false;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeOwner is null || !_activeOwner.Equals(expected) ||
                verification.State == QoderOwnerVerificationState.Verified)
                return;

            InvalidateOwnerWorkLocked();
            _reading = null;
            _failureState = MetricState.Unavailable;
            _failureCode = verification.ErrorCode ??
                (verification.State == QoderOwnerVerificationState.Changed ? null : "identity_unavailable");
            _failureObservedAt = _timeProvider.GetUtcNow();
            _activeOwner = verification.State == QoderOwnerVerificationState.Changed ? verification.Owner : null;
            changed = true;
        }
        finally { _stateLock.Release(); }

        if (changed)
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
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
                ownerGeneration = _ownerGeneration;
                publicationGeneration = _publicationGeneration;
                snapshots = CreateSnapshotsLocked();
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
                _reading = null;
                _activeOwner = null;
                _failureCode = null;
            }
            finally { _stateLock.Release(); }
        }
    }

    private void ApplyAcquisitionLocked(QoderQuotaAcquisition acquisition)
    {
        InvalidatePublicationLocked();
        if (acquisition.Owner is null || acquisition.Reading is null)
        {
            if (_activeOwner is not null)
                InvalidateOwnerWorkLocked();
            _activeOwner = null;
            _reading = null;
            _failureState = acquisition.FailureState;
            _failureCode = acquisition.ErrorCode;
            _failureObservedAt = _timeProvider.GetUtcNow();
            return;
        }

        if (_activeOwner is null || !_activeOwner.Equals(acquisition.Owner))
        {
            InvalidateOwnerWorkLocked();
            _activeOwner = acquisition.Owner;
        }
        _reading = acquisition.Reading;
        _failureState = MetricState.Unavailable;
        _failureCode = null;
        _failureObservedAt = acquisition.Reading.ObservedAt;
    }

    private MetricSnapshot[] CreateSnapshotsLocked()
    {
        var snapshots = new MetricSnapshot[_descriptors.Length];
        for (var index = 0; index < snapshots.Length; index++)
        {
            var point = _reading?.Points[index];
            var state = point?.State ?? _failureState;
            var value = point?.Value;
            var confidence = value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown;
            var observedAt = _reading?.ObservedAt ?? _failureObservedAt;
            var errorCode = point?.ErrorCode ?? _failureCode;
            snapshots[index] = new MetricSnapshot(_descriptors[index].Id, value, observedAt, state, confidence,
                Source, errorCode);
        }
        return snapshots;
    }

    private async Task<bool> IsPublicationCurrentAsync(long ownerGeneration, long publicationGeneration,
        CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _ownerGeneration == ownerGeneration && _publicationGeneration == publicationGeneration;
        }
        finally { _stateLock.Release(); }
    }

    private void InvalidateOwnerWorkLocked()
    {
        _ownerGeneration++;
        _activeRequestStop?.Cancel();
        InvalidatePublicationLocked();
    }

    private void InvalidatePublicationLocked()
    {
        _publicationGeneration++;
        _activePublishStop?.Cancel();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorOwnerLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(OwnerCheckInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            await VerifyOwnerOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExceptionDispatchInfo?> DrainWorkersAsync(Task[] workers, CancellationToken stopToken)
    {
        var allWorkers = Task.WhenAll(workers);
        try
        {
            await allWorkers.WaitAsync(WorkerDrainTimeout).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return ExceptionDispatchInfo.Capture(new TimeoutException("Qoder quota workers did not stop in time."));
        }
        catch (Exception exception)
        {
            return ExceptionDispatchInfo.Capture(exception);
        }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(StoppedPublishTimeout);
        var publishLockAcquired = false;
        var stateLockAcquired = false;
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
            _reading = null;
            _activeOwner = null;
            _failureCode = null;
            var snapshots = _descriptors.Select(descriptor => new MetricSnapshot(descriptor.Id, null, now,
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
}
