#nullable enable

using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal sealed class CodexQuotaProvider : IDisposable
{
    internal static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 1024 * 1024;

    private static readonly string[] MetricIds =
    [
        "ai.codex.quota.primary.remaining",
        "ai.codex.quota.secondary.remaining",
        "ai.codex.quota.primary.reset",
        "ai.codex.quota.secondary.reset",
        "ai.codex.quota.primary.duration",
        "ai.codex.quota.secondary.duration"
    ];

    private static readonly MetricValueKind[] ValueKinds =
    [
        MetricValueKind.Double, MetricValueKind.Double,
        MetricValueKind.DateTimeOffset, MetricValueKind.DateTimeOffset,
        MetricValueKind.TimeSpan, MetricValueKind.TimeSpan
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly CodexQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, int> _jitter;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateAndPublish = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly CodexQuotaPoint[] _points = new CodexQuotaPoint[6];
    private readonly CachedPoint?[] _cache = new CachedPoint?[6];
    private readonly DateTimeOffset[] _pointObservedAt = new DateTimeOffset[6];
    private CodexAuthIdentity? _activeAuth;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _rejectedToken;
    private long _authGeneration;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _rateLimitFailures;
    private DateTimeOffset? _lastUtc;
    private long _lastTimestamp;
    private bool _disposed;
    private int _disposeStarted;

    public CodexQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new CodexQuotaAuthReader(), CreateHttpClient(), TimeProvider.System,
            static maximum => Random.Shared.Next(maximum + 1), ownsHttpClient: true)
    {
    }

    internal CodexQuotaProvider(MetricRegistry registry, MetricBus bus, CodexQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, Func<int, int>? jitter = null)
        : this(registry, bus, authReader, httpClient, timeProvider, jitter ?? (static _ => 0), ownsHttpClient: false)
    {
    }

    private CodexQuotaProvider(MetricRegistry registry, MetricBus bus, CodexQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, Func<int, int> jitter, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _ownsHttpClient = ownsHttpClient;
        var labels = new[] { "Primary", "Secondary", "Primary", "Secondary", "Primary", "Secondary" };
        var names = new[] { "Quota Remaining", "Quota Remaining", "Reset At", "Reset At", "Window Duration", "Window Duration" };
        var semanticTypes = new[] { "quota_remaining", "quota_remaining", "timestamp", "timestamp", "duration", "duration" };
        var units = new string?[] { "%", "%", null, null, null, null };
        _descriptors = new MetricDescriptor[MetricIds.Length];
        for (var i = 0; i < _descriptors.Length; i++)
        {
            _descriptors[i] = new MetricDescriptor(new MetricId(MetricIds[i]), $"Codex {labels[i]} {names[i]}",
                semanticTypes[i], ValueKinds[i], unit: units[i]);
            if (!registry.TryRegister(_descriptors[i]))
                throw new InvalidOperationException("Codex quota metric registration failed.");
            _points[i] = CodexQuotaPoint.Unavailable();
            _pointObservedAt[i] = _timeProvider.GetUtcNow();
        }
        _lastUtc = _timeProvider.GetUtcNow();
        _lastTimestamp = _timeProvider.GetTimestamp();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers = [];
        try
        {
            await PublishCurrentAsync(stop.Token).ConfigureAwait(false);
            workers =
            [
                PublishLoopAsync(stop.Token),
                MonitorLocalStateLoopAsync(stop.Token),
                PollLoopAsync(stop.Token)
            ];
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            try
            {
                try { await Task.WhenAll(workers).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            finally { await PublishStoppedAsync().ConfigureAwait(false); }
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? requestStop = null;
        byte[]? body = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var initialTimestamp = _timeProvider.GetTimestamp();
            if (initialTimestamp < _nextPollTimestamp) return;

            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            CodexAuthIdentity? requestAuth;
            long requestGeneration;
            try
            {
                var initialRead = _authReader.Read();
                ObserveClockLocked(_timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
                ApplyAuthReadLocked(initialRead, _timeProvider.GetUtcNow());
                requestAuth = initialRead.Status == CodexAuthStatus.Ready ? initialRead.Identity : null;
                if (requestAuth is null || (_rejectedToken is not null &&
                    StringComparer.Ordinal.Equals(_rejectedToken, requestAuth.AccessToken)))
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }
                if (_timeProvider.GetTimestamp() < _retryNotBeforeTimestamp)
                {
                    ScheduleRetryCheckLocked();
                    return;
                }
                requestGeneration = _authGeneration;
                requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = requestStop;
            }
            finally { _stateAndPublish.Release(); }

            var startedTimestamp = _timeProvider.GetTimestamp();
            HttpResponseMessage? response = null;
            var failureCode = (string?)null;
            var noCacheFailureState = MetricState.Error;
            var ownerCancelled = false;
            TimeSpan? retryAfter = null;

            using (var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(requestStop.Token, deadline.Token))
            {
                try
                {
                    using var request = CreateRequest(requestAuth);
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        linked.Token).ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri != UsageEndpoint)
                        failureCode = "redirect_rejected";
                    else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        failureCode = response.StatusCode == HttpStatusCode.Unauthorized ? "auth_required" : "auth_forbidden";
                    else if ((int)response.StatusCode == 429)
                    {
                        failureCode = "rate_limited";
                        retryAfter = ReadRetryAfter(response, _timeProvider.GetUtcNow());
                    }
                    else if ((int)response.StatusCode is >= 300 and < 400)
                        failureCode = "redirect_rejected";
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
                catch (OperationCanceledException) when (requestStop.IsCancellationRequested)
                {
                    ownerCancelled = true;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    failureCode = "timeout";
                }
                catch (HttpRequestException) { failureCode = "offline"; noCacheFailureState = MetricState.Unavailable; }
                catch (IOException) { failureCode = "offline"; noCacheFailureState = MetricState.Unavailable; }
                catch (Exception) { failureCode = "request_failed"; }
                finally { response?.Dispose(); }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();
            var receivedAt = _timeProvider.GetUtcNow();
            var elapsed = _timeProvider.GetElapsedTime(startedTimestamp, _timeProvider.GetTimestamp());
            var acceptResult = false;
            await _stateAndPublish.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var finalRead = _authReader.Read();
                var commitUtc = _timeProvider.GetUtcNow();
                ObserveClockLocked(commitUtc, _timeProvider.GetTimestamp());
                ApplyAuthReadLocked(finalRead, commitUtc);
                acceptResult = !ownerCancelled && _authGeneration == requestGeneration &&
                    finalRead.Status == CodexAuthStatus.Ready &&
                    StringComparer.Ordinal.Equals(finalRead.Identity!.StableId, requestAuth.StableId);

                if (!acceptResult)
                {
                    ScheduleNextPollLocked(PollInterval);
                }
                else if (failureCode is "auth_required" or "auth_forbidden")
                {
                    _rateLimitFailures = 0;
                    _retryNotBeforeTimestamp = 0;
                    _rejectedToken = requestAuth.AccessToken;
                    ClearToStateLocked(MetricState.Error, failureCode, receivedAt);
                    ScheduleNextPollLocked(PollInterval);
                }
                else if (failureCode == "rate_limited")
                {
                    _rateLimitFailures++;
                    var delay = retryAfter ?? GetBackoffDelay(_rateLimitFailures);
                    SetRetryNotBeforeLocked(delay);
                    PreserveCacheOnFailureLocked("rate_limited", MetricState.Error, receivedAt);
                    ScheduleNextPollLocked(PollInterval);
                }
                else if (failureCode is not null)
                {
                    if (failureCode != "rate_limited") _rateLimitFailures = 0;
                    if (failureCode is "offline" or "timeout" or "upstream_unavailable")
                        PreserveCacheOnFailureLocked(failureCode, noCacheFailureState, receivedAt);
                    else
                        ClearToStateLocked(MetricState.Error, failureCode, receivedAt);
                    ScheduleNextPollLocked(PollInterval);
                }
                else if (body is null)
                {
                    ClearToStateLocked(MetricState.Error, "response_invalid", receivedAt);
                    ScheduleNextPollLocked(PollInterval);
                }
                else
                {
                    var reading = CodexQuotaParser.Parse(body, requestAuth.AccountId, requestAuth.UserId, receivedAt, elapsed);
                    ReplaceFromReadingLocked(reading, receivedAt, _timeProvider.GetTimestamp());
                    _rateLimitFailures = 0;
                    _retryNotBeforeTimestamp = 0;
                    ScheduleNextPollLocked(PollInterval);
                }
            }
            finally
            {
                _stateAndPublish.Release();
            }
        }
        finally
        {
            if (requestStop is not null)
            {
                await _stateAndPublish.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activeRequestStop, requestStop)) _activeRequestStop = null;
                }
                finally { _stateAndPublish.Release(); }
                requestStop.Dispose();
            }
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            _pollLock.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            var wait = await GetPollDelayAsync(cancellationToken).ConfigureAwait(false);
            var chunk = wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait;
            await Task.Delay(chunk, _timeProvider, cancellationToken).ConfigureAwait(false);
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

    internal async ValueTask ObserveLocalStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _stateAndPublish.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var authRead = _authReader.Read();
            var now = _timeProvider.GetUtcNow();
            ObserveClockLocked(now, _timeProvider.GetTimestamp());
            ApplyAuthReadLocked(authRead, now);
            ExpireCachesLocked(now, _timeProvider.GetTimestamp());
        }
        finally { _stateAndPublish.Release(); }
    }

    internal async ValueTask PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _publishLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? publication = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            publication = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
            var snapshots = new MetricSnapshot[_descriptors.Length];
            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
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
                    var hasValue = point.Value.HasValue;
                    snapshots[i] = new MetricSnapshot(_descriptors[i].Id, point.Value, observedAt, point.State,
                        hasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                        source: "Codex.Usage.ReadOnly.v1", errorCode: point.ErrorCode);
                }
            }
            finally { _stateAndPublish.Release(); }

            try
            {
                foreach (var snapshot in snapshots)
                    await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
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
                await _stateAndPublish.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null;
                }
                finally { _stateAndPublish.Release(); }
                publication.Dispose();
            }
            _publishLock.Release();
        }
    }

    private async ValueTask<TimeSpan> GetPollDelayAsync(CancellationToken cancellationToken)
    {
        await _stateAndPublish.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetTimestamp();
            var ticks = Math.Max(0, _nextPollTimestamp - now);
            return TimeSpan.FromSeconds((double)ticks / _timeProvider.TimestampFrequency);
        }
        finally { _stateAndPublish.Release(); }
    }

    private void ApplyAuthReadLocked(CodexAuthRead read, DateTimeOffset observedAt)
    {
        if (read.Status != CodexAuthStatus.Ready || read.Identity is null)
        {
            if (_activeAuth is not null)
            {
                InvalidateOwnerWorkLocked();
                ClearCacheLocked(observedAt);
                _rateLimitFailures = 0;
                _retryNotBeforeTimestamp = 0;
            }
            _activeAuth = null;
            _rejectedToken = null;
            var state = read.Status == CodexAuthStatus.Unavailable ? MetricState.Unavailable : MetricState.Error;
            ClearToStateLocked(state, state == MetricState.Error ? read.ErrorCode ?? "auth_unavailable" : null, observedAt);
            return;
        }

        var changedOwner = _activeAuth is not null &&
            !StringComparer.Ordinal.Equals(_activeAuth.StableId, read.Identity.StableId);
        var changedToken = _activeAuth is not null &&
            !StringComparer.Ordinal.Equals(_activeAuth.AccessToken, read.Identity.AccessToken);
        if (changedOwner)
        {
            InvalidateOwnerWorkLocked();
            ClearCacheLocked(observedAt);
            ClearToStateLocked(MetricState.Unavailable, null, observedAt);
            _rejectedToken = null;
            _rateLimitFailures = 0;
            _retryNotBeforeTimestamp = 0;
        }
        else if (changedToken)
        {
            InvalidateOwnerWorkLocked();
            _rateLimitFailures = 0;
            _retryNotBeforeTimestamp = 0;
            if (_rejectedToken is not null &&
                !StringComparer.Ordinal.Equals(_rejectedToken, read.Identity.AccessToken))
            {
                _rejectedToken = null;
                ClearToStateLocked(MetricState.Unavailable, null, observedAt);
            }
        }
        _activeAuth = read.Identity;
    }

    private void InvalidateOwnerWorkLocked()
    {
        _authGeneration++;
        _activeRequestStop?.Cancel();
        _activePublishStop?.Cancel();
    }

    private void ReplaceFromReadingLocked(CodexQuotaReading reading, DateTimeOffset observedAt, long timestamp)
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
                _cache[i] = cached with
                {
                    ForceStale = true,
                    ErrorCode = cached.ErrorCode == "source_disagreement" ? cached.ErrorCode : errorCode
                };
                continue;
            }
            _points[i] = noCacheState == MetricState.Unavailable
                ? CodexQuotaPoint.Unavailable()
                : CodexQuotaPoint.Error(errorCode);
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
                MetricState.Error => CodexQuotaPoint.Error(errorCode ?? "auth_unavailable"),
                MetricState.Stopped => new CodexQuotaPoint(MetricState.Stopped, null, null),
                _ => CodexQuotaPoint.Unavailable()
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
            await _stateAndPublish.WaitAsync(deadline.Token).ConfigureAwait(false);
            stateLockAcquired = true;

            var now = _timeProvider.GetUtcNow();
            _activeAuth = null;
            _activeRequestStop = null;
            _rejectedToken = null;
            _retryNotBeforeTimestamp = 0;
            _rateLimitFailures = 0;
            ClearToStateLocked(MetricState.Stopped, null, now);
            var snapshots = _descriptors.Select(descriptor => new MetricSnapshot(descriptor.Id, null, now,
                MetricState.Stopped, MetricConfidence.Unknown, source: "Codex.Usage.ReadOnly.v1")).ToArray();

            _stateAndPublish.Release();
            stateLockAcquired = false;
            await Task.WhenAll(snapshots.Select(snapshot => _bus.PublishAsync(snapshot, deadline.Token).AsTask()))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally
        {
            if (stateLockAcquired) _stateAndPublish.Release();
            if (publishLockAcquired) _publishLock.Release();
        }
    }

    private void ClearCacheLocked(DateTimeOffset observedAt)
    {
        _activePublishStop?.Cancel();
        for (var i = 0; i < _cache.Length; i++)
        {
            _cache[i] = null;
            _points[i] = CodexQuotaPoint.Unavailable();
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
        ExpireFamilyLocked(CodexQuotaParser.PrimaryRemaining, CodexQuotaParser.PrimaryReset,
            CodexQuotaParser.PrimaryDuration, utcNow, timestamp);
        ExpireFamilyLocked(CodexQuotaParser.SecondaryRemaining, CodexQuotaParser.SecondaryReset,
            CodexQuotaParser.SecondaryDuration, utcNow, timestamp);
    }

    private void ExpireFamilyLocked(int remainingIndex, int resetIndex, int durationIndex,
        DateTimeOffset utcNow, long timestamp)
    {
        var points = new[] { remainingIndex, resetIndex, durationIndex };
        var resetAt = _cache[resetIndex] is CachedPoint resetCache
            ? resetCache.Value.AsDateTimeOffset()
            : (DateTimeOffset?)null;
        var resetElapsed = resetAt.HasValue && utcNow >= resetAt.Value;
        var tooOld = false;
        foreach (var i in points)
            if (_cache[i] is CachedPoint cached &&
                _timeProvider.GetElapsedTime(cached.Timestamp, timestamp) >= MaximumCacheAge)
                tooOld = true;
        if (!resetElapsed && !tooOld) return;

        _activePublishStop?.Cancel();
        var code = resetElapsed ? "reset_elapsed" : "cache_expired";
        foreach (var i in points)
        {
            _cache[i] = null;
            _points[i] = CodexQuotaPoint.Unavailable(code);
            _pointObservedAt[i] = utcNow;
        }
    }

    private CodexQuotaPoint EvaluateCache(CachedPoint cached, long timestamp)
    {
        var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
        return cached.ForceStale || age >= StaleAfter
            ? new CodexQuotaPoint(MetricState.Stale, cached.Value, cached.ErrorCode)
            : new CodexQuotaPoint(MetricState.Fresh, cached.Value, cached.ErrorCode);
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
        var regularCheck = ToTimestampTicks(PollInterval);
        var nextRegularCheck = regularCheck > long.MaxValue - now ? long.MaxValue : now + regularCheck;
        _nextPollTimestamp = Math.Min(_retryNotBeforeTimestamp, nextRegularCheck);
    }

    private long ToTimestampTicks(TimeSpan delay) =>
        checked((long)(delay.TotalSeconds * _timeProvider.TimestampFrequency));

    private TimeSpan GetBackoffDelay(int failures)
    {
        var seconds = 60d * Math.Pow(2, Math.Min(failures - 1, 20));
        seconds = Math.Min(seconds, TimeSpan.FromMinutes(15).TotalSeconds);
        var jitterCeiling = (int)Math.Min(15, Math.Min(Math.Floor(seconds / 10),
            TimeSpan.FromMinutes(15).TotalSeconds - seconds));
        var jitter = _jitter(jitterCeiling);
        if (jitter < 0 || jitter > jitterCeiling) jitter = Math.Clamp(jitter, 0, jitterCeiling);
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static HttpRequestMessage CreateRequest(CodexAuthIdentity auth)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.UserAgent.ParseAdd("codex-cli");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = RequestTimeout,
            MaxResponseHeadersLength = 16
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<(byte[]? Body, bool TooLarge)> ReadResponseBodyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) return (null, true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxResponseBytes + 1);
        try
        {
            var count = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count, MaxResponseBytes + 1 - count), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                count += read;
                if (count > MaxResponseBytes) return (null, true);
            }
            var body = new byte[count];
            buffer.AsSpan(0, count).CopyTo(body);
            return (body, false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        try
        {
            var retry = response.Headers.RetryAfter;
            if (retry?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (retry?.Date is DateTimeOffset date) return date <= now ? TimeSpan.Zero : date - now;
            return null;
        }
        catch (FormatException) { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        _shutdown.Cancel();
        _pollLock.Wait();
        _publishLock.Wait();
        _stateAndPublish.Wait();
        try
        {
            _activeAuth = null;
            _activeRequestStop = null;
            _activePublishStop = null;
            _rejectedToken = null;
            _retryNotBeforeTimestamp = 0;
            _rateLimitFailures = 0;
            for (var i = 0; i < _cache.Length; i++)
            {
                _cache[i] = null;
                _points[i] = CodexQuotaPoint.Unavailable();
                _pointObservedAt[i] = default;
            }
        }
        finally
        {
            _stateAndPublish.Release();
            _publishLock.Release();
            _pollLock.Release();
        }
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record CachedPoint(MetricValue Value, string? ErrorCode, DateTimeOffset ObservedAt, long Timestamp,
        bool ForceStale = false);
}
