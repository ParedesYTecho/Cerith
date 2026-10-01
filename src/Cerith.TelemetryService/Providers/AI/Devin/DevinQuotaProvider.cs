#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal sealed class DevinQuotaProvider : IDisposable
{
    internal const string PlanStatusUrl =
        "https://server.codeium.com/exa.seat_management_pb.SeatManagementService/GetPlanStatus";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan CredentialCheckInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 64 * 1024;

    private const string Source = "DevinCLI.GetPlanStatus.ReadOnly.v1";
    private static readonly Uri PlanStatusEndpoint = new(PlanStatusUrl);
    private static readonly string[] MetricIds =
    [
        "ai.devin.quota.daily.remaining_percent",
        "ai.devin.quota.daily.reset_at",
        "ai.devin.quota.weekly.remaining_percent",
        "ai.devin.quota.weekly.reset_at"
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly DevinQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly SemaphoreSlim _publishSignal = new(0, 1);
    // The per-process tag detects credential-file changes; it is not account identity and is never persisted.
    private readonly byte[] _credentialScopeKey = RandomNumberGenerator.GetBytes(32);
    private readonly DevinQuotaPoint[] _points = new DevinQuotaPoint[DevinQuotaParser.PointCount];
    private readonly CachedPoint?[] _cache = new CachedPoint?[DevinQuotaParser.PointCount];
    private readonly DateTimeOffset[] _observedAt = new DateTimeOffset[DevinQuotaParser.PointCount];

    private DevinAuthStatus _authStatus = DevinAuthStatus.Unconfigured;
    private string? _authErrorCode = "not_configured";
    private string? _credentialGenerationTag;
    private string? _rejectedCredentialGenerationTag;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private long _credentialGeneration;
    private long _publicationGeneration;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _rateLimitFailures;
    private int _publishSignaled;
    private int _runStarted;
    private int _disposeStarted;
    private DateTimeOffset? _lastUtc;
    private long _lastTimestamp;

    public DevinQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new DevinQuotaAuthReader(), CreateHttpClient(), TimeProvider.System,
            ownsHttpClient: true)
    {
    }

    internal DevinQuotaProvider(MetricRegistry registry, MetricBus bus, DevinQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider)
        : this(registry, bus, authReader, httpClient, timeProvider, ownsHttpClient: false)
    {
    }

    private DevinQuotaProvider(MetricRegistry registry, MetricBus bus, DevinQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownsHttpClient = ownsHttpClient;

        var names = new[] { "Daily remaining", "Daily reset", "Weekly remaining", "Weekly reset" };
        var semanticTypes = new[] { "quota_remaining", "timestamp", "quota_remaining", "timestamp" };
        var valueKinds = new[]
        {
            MetricValueKind.Double, MetricValueKind.DateTimeOffset,
            MetricValueKind.Double, MetricValueKind.DateTimeOffset
        };
        var units = new string?[] { "%", null, "%", null };
        _descriptors = new MetricDescriptor[MetricIds.Length];
        var now = _timeProvider.GetUtcNow();
        for (var i = 0; i < _descriptors.Length; i++)
        {
            var descriptor = new MetricDescriptor(new MetricId(MetricIds[i]), $"Devin {names[i]}",
                semanticTypes[i], valueKinds[i], unit: units[i]);
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("Devin quota metric registration failed.");
            _descriptors[i] = descriptor;
            _points[i] = DevinQuotaPoint.Unavailable("not_configured");
            _observedAt[i] = now;
        }

        _lastUtc = now;
        _lastTimestamp = _timeProvider.GetTimestamp();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("Devin quota provider can only run once.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers = [];
        Exception? runFailure = null;
        Exception? stopFailure = null;
        Exception? drainFailure = null;
        Exception? stoppedPublicationFailure = null;
        try
        {
            await ObserveLocalStateAsync(stop.Token).ConfigureAwait(false);
            SignalPublication();
            workers = [MonitorLocalStateLoopAsync(stop.Token), PollLoopAsync(stop.Token), PublishLoopAsync(stop.Token)];
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (!stop.IsCancellationRequested)
                runFailure = new InvalidOperationException("Devin quota worker stopped unexpectedly.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            runFailure = exception;
        }
        finally
        {
            try
            {
                try { stop.Cancel(); }
                catch (Exception exception) { stopFailure = exception; }

                if (workers.Length > 0)
                {
                    try { await DrainWorkersAsync(workers, stop.Token).ConfigureAwait(false); }
                    catch (Exception exception) { drainFailure = exception; }
                }
            }
            finally
            {
                try { await PublishStoppedAsync().ConfigureAwait(false); }
                catch (Exception exception) { stoppedPublicationFailure = exception; }
            }
        }

        var failures = new List<Exception>();
        foreach (var failure in new[] { runFailure, stopFailure, drainFailure, stoppedPublicationFailure })
        {
            if (failure is null) continue;
            IEnumerable<Exception> candidates = failure is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions
                : new[] { failure };
            foreach (var candidate in candidates)
                if (!failures.Any(existing => ReferenceEquals(existing, candidate))) failures.Add(candidate);
        }

        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Devin quota run and shutdown encountered multiple failures.", failures);
    }

    private async Task DrainWorkersAsync(Task[] workers, CancellationToken stopToken)
    {
        var drain = Task.WhenAll(workers);
        try
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!drain.IsCompleted)
        {
            _ = drain.ContinueWith(static completed => completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested && drain.IsCanceled)
        {
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task MonitorLocalStateLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CredentialCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ObserveLocalStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(CredentialCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            if (_timeProvider.GetTimestamp() >= Volatile.Read(ref _nextPollTimestamp))
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _publishSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _publishSignaled, 0);
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);

        CancellationTokenSource? requestStop = null;
        DevinCliCredential? requestCredential = null;
        string? requestGenerationTag = null;
        long requestGeneration = 0;
        var publishState = false;
        try
        {
            await _stateGate.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                var authRead = _authReader.Read();
                publishState = ApplyAuthReadLocked(authRead, now, timestamp);
                publishState |= RefreshCacheLocked(now, timestamp);

                if (authRead.Status == DevinAuthStatus.Ready && authRead.Credential is not null)
                {
                    requestGenerationTag = _credentialGenerationTag;
                    if (requestGenerationTag is not null &&
                        !StringComparer.Ordinal.Equals(_rejectedCredentialGenerationTag, requestGenerationTag) &&
                        timestamp >= _retryNotBeforeTimestamp && timestamp >= _nextPollTimestamp)
                    {
                        requestGeneration = _credentialGeneration;
                        requestCredential = authRead.Credential;
                        requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                        _activeRequestStop = requestStop;
                    }
                }
                else
                {
                    _nextPollTimestamp = AddDelay(timestamp, PollInterval);
                }
            }
            finally { _stateGate.Release(); }

            if (publishState) SignalPublication();
            if (requestCredential is null || requestGenerationTag is null) return;

            var result = await SendRequestAsync(requestCredential, requestStop!.Token).ConfigureAwait(false);
            DevinQuotaPoint[]? parsed = null;
            try
            {
                if (result.Body is not null) parsed = DevinQuotaParser.Parse(result.Body);

                await _stateGate.WaitAsync(admission.Token).ConfigureAwait(false);
                try
                {
                    var receivedAt = _timeProvider.GetUtcNow();
                    var receivedTimestamp = _timeProvider.GetTimestamp();
                    var finalAuthRead = _authReader.Read();
                    var authChanged = ApplyAuthReadLocked(finalAuthRead, receivedAt, receivedTimestamp);
                    var accepted = requestGeneration == _credentialGeneration &&
                        StringComparer.Ordinal.Equals(requestGenerationTag, _credentialGenerationTag) &&
                        finalAuthRead.Status == DevinAuthStatus.Ready &&
                        !result.Cancelled;
                    publishState = authChanged;

                    if (accepted)
                    {
                        if (parsed is not null)
                        {
                            ApplySuccessfulResponseLocked(parsed, receivedAt, receivedTimestamp);
                            _rateLimitFailures = 0;
                            _retryNotBeforeTimestamp = 0;
                            _nextPollTimestamp = AddDelay(receivedTimestamp, PollInterval);
                        }
                        else if (result.ErrorCode is not null)
                        {
                            ApplyFailureLocked(result, requestGenerationTag, receivedAt, receivedTimestamp);
                        }

                        publishState = true;
                    }
                }
                finally { _stateGate.Release(); }
            }
            finally
            {
                if (result.Body is not null) CryptographicOperations.ZeroMemory(result.Body);
            }

            if (publishState) SignalPublication();
        }
        finally
        {
            if (requestStop is not null)
            {
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activeRequestStop, requestStop)) _activeRequestStop = null;
                }
                finally { _stateGate.Release(); }
                requestStop.Dispose();
            }
            _pollLock.Release();
        }
    }

    internal async ValueTask ObserveLocalStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _stateGate.WaitAsync(admission.Token).ConfigureAwait(false);
        bool changed;
        try
        {
            var now = _timeProvider.GetUtcNow();
            var timestamp = _timeProvider.GetTimestamp();
            changed = ApplyAuthReadLocked(_authReader.Read(), now, timestamp);
            changed |= RefreshCacheLocked(now, timestamp);
        }
        finally { _stateGate.Release(); }

        if (changed) SignalPublication();
    }

    private bool ApplyAuthReadLocked(DevinAuthRead read, DateTimeOffset observedAt, long timestamp)
    {
        if (read.Status == DevinAuthStatus.Ready && read.Credential is not null)
        {
            var nextTag = CredentialGenerationTag(read.Credential.ApiKey);
            if (_authStatus == DevinAuthStatus.Ready &&
                StringComparer.Ordinal.Equals(_credentialGenerationTag, nextTag))
                return false;

            _credentialGeneration++;
            _credentialGenerationTag = nextTag;
            _authStatus = DevinAuthStatus.Ready;
            _authErrorCode = null;
            _rejectedCredentialGenerationTag = null;
            _rateLimitFailures = 0;
            _retryNotBeforeTimestamp = 0;
            _nextPollTimestamp = timestamp;
            InvalidateOwnerWorkLocked();
            ClearToStateLocked(MetricState.Unavailable, "auth_changed", observedAt);
            return true;
        }

        var nextCode = read.ErrorCode ?? "credential_invalid";
        var changed = _authStatus != read.Status ||
            _credentialGenerationTag is not null ||
            !StringComparer.Ordinal.Equals(_authErrorCode, nextCode);
        if (!changed) return false;

        _credentialGeneration++;
        _credentialGenerationTag = null;
        _authStatus = read.Status;
        _authErrorCode = nextCode;
        _rejectedCredentialGenerationTag = null;
        _rateLimitFailures = 0;
        _retryNotBeforeTimestamp = 0;
        _nextPollTimestamp = AddDelay(timestamp, PollInterval);
        InvalidateOwnerWorkLocked();
        var state = read.Status == DevinAuthStatus.Unconfigured ? MetricState.Unavailable : MetricState.Error;
        ClearToStateLocked(state, nextCode, observedAt);
        return true;
    }

    private void ApplySuccessfulResponseLocked(DevinQuotaPoint[] points, DateTimeOffset observedAt,
        long timestamp)
    {
        InvalidatePublicationLocked();
        var accepted = (DevinQuotaPoint[])points.Clone();
        InvalidateElapsedWindow(accepted, DevinQuotaParser.DailyRemaining, DevinQuotaParser.DailyReset, observedAt);
        InvalidateElapsedWindow(accepted, DevinQuotaParser.WeeklyRemaining, DevinQuotaParser.WeeklyReset, observedAt);

        var dailyReset = ReadResetAt(accepted[DevinQuotaParser.DailyReset]);
        var weeklyReset = ReadResetAt(accepted[DevinQuotaParser.WeeklyReset]);
        for (var i = 0; i < accepted.Length; i++)
        {
            var point = accepted[i];
            _points[i] = point;
            _observedAt[i] = observedAt;
            var resetAt = i < DevinQuotaParser.WeeklyRemaining ? dailyReset : weeklyReset;
            _cache[i] = point.State == MetricState.Fresh && point.Value is MetricValue value
                ? new CachedPoint(value, observedAt, timestamp, resetAt, false, null)
                : null;
        }
    }

    private static void InvalidateElapsedWindow(DevinQuotaPoint[] points, int remainingIndex, int resetIndex,
        DateTimeOffset observedAt)
    {
        var resetAt = ReadResetAt(points[resetIndex]);
        if (resetAt is not DateTimeOffset reset || reset > observedAt) return;
        points[remainingIndex] = DevinQuotaPoint.Unavailable("reset_elapsed");
        points[resetIndex] = DevinQuotaPoint.Unavailable("reset_elapsed");
    }

    private static DateTimeOffset? ReadResetAt(DevinQuotaPoint point) =>
        point.State == MetricState.Fresh && point.Value is MetricValue value &&
        value.Kind == MetricValueKind.DateTimeOffset
            ? value.AsDateTimeOffset()
            : null;

    private void ApplyFailureLocked(TransportResult result, string credentialGenerationTag,
        DateTimeOffset observedAt, long timestamp)
    {
        InvalidatePublicationLocked();
        var errorCode = result.ErrorCode!;
        if (errorCode is "auth_required" or "auth_forbidden")
        {
            _rejectedCredentialGenerationTag = credentialGenerationTag;
            _retryNotBeforeTimestamp = 0;
            _nextPollTimestamp = long.MaxValue;
            ClearToStateLocked(MetricState.Error, errorCode, observedAt);
            return;
        }

        var cacheable = errorCode is "network_unavailable" or "timeout" or "upstream_unavailable" or "rate_limited";
        for (var i = 0; i < _points.Length; i++)
        {
            if (cacheable && TryGetValidCacheLocked(i, observedAt, timestamp, out var cached))
            {
                var stale = cached with { ForceStale = true, ErrorCode = errorCode };
                _cache[i] = stale;
                _points[i] = DevinQuotaPoint.Stale(stale.Value, errorCode);
                _observedAt[i] = stale.ObservedAt;
                continue;
            }

            _cache[i] = null;
            _points[i] = errorCode == "network_unavailable"
                ? DevinQuotaPoint.Unavailable(errorCode)
                : DevinQuotaPoint.Error(errorCode);
            _observedAt[i] = observedAt;
        }

        if (errorCode == "rate_limited")
        {
            _rateLimitFailures++;
            var delay = result.RetryAfter ?? GetBackoffDelay(_rateLimitFailures);
            SetRetryNotBeforeLocked(timestamp, delay);
        }
        else
        {
            _rateLimitFailures = 0;
            _retryNotBeforeTimestamp = 0;
        }

        ScheduleNextPollLocked(timestamp);
    }

    private bool RefreshCacheLocked(DateTimeOffset now, long timestamp)
    {
        var changed = false;
        if (_lastUtc is DateTimeOffset previousUtc)
        {
            var monotonicElapsed = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
            var utcElapsed = now - previousUtc;
            if (monotonicElapsed < TimeSpan.Zero || utcElapsed < TimeSpan.Zero ||
                Math.Abs((utcElapsed - monotonicElapsed).TotalSeconds) > 2)
            {
                ClearToStateLocked(MetricState.Unavailable, "clock_discontinuity", now);
                changed = true;
            }
        }
        _lastUtc = now;
        _lastTimestamp = timestamp;

        for (var i = 0; i < _cache.Length; i++)
        {
            var cached = _cache[i];
            if (cached is null) continue;

            var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
            var expiryCode = now < cached.ObservedAt || age < TimeSpan.Zero
                ? "clock_discontinuity"
                : cached.ResetAt is DateTimeOffset reset && reset <= now
                    ? "reset_elapsed"
                    : age >= MaximumCacheAge ? "cache_expired" : null;
            if (expiryCode is not null)
            {
                _cache[i] = null;
                _points[i] = DevinQuotaPoint.Unavailable(expiryCode);
                _observedAt[i] = now;
                changed = true;
                continue;
            }

            var stale = cached.ForceStale || age >= StaleAfter;
            var next = stale
                ? DevinQuotaPoint.Stale(cached.Value, cached.ErrorCode)
                : DevinQuotaPoint.Fresh(cached.Value);
            if (SamePoint(_points[i], next)) continue;
            _points[i] = next;
            _observedAt[i] = cached.ObservedAt;
            changed = true;
        }

        if (changed) InvalidatePublicationLocked();
        return changed;
    }

    private bool TryGetValidCacheLocked(int index, DateTimeOffset now, long timestamp, out CachedPoint cached)
    {
        var candidate = _cache[index];
        if (candidate is not null)
        {
            var age = _timeProvider.GetElapsedTime(candidate.Timestamp, timestamp);
            if (now >= candidate.ObservedAt && age >= TimeSpan.Zero && age < MaximumCacheAge &&
                (candidate.ResetAt is null || candidate.ResetAt > now))
            {
                cached = candidate;
                return true;
            }
        }

        _cache[index] = null;
        cached = null!;
        return false;
    }

    private void ClearToStateLocked(MetricState state, string errorCode, DateTimeOffset observedAt)
    {
        InvalidatePublicationLocked();
        for (var i = 0; i < _points.Length; i++)
        {
            _cache[i] = null;
            _points[i] = state == MetricState.Error
                ? DevinQuotaPoint.Error(errorCode)
                : DevinQuotaPoint.Unavailable(errorCode);
            _observedAt[i] = observedAt;
        }
    }

    private void ClearForStoppedLocked(DateTimeOffset observedAt)
    {
        InvalidatePublicationLocked();
        _credentialGeneration++;
        _credentialGenerationTag = null;
        _rejectedCredentialGenerationTag = null;
        for (var i = 0; i < _points.Length; i++)
        {
            _cache[i] = null;
            _points[i] = DevinQuotaPoint.Stopped();
            _observedAt[i] = observedAt;
        }
    }

    private void InvalidateOwnerWorkLocked()
    {
        _activeRequestStop?.Cancel();
        InvalidatePublicationLocked();
    }

    private void InvalidatePublicationLocked()
    {
        _publicationGeneration++;
        _activePublishStop?.Cancel();
    }

    internal async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _publishLock.WaitAsync(admission.Token).ConfigureAwait(false);
        using var publication = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
        var snapshots = new MetricSnapshot[_descriptors.Length];
        long generation;
        try
        {
            await _stateGate.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                var authChanged = ApplyAuthReadLocked(_authReader.Read(), now, timestamp);
                var cacheChanged = RefreshCacheLocked(now, timestamp);
                if (authChanged || cacheChanged) InvalidatePublicationLocked();
                _activePublishStop = publication;
                generation = _publicationGeneration;

                for (var i = 0; i < _descriptors.Length; i++)
                {
                    var point = _points[i];
                    var hasValue = point.Value.HasValue;
                    snapshots[i] = new MetricSnapshot(_descriptors[i].Id, point.Value, _observedAt[i], point.State,
                        hasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                        source: Source, errorCode: point.ErrorCode);
                }
            }
            finally { _stateGate.Release(); }

            foreach (var snapshot in snapshots)
            {
                await _stateGate.WaitAsync(publication.Token).ConfigureAwait(false);
                try
                {
                    if (generation != _publicationGeneration || !ReferenceEquals(_activePublishStop, publication))
                        return;
                }
                finally { _stateGate.Release(); }

                await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!admission.IsCancellationRequested && publication.IsCancellationRequested)
        {
        }
        finally
        {
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null;
            }
            finally { _stateGate.Release(); }
            _publishLock.Release();
        }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var publishLockAcquired = false;
        var stateLockAcquired = false;
        try
        {
            await _publishLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            publishLockAcquired = true;
            await _stateGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            stateLockAcquired = true;
            _activeRequestStop?.Cancel();
            ClearForStoppedLocked(_timeProvider.GetUtcNow());
            var snapshots = _descriptors.Select((descriptor, i) => new MetricSnapshot(descriptor.Id, null,
                _observedAt[i], MetricState.Stopped, MetricConfidence.Unknown, source: Source)).ToArray();
            _stateGate.Release();
            stateLockAcquired = false;

            foreach (var snapshot in snapshots)
                await _bus.PublishAsync(snapshot, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            if (stateLockAcquired) _stateGate.Release();
            if (publishLockAcquired) _publishLock.Release();
        }
    }

    private void SignalPublication()
    {
        if (Volatile.Read(ref _disposeStarted) != 0) return;
        if (Interlocked.Exchange(ref _publishSignaled, 1) != 0) return;
        try { _publishSignal.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private async Task<TransportResult> SendRequestAsync(DevinCliCredential credential,
        CancellationToken ownerToken)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PlanStatusEndpoint)
            {
                Content = new ByteArrayContent("{}"u8.ToArray())
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Headers.TryAddWithoutValidation("x-auth-token", credential.ApiKey);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != PlanStatusEndpoint ||
                (int)response.StatusCode is >= 300 and < 400)
                return TransportResult.Failure("redirect_rejected");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return TransportResult.Failure("auth_required");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return TransportResult.Failure("auth_forbidden");
            if ((int)response.StatusCode == 429)
                return TransportResult.Failure("rate_limited", ReadRetryAfter(response, _timeProvider.GetUtcNow()));
            if (!response.IsSuccessStatusCode)
                return TransportResult.Failure((int)response.StatusCode >= 500
                    ? "upstream_unavailable"
                    : "http_failure");

            var read = await ReadResponseBodyAsync(response, linked.Token).ConfigureAwait(false);
            return read.TooLarge
                ? TransportResult.Failure("response_too_large")
                : read.Body is null
                    ? TransportResult.Failure("response_invalid")
                    : TransportResult.Success(read.Body);
        }
        catch (OperationCanceledException) when (ownerToken.IsCancellationRequested)
        {
            return TransportResult.CancelledResult();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return TransportResult.Failure("timeout");
        }
        catch (HttpRequestException)
        {
            return TransportResult.Failure("network_unavailable");
        }
        catch (Exception)
        {
            return TransportResult.Failure("network_unavailable");
        }
    }

    private static async Task<(byte[]? Body, bool TooLarge)> ReadResponseBodyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxResponseBytes)
            return (null, true);

        using var output = new System.IO.MemoryStream(Math.Min(MaxResponseBytes, 8192));
        var buffer = new byte[8192];
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) return (output.ToArray(), false);
                if (output.Length + read > MaxResponseBytes) return (null, true);
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment))
                CryptographicOperations.ZeroMemory(segment.AsSpan(0, (int)output.Length));
        }
    }

    private void SetRetryNotBeforeLocked(long timestamp, TimeSpan delay)
    {
        var requested = AddDelay(timestamp, delay);
        _retryNotBeforeTimestamp = Math.Max(_retryNotBeforeTimestamp, requested);
    }

    private void ScheduleNextPollLocked(long timestamp)
    {
        var regular = AddDelay(timestamp, PollInterval);
        _nextPollTimestamp = Math.Max(regular, _retryNotBeforeTimestamp);
    }

    private long AddDelay(long timestamp, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return timestamp;
        var ticks = Math.Ceiling((double)delay.Ticks * _timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond);
        if (!double.IsFinite(ticks) || ticks >= long.MaxValue) return long.MaxValue;
        try { return checked(timestamp + (long)ticks); }
        catch (OverflowException) { return long.MaxValue; }
    }

    private static TimeSpan GetBackoffDelay(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromSeconds(60),
        2 => TimeSpan.FromSeconds(120),
        3 => TimeSpan.FromSeconds(240),
        4 => TimeSpan.FromSeconds(480),
        _ => TimeSpan.FromMinutes(15)
    };

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        try
        {
            var retry = response.Headers.RetryAfter;
            if (retry?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (retry?.Date is DateTimeOffset date) return date <= now ? TimeSpan.Zero : date - now;
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string CredentialGenerationTag(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        byte[]? digest = null;
        try
        {
            digest = HMACSHA256.HashData(_credentialScopeKey, bytes);
            return Convert.ToHexString(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (digest is not null) CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool SamePoint(DevinQuotaPoint left, DevinQuotaPoint right) =>
        left.State == right.State && left.Value.HasValue == right.Value.HasValue &&
        (!left.Value.HasValue || left.Value.Value.Equals(right.Value!.Value)) &&
        StringComparer.Ordinal.Equals(left.ErrorCode, right.ErrorCode);

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = RequestTimeout
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }

        if (_ownsHttpClient) _httpClient.Dispose();
        if (_stateGate.Wait(TimeSpan.FromSeconds(1)))
        {
            try
            {
                _activeRequestStop?.Cancel();
                _activePublishStop?.Cancel();
                _credentialGeneration++;
                _credentialGenerationTag = null;
                _rejectedCredentialGenerationTag = null;
                Array.Clear(_cache);
                Array.Clear(_points);
                Array.Clear(_observedAt);
                CryptographicOperations.ZeroMemory(_credentialScopeKey);
            }
            finally { _stateGate.Release(); }
        }
        else
        {
            CryptographicOperations.ZeroMemory(_credentialScopeKey);
        }
    }

    private sealed record CachedPoint(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp,
        DateTimeOffset? ResetAt, bool ForceStale, string? ErrorCode);

    private sealed record TransportResult(byte[]? Body, string? ErrorCode, TimeSpan? RetryAfter, bool Cancelled)
    {
        public static TransportResult Success(byte[] body) => new(body, null, null, false);
        public static TransportResult Failure(string errorCode, TimeSpan? retryAfter = null) =>
            new(null, errorCode, retryAfter, false);
        public static TransportResult CancelledResult() => new(null, null, null, true);
    }
}
