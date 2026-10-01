#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
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

internal sealed class ZCodeQuotaProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 1024 * 1024;
    internal static readonly Uri ZaiUsageEndpoint = new("https://api.z.ai/api/monitor/usage/quota/limit");
    internal static readonly Uri BigModelUsageEndpoint = new("https://bigmodel.cn/api/monitor/usage/quota/limit");

    private static readonly string[] MetricIds =
    [
        "ai.zcode.coding_plan.level",
        "ai.zcode.coding_plan.five_hour.remaining_percent",
        "ai.zcode.coding_plan.five_hour.reset_at",
        "ai.zcode.coding_plan.five_hour.bucket_metadata",
        "ai.zcode.coding_plan.weekly.remaining_percent",
        "ai.zcode.coding_plan.weekly.reset_at",
        "ai.zcode.coding_plan.weekly.bucket_metadata",
        "ai.zcode.coding_plan.tools.remaining_percent",
        "ai.zcode.coding_plan.tools.reset_at",
        "ai.zcode.coding_plan.tools.bucket_metadata"
    ];
    private static readonly MetricValueKind[] ValueKinds =
    [
        MetricValueKind.String,
        MetricValueKind.Double, MetricValueKind.DateTimeOffset, MetricValueKind.String,
        MetricValueKind.Double, MetricValueKind.DateTimeOffset, MetricValueKind.String,
        MetricValueKind.Double, MetricValueKind.DateTimeOffset, MetricValueKind.String
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly ZCodeQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly ZCodeQuotaPoint[] _points = new ZCodeQuotaPoint[ZCodeQuotaParser.PointCount];
    private readonly CachedPoint?[] _cache = new CachedPoint?[ZCodeQuotaParser.PointCount];
    private readonly DateTimeOffset[] _pointObservedAt = new DateTimeOffset[ZCodeQuotaParser.PointCount];
    private ZCodeQuotaAuthIdentity? _activeAuth;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _rejectedCredentialFingerprint;
    private long _authGeneration;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _rateLimitFailures;
    private DateTimeOffset? _lastUtc;
    private long _lastTimestamp;
    private int _disposeStarted;

    public ZCodeQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new ZCodeQuotaAuthReader(), CreateHttpClient(), TimeProvider.System, ownsHttpClient: true)
    {
    }

    internal ZCodeQuotaProvider(MetricRegistry registry, MetricBus bus, ZCodeQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider)
        : this(registry, bus, authReader, httpClient, timeProvider, ownsHttpClient: false)
    {
    }

    private ZCodeQuotaProvider(MetricRegistry registry, MetricBus bus, ZCodeQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownsHttpClient = ownsHttpClient;
        var names = new[] { "Coding plan level", "5-hour remaining", "5-hour reset", "5-hour bucket metadata",
            "Weekly remaining", "Weekly reset", "Weekly bucket metadata", "Tools remaining", "Tools reset", "Tools bucket metadata" };
        var semantics = new[] { "plan_id", "quota_remaining", "timestamp", "quota_bucket_metadata",
            "quota_remaining", "timestamp", "quota_bucket_metadata", "quota_remaining", "timestamp", "quota_bucket_metadata" };
        var units = new string?[] { null, "%", null, "json", "%", null, "json", "%", null, "json" };
        _descriptors = new MetricDescriptor[MetricIds.Length];
        for (var i = 0; i < _descriptors.Length; i++)
        {
            _descriptors[i] = new MetricDescriptor(new MetricId(MetricIds[i]), $"ZCode {names[i]}",
                semantics[i], ValueKinds[i], unit: units[i]);
            if (!registry.TryRegister(_descriptors[i])) throw new InvalidOperationException("ZCode quota metric registration failed.");
            _points[i] = ZCodeQuotaPoint.Unavailable();
            _pointObservedAt[i] = _timeProvider.GetUtcNow();
        }
        _lastUtc = _timeProvider.GetUtcNow();
        _lastTimestamp = _timeProvider.GetTimestamp();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers = [];
        Exception? runFailure = null;
        Exception? stopFailure = null;
        Exception? drainFailure = null;
        Exception? stoppedPublicationFailure = null;
        try
        {
            await PublishCurrentAsync(stop.Token).ConfigureAwait(false);
            workers = [PublishLoopAsync(stop.Token), MonitorLocalStateLoopAsync(stop.Token), PollLoopAsync(stop.Token)];
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
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
            throw new AggregateException("ZCode quota run and shutdown encountered multiple failures.", failures);
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
            return;
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
    }
    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? requestStop = null;
        byte[]? body = null;
        try
        {
            if (_timeProvider.GetTimestamp() < _nextPollTimestamp) return;
            await _stateGate.WaitAsync(admission.Token).ConfigureAwait(false);
            ZCodeQuotaAuthIdentity? requestAuth;
            long requestGeneration;
            string credentialFingerprint;
            try
            {
                var initialRead = _authReader.Read();
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(now, timestamp);
                ApplyAuthReadLocked(initialRead, now);
                requestAuth = initialRead.Status == ZCodeAuthStatus.Ready ? initialRead.Identity : null;
                if (requestAuth is null)
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }
                credentialFingerprint = Fingerprint(requestAuth.ApiKey);
                if (StringComparer.Ordinal.Equals(_rejectedCredentialFingerprint, credentialFingerprint))
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }
                if (timestamp < _retryNotBeforeTimestamp)
                {
                    ScheduleRetryCheckLocked();
                    return;
                }
                requestGeneration = _authGeneration;
                requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = requestStop;
            }
            finally { _stateGate.Release(); }

            string? failureCode = null;
            var noCacheFailureState = MetricState.Error;
            var ownerCancelled = false;
            TimeSpan? retryAfter = null;
            HttpResponseMessage? response = null;
            using (var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(requestStop.Token, deadline.Token))
            {
                try
                {
                    using var request = CreateRequest(requestAuth);
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                        .ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri != request.RequestUri) failureCode = "redirect_rejected";
                    else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        failureCode = response.StatusCode == HttpStatusCode.Unauthorized ? "auth_required" : "auth_forbidden";
                    else if ((int)response.StatusCode == 429)
                    {
                        failureCode = "rate_limited";
                        retryAfter = ReadRetryAfter(response, _timeProvider.GetUtcNow());
                    }
                    else if ((int)response.StatusCode is >= 300 and < 400) failureCode = "redirect_rejected";
                    else if (!response.IsSuccessStatusCode)
                        failureCode = (int)response.StatusCode >= 500 ? "upstream_unavailable" : "http_failure";
                    else
                    {
                        var read = await ReadResponseBodyAsync(response, linked.Token).ConfigureAwait(false);
                        body = read.Body;
                        if (read.TooLarge) failureCode = "response_too_large";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (requestStop.IsCancellationRequested) { ownerCancelled = true; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { failureCode = "timeout"; }
                catch (HttpRequestException) { failureCode = "offline"; noCacheFailureState = MetricState.Unavailable; }
                catch (IOException) { failureCode = "offline"; noCacheFailureState = MetricState.Unavailable; }
                catch (Exception) { failureCode = "request_failed"; }
                finally { response?.Dispose(); }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();
            var receivedAt = _timeProvider.GetUtcNow();
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var finalRead = _authReader.Read();
                var commitUtc = _timeProvider.GetUtcNow();
                ObserveClockLocked(commitUtc, _timeProvider.GetTimestamp());
                ApplyAuthReadLocked(finalRead, commitUtc);
                var accepted = !ownerCancelled && _authGeneration == requestGeneration &&
                    finalRead.Status == ZCodeAuthStatus.Ready &&
                    StringComparer.Ordinal.Equals(finalRead.Identity!.StableId, requestAuth.StableId) &&
                    StringComparer.Ordinal.Equals(Fingerprint(finalRead.Identity.ApiKey), credentialFingerprint);
                if (!accepted) ScheduleNextPollLocked(PollInterval);
                else if (failureCode is "auth_required" or "auth_forbidden")
                {
                    _rateLimitFailures = 0;
                    _retryNotBeforeTimestamp = 0;
                    _rejectedCredentialFingerprint = credentialFingerprint;
                    ClearToStateLocked(MetricState.Error, failureCode, receivedAt);
                    ScheduleNextPollLocked(PollInterval);
                }
                else if (failureCode == "rate_limited")
                {
                    _rateLimitFailures++;
                    var delay = retryAfter ?? GetBackoffDelay(_rateLimitFailures);
                    SetRetryNotBeforeLocked(delay);
                    PreserveCacheOnFailureLocked("rate_limited", MetricState.Error, receivedAt);
                    ScheduleNextPollLocked(delay);
                }
                else if (failureCode is not null)
                {
                    _rateLimitFailures = 0;
                    if (failureCode is "offline" or "timeout" or "upstream_unavailable")
                        PreserveCacheOnFailureLocked(failureCode, noCacheFailureState, receivedAt);
                    else ClearToStateLocked(MetricState.Error, failureCode, receivedAt);
                    ScheduleNextPollLocked(PollInterval);
                }
                else if (body is null)
                {
                    ClearToStateLocked(MetricState.Error, "response_invalid", receivedAt);
                    ScheduleNextPollLocked(PollInterval);
                }
                else
                {
                    var responseTimestamp = _timeProvider.GetTimestamp();
                    ReplaceFromReadingLocked(ZCodeQuotaParser.Parse(body), receivedAt, responseTimestamp);
                    ExpireCachesLocked(receivedAt, responseTimestamp);
                    _rateLimitFailures = 0;
                    _retryNotBeforeTimestamp = 0;
                    _rejectedCredentialFingerprint = null;
                    ScheduleNextPollLocked(PollInterval);
                }
            }
            finally { _stateGate.Release(); }
        }
        finally
        {
            if (requestStop is not null)
            {
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { if (ReferenceEquals(_activeRequestStop, requestStop)) _activeRequestStop = null; }
                finally { _stateGate.Release(); }
                requestStop.Dispose();
            }
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            _pollLock.Release();
        }
    }
    internal async ValueTask ObserveLocalStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var authRead = _authReader.Read();
            var now = _timeProvider.GetUtcNow();
            var timestamp = _timeProvider.GetTimestamp();
            ObserveClockLocked(now, timestamp);
            ApplyAuthReadLocked(authRead, now);
            ExpireCachesLocked(now, timestamp);
        }
        finally { _stateGate.Release(); }
    }

    internal async ValueTask PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _publishLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? publication = null;
        try
        {
            publication = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
            var snapshots = new MetricSnapshot[_descriptors.Length];
            await _stateGate.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                var authRead = _authReader.Read();
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(now, timestamp);
                ApplyAuthReadLocked(authRead, now);
                ExpireCachesLocked(now, timestamp);
                _activePublishStop?.Cancel();
                _activePublishStop = publication;
                for (var i = 0; i < _descriptors.Length; i++)
                {
                    var cached = _cache[i];
                    var point = cached is null ? _points[i] : EvaluateCache(cached, timestamp);
                    var observedAt = cached?.ObservedAt ?? _pointObservedAt[i];
                    snapshots[i] = new MetricSnapshot(_descriptors[i].Id, point.Value, observedAt, point.State,
                        point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                        source: "ZCode.CodingPlan.ReadOnly.v1", errorCode: point.ErrorCode);
                }
            }
            finally { _stateGate.Release(); }

            try
            {
                foreach (var snapshot in snapshots)
                    await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!admission.IsCancellationRequested && publication.IsCancellationRequested)
            {
                return;
            }
        }
        finally
        {
            if (publication is not null)
            {
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null; }
                finally { _stateGate.Release(); }
                publication.Dispose();
            }
            _publishLock.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            var delay = await GetPollDelayAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay,
                _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorLocalStateLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ObserveLocalStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TimeSpan> GetPollDelayAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ticks = Math.Max(0, _nextPollTimestamp - _timeProvider.GetTimestamp());
            return TimeSpan.FromSeconds((double)ticks / _timeProvider.TimestampFrequency);
        }
        finally { _stateGate.Release(); }
    }

    private void ApplyAuthReadLocked(ZCodeAuthRead read, DateTimeOffset observedAt)
    {
        if (read.Status != ZCodeAuthStatus.Ready || read.Identity is null)
        {
            if (_activeAuth is not null)
            {
                InvalidateOwnerWorkLocked();
                ClearCacheLocked(observedAt);
                _rateLimitFailures = 0;
                _retryNotBeforeTimestamp = 0;
            }
            _activeAuth = null;
            _rejectedCredentialFingerprint = null;
            var state = read.Status == ZCodeAuthStatus.Unavailable ? MetricState.Unavailable : MetricState.Error;
            ClearToStateLocked(state, state == MetricState.Error ? read.ErrorCode ?? "auth_unavailable" : null, observedAt);
            return;
        }

        var changedOwner = _activeAuth is not null &&
            !StringComparer.Ordinal.Equals(_activeAuth.StableId, read.Identity.StableId);
        var changedCredential = _activeAuth is not null &&
            !StringComparer.Ordinal.Equals(_activeAuth.ApiKey, read.Identity.ApiKey);
        if (changedOwner)
        {
            InvalidateOwnerWorkLocked();
            ClearCacheLocked(observedAt);
            ClearToStateLocked(MetricState.Unavailable, null, observedAt);
            _rejectedCredentialFingerprint = null;
            _rateLimitFailures = 0;
            _retryNotBeforeTimestamp = 0;
        }
        else if (changedCredential)
        {
            InvalidateOwnerWorkLocked();
            _rateLimitFailures = 0;
            _retryNotBeforeTimestamp = 0;
            _rejectedCredentialFingerprint = null;
        }
        _activeAuth = read.Identity;
    }

    private void InvalidateOwnerWorkLocked()
    {
        _authGeneration++;
        _activeRequestStop?.Cancel();
        _activePublishStop?.Cancel();
    }

    private void ReplaceFromReadingLocked(ZCodeQuotaReading reading, DateTimeOffset observedAt, long timestamp)
    {
        _activePublishStop?.Cancel();
        for (var i = 0; i < _points.Length; i++)
        {
            var point = reading.Points[i];
            _points[i] = point;
            _pointObservedAt[i] = observedAt;
            _cache[i] = point.State == MetricState.Fresh && point.Value.HasValue
                ? new CachedPoint(point.Value.Value, point.ErrorCode, observedAt, timestamp)
                : null;
        }
    }
    private void PreserveCacheOnFailureLocked(string errorCode, MetricState noCacheState, DateTimeOffset observedAt)
    {
        _activePublishStop?.Cancel();
        for (var i = 0; i < _points.Length; i++)
        {
            if (_cache[i] is CachedPoint cached)
            {
                _cache[i] = cached with { ForceStale = true, ErrorCode = errorCode };
                continue;
            }
            _points[i] = noCacheState == MetricState.Unavailable
                ? ZCodeQuotaPoint.Unavailable()
                : ZCodeQuotaPoint.Error(errorCode);
            _pointObservedAt[i] = observedAt;
        }
    }

    private void ClearToStateLocked(MetricState state, string? errorCode, DateTimeOffset observedAt)
    {
        _activePublishStop?.Cancel();
        for (var i = 0; i < _points.Length; i++)
        {
            _cache[i] = null;
            _points[i] = state switch
            {
                MetricState.Error => ZCodeQuotaPoint.Error(errorCode ?? "auth_unavailable"),
                MetricState.Stopped => new ZCodeQuotaPoint(MetricState.Stopped, null, null),
                _ => ZCodeQuotaPoint.Unavailable()
            };
            _pointObservedAt[i] = observedAt;
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
            var now = _timeProvider.GetUtcNow();
            _activeAuth = null;
            _activeRequestStop?.Cancel();
            _activePublishStop?.Cancel();
            _rejectedCredentialFingerprint = null;
            _retryNotBeforeTimestamp = 0;
            _rateLimitFailures = 0;
            ClearToStateLocked(MetricState.Stopped, null, now);
            var snapshots = _descriptors.Select(descriptor => new MetricSnapshot(descriptor.Id, null, now,
                MetricState.Stopped, MetricConfidence.Unknown, source: "ZCode.CodingPlan.ReadOnly.v1")).ToArray();
            _stateGate.Release();
            stateLockAcquired = false;
            await Task.WhenAll(snapshots.Select(snapshot => _bus.PublishAsync(snapshot, deadline.Token).AsTask()))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return;
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (stateLockAcquired) _stateGate.Release();
            if (publishLockAcquired) _publishLock.Release();
        }
    }

    private void ClearCacheLocked(DateTimeOffset observedAt)
    {
        _activePublishStop?.Cancel();
        for (var i = 0; i < _cache.Length; i++)
        {
            _cache[i] = null;
            _points[i] = ZCodeQuotaPoint.Unavailable();
            _pointObservedAt[i] = observedAt;
        }
    }

    private void ObserveClockLocked(DateTimeOffset utcNow, long timestamp)
    {
        if (_lastUtc is DateTimeOffset previousUtc)
        {
            var monotonic = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
            var utcElapsed = utcNow - previousUtc;
            if (utcElapsed < TimeSpan.Zero || Math.Abs((utcElapsed - monotonic).TotalSeconds) > 2)
                ClearCacheLocked(utcNow);
        }
        _lastUtc = utcNow;
        _lastTimestamp = timestamp;
    }

    private void ExpireCachesLocked(DateTimeOffset utcNow, long timestamp)
    {
        ExpireWindowLocked(ZCodeQuotaParser.FiveHourRemaining, ZCodeQuotaParser.FiveHourReset,
            ZCodeQuotaParser.FiveHourMetadata, utcNow, timestamp);
        ExpireWindowLocked(ZCodeQuotaParser.WeeklyRemaining, ZCodeQuotaParser.WeeklyReset,
            ZCodeQuotaParser.WeeklyMetadata, utcNow, timestamp);
        ExpireWindowLocked(ZCodeQuotaParser.ToolsRemaining, ZCodeQuotaParser.ToolsReset,
            ZCodeQuotaParser.ToolsMetadata, utcNow, timestamp);
        if (_cache[ZCodeQuotaParser.PlanId] is CachedPoint plan &&
            _timeProvider.GetElapsedTime(plan.Timestamp, timestamp) >= MaximumCacheAge)
        {
            _cache[ZCodeQuotaParser.PlanId] = null;
            _points[ZCodeQuotaParser.PlanId] = ZCodeQuotaPoint.Unavailable("cache_expired");
            _pointObservedAt[ZCodeQuotaParser.PlanId] = utcNow;
            _activePublishStop?.Cancel();
        }
    }

    private void ExpireWindowLocked(int remainingIndex, int resetIndex, int metadataIndex,
        DateTimeOffset utcNow, long timestamp)
    {
        var resetAt = _cache[resetIndex] is CachedPoint reset ? reset.Value.AsDateTimeOffset() : (DateTimeOffset?)null;
        var resetElapsed = resetAt.HasValue && utcNow >= resetAt.Value;
        var indices = new[] { remainingIndex, resetIndex, metadataIndex };
        var tooOld = indices.Any(i => _cache[i] is CachedPoint cached &&
            _timeProvider.GetElapsedTime(cached.Timestamp, timestamp) >= MaximumCacheAge);
        if (!resetElapsed && !tooOld) return;
        _activePublishStop?.Cancel();
        var code = resetElapsed ? "reset_elapsed" : "cache_expired";
        foreach (var i in indices)
        {
            _cache[i] = null;
            _points[i] = ZCodeQuotaPoint.Unavailable(code);
            _pointObservedAt[i] = utcNow;
        }
    }

    private ZCodeQuotaPoint EvaluateCache(CachedPoint cached, long timestamp)
    {
        var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
        return cached.ForceStale || age >= StaleAfter
            ? new ZCodeQuotaPoint(MetricState.Stale, cached.Value, cached.ErrorCode)
            : new ZCodeQuotaPoint(MetricState.Fresh, cached.Value, cached.ErrorCode);
    }

    private void ScheduleNextPollLocked(TimeSpan delay)
    {
        if (delay < PollInterval) delay = PollInterval;
        var now = _timeProvider.GetTimestamp();
        var delta = ToTimestampTicks(delay);
        _nextPollTimestamp = delta > long.MaxValue - now ? long.MaxValue : now + delta;
    }

    private void SetRetryNotBeforeLocked(TimeSpan delay)
    {
        var now = _timeProvider.GetTimestamp();
        var delta = ToTimestampTicks(delay);
        _retryNotBeforeTimestamp = delta > long.MaxValue - now ? long.MaxValue : now + delta;
    }

    private void ScheduleRetryCheckLocked()
    {
        var now = _timeProvider.GetTimestamp();
        var delta = ToTimestampTicks(PollInterval);
        var regularAt = delta > long.MaxValue - now ? long.MaxValue : now + delta;
        _nextPollTimestamp = Math.Min(_retryNotBeforeTimestamp, regularAt);
    }

    private long ToTimestampTicks(TimeSpan delay)
    {
        var ticks = delay.TotalSeconds * _timeProvider.TimestampFrequency;
        return !double.IsFinite(ticks) || ticks >= long.MaxValue ? long.MaxValue : Math.Max(0, (long)ticks);
    }

    private static TimeSpan GetBackoffDelay(int failures)
    {
        var seconds = Math.Min(60d * Math.Pow(2, Math.Min(failures - 1, 20)), TimeSpan.FromMinutes(15).TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
    private static HttpRequestMessage CreateRequest(ZCodeQuotaAuthIdentity auth)
    {
        var builder = new UriBuilder(GetEndpoint(auth.Family));
        if (auth.OrganizationId is not null && auth.ProjectId is not null) builder.Query = "type=2";
        var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.TryAddWithoutValidation("Authorization", auth.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (auth.OrganizationId is not null && auth.ProjectId is not null)
        {
            request.Headers.TryAddWithoutValidation("bigmodel-organization", auth.OrganizationId);
            request.Headers.TryAddWithoutValidation("bigmodel-project", auth.ProjectId);
        }
        return request;
    }

    private static Uri GetEndpoint(string family) => family switch
    {
        "zai" => ZaiUsageEndpoint,
        "bigmodel" => BigModelUsageEndpoint,
        _ => throw new InvalidOperationException("Unsupported ZCode provider family.")
    };

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<(byte[]? Body, bool TooLarge)> ReadResponseBodyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) return (null, true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var output = new MemoryStream(8192);
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaxResponseBytes) return (null, true);
                output.Write(buffer, 0, read);
            }
            return (output.ToArray(), false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is TimeSpan delta && delta > TimeSpan.Zero) return delta;
        if (retry?.Date is DateTimeOffset date && date > now) return date - now;
        return null;
    }

    private static string Fingerprint(string credential)
    {
        var bytes = Encoding.UTF8.GetBytes(credential);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _shutdown.Cancel();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record CachedPoint(MetricValue Value, string? ErrorCode, DateTimeOffset ObservedAt,
        long Timestamp, bool ForceStale = false);
}
