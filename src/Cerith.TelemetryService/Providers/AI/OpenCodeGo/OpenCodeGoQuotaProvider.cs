#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal sealed class OpenCodeGoQuotaProvider : IDisposable
{
    internal static readonly Uri UsageEndpoint = new("https://opencode.ai/zen/go/v1/usage");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 128 * 1024;
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StoppedPublishTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkerDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClockDiscrepancyTolerance = TimeSpan.FromSeconds(2);

    private static readonly (string Id, string Name, string SemanticType, MetricValueKind Kind, string? Unit)[] Definitions =
    [
        ("ai.opencode-go.quota.rolling.used", "Rolling Usage", "quota_used", MetricValueKind.Double, "%"),
        ("ai.opencode-go.quota.weekly.used", "Weekly Usage", "quota_used", MetricValueKind.Double, "%"),
        ("ai.opencode-go.quota.monthly.used", "Monthly Usage", "quota_used", MetricValueKind.Double, "%"),
        ("ai.opencode-go.quota.rolling.reset", "Rolling Reset At", "timestamp", MetricValueKind.DateTimeOffset, null),
        ("ai.opencode-go.quota.weekly.reset", "Weekly Reset At", "timestamp", MetricValueKind.DateTimeOffset, null),
        ("ai.opencode-go.quota.monthly.reset", "Monthly Reset At", "timestamp", MetricValueKind.DateTimeOffset, null)
    ];

    private readonly object _stateLock = new();
    private readonly object _authReadLock = new();
    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly OpenCodeGoAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, int> _jitter;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly OpenCodeGoQuotaPoint[] _points = new OpenCodeGoQuotaPoint[6];
    private readonly CachedPoint?[] _cache = new CachedPoint?[6];
    private readonly DateTimeOffset[] _pointObservedAt = new DateTimeOffset[6];
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _activeKeyFingerprint;
    private string? _activeAuthSourceFingerprint;
    private string? _rejectedKeyFingerprint;
    private long _authGeneration;
    private long _snapshotGeneration;
    private long _nextPollTimestamp = long.MinValue;
    private long _retryNotBeforeTimestamp = long.MinValue;
    private int _rateLimitFailures;
    private DateTimeOffset? _lastUtc;
    private long _lastTimestamp;
    private bool _runActive;
    private bool _runStopped;
    private bool _disposed;

    public OpenCodeGoQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new OpenCodeGoAuthReader(), CreateHttpClient(), TimeProvider.System)
    {
    }

    internal OpenCodeGoQuotaProvider(MetricRegistry registry, MetricBus bus, OpenCodeGoAuthReader authReader,
        HttpMessageHandler handler, TimeProvider timeProvider, Func<int, int>? jitter = null)
        : this(registry, bus, authReader, CreateHttpClient(handler), timeProvider,
            jitter ?? (static maximum => Random.Shared.Next(maximum + 1)))
    {
    }

    private OpenCodeGoQuotaProvider(MetricRegistry registry, MetricBus bus, OpenCodeGoAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, Func<int, int>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitter = jitter ?? (static maximum => Random.Shared.Next(maximum + 1));
        if (_timeProvider.TimestampFrequency <= 0)
            throw new ArgumentException("Time provider must expose a positive timestamp frequency.", nameof(timeProvider));

        _descriptors = new MetricDescriptor[Definitions.Length];
        for (var i = 0; i < Definitions.Length; i++)
        {
            var definition = Definitions[i];
            var descriptor = new MetricDescriptor(new MetricId(definition.Id), $"OpenCode Go {definition.Name}",
                definition.SemanticType, definition.Kind, unit: definition.Unit);
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("OpenCode Go quota metric registration failed.");
            _descriptors[i] = descriptor;
        }

        var now = _timeProvider.GetUtcNow();
        _lastUtc = now;
        _lastTimestamp = _timeProvider.GetTimestamp();
        for (var i = 0; i < _points.Length; i++)
        {
            _points[i] = OpenCodeGoQuotaPoint.Unavailable();
            _pointObservedAt[i] = now;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            if (_runActive) throw new InvalidOperationException("OpenCode Go quota provider is already running.");
            if (_runStopped) throw new InvalidOperationException("OpenCode Go quota provider has stopped.");
            _runActive = true;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers =
        [
            PollLoopAsync(stop.Token),
            PublishLoopAsync(stop.Token),
            ObserveAuthLoopAsync(stop.Token)
        ];
        Exception? failure = null;

        try
        {
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (!stop.IsCancellationRequested)
                failure = new InvalidOperationException("OpenCode Go quota worker stopped unexpectedly.");
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
        await _pollGate.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? ownerStop = null;
        byte[]? body = null;

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var startedAt = _timeProvider.GetUtcNow();
            var startedTimestamp = _timeProvider.GetTimestamp();
            var auth = ReadAndApplyAuth(startedAt, startedTimestamp);
            if (auth.Status != OpenCodeGoAuthStatus.Ready || auth.Credential is null) return;

            var generation = 0L;
            lock (_stateLock)
            {
                if (_runStopped || _disposed) return;
                if (StringComparer.Ordinal.Equals(_rejectedKeyFingerprint, auth.Credential.Fingerprint)) return;
                var dueTimestamp = Math.Max(_nextPollTimestamp, _retryNotBeforeTimestamp);
                if (startedTimestamp < dueTimestamp) return;

                _nextPollTimestamp = AddTimestamp(startedTimestamp, PollInterval);
                generation = _authGeneration;
                ownerStop = new CancellationTokenSource();
                _activeRequestStop = ownerStop;
            }

            string? failureCode = null;
            var failureState = MetricState.Error;
            TimeSpan? retryAfter = null;
            using var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider);
            using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(
                admission.Token, deadline.Token, ownerStop.Token);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Credential.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("Cerith.TelemetryService");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    requestStop.Token).ConfigureAwait(false);
                requestStop.Token.ThrowIfCancellationRequested();

                var finalUri = response.RequestMessage?.RequestUri;
                if (((int)response.StatusCode is >= 300 and < 400) ||
                    (finalUri is not null && !UsageEndpoint.Equals(finalUri)))
                {
                    failureCode = "redirect_rejected";
                }
                else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    failureCode = "rate_limited";
                    retryAfter = ParseRetryAfter(response.Headers.RetryAfter, _timeProvider.GetUtcNow());
                }
                else if (!response.IsSuccessStatusCode)
                {
                    (failureCode, failureState) = MapFailure(response.StatusCode);
                }
                else
                {
                    var result = await ReadBoundedBodyAsync(response, requestStop.Token).ConfigureAwait(false);
                    requestStop.Token.ThrowIfCancellationRequested();
                    if (result.TooLarge) failureCode = "response_too_large";
                    else if (result.Body is null) failureCode = "response_invalid";
                    else body = result.Body;
                }
            }
            catch (OperationCanceledException) when (admission.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (ownerStop?.IsCancellationRequested == true)
            {
                return;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                failureCode = "timeout";
            }
            catch (OperationCanceledException)
            {
                failureCode = "timeout";
            }
            catch (HttpRequestException)
            {
                failureCode = "offline";
                failureState = MetricState.Unavailable;
            }
            catch (IOException)
            {
                failureCode = "offline";
                failureState = MetricState.Unavailable;
            }

            if (admission.IsCancellationRequested) admission.Token.ThrowIfCancellationRequested();
            if (ownerStop.IsCancellationRequested) return;
            var receivedAt = _timeProvider.GetUtcNow();
            var receivedTimestamp = _timeProvider.GetTimestamp();
            var finalAuth = ReadAndApplyAuth(receivedAt, receivedTimestamp);
            if (finalAuth.Status != OpenCodeGoAuthStatus.Ready || finalAuth.Credential is null ||
                !StringComparer.Ordinal.Equals(finalAuth.Credential.Fingerprint, auth.Credential.Fingerprint) ||
                GetAuthGeneration() != generation)
                return;

            if (admission.IsCancellationRequested) admission.Token.ThrowIfCancellationRequested();
            if (ownerStop.IsCancellationRequested) return;
            if (failureCode is not null)
            {
                ApplyFailure(failureCode, failureState, retryAfter, receivedAt, receivedTimestamp,
                    auth.Credential.Fingerprint, generation);
            }
            else if (body is null)
            {
                ApplyFailure("response_invalid", MetricState.Error, null, receivedAt, receivedTimestamp,
                    auth.Credential.Fingerprint, generation);
            }
            else
            {
                var reading = OpenCodeGoQuotaParser.Parse(body, receivedAt);
                ApplyReading(reading, receivedAt, receivedTimestamp, generation);
            }
        }
        finally
        {
            if (ownerStop is not null)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activeRequestStop, ownerStop)) _activeRequestStop = null;
                }
                ownerStop.Dispose();
            }
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            _pollGate.Release();
        }
    }

    internal ValueTask ObserveLocalStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ReadAndApplyAuth(_timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
        return ValueTask.CompletedTask;
    }

    internal async ValueTask PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        ReadAndApplyAuth(_timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
        await PublishSnapshotsAsync(admission.Token).ConfigureAwait(false);
    }

    private OpenCodeGoAuthRead ReadAndApplyAuth(DateTimeOffset now, long timestamp)
    {
        lock (_authReadLock)
        {
            var auth = _authReader.Read();
            lock (_stateLock)
            {
                if (_disposed || _runStopped) return auth;
                ObserveClockLocked(now, timestamp);

                var fingerprint = auth.Credential?.Fingerprint;
                if (!StringComparer.Ordinal.Equals(_activeKeyFingerprint, fingerprint) ||
                    !StringComparer.Ordinal.Equals(_activeAuthSourceFingerprint, auth.SourceFingerprint))
                {
                    _activeKeyFingerprint = fingerprint;
                    _activeAuthSourceFingerprint = auth.SourceFingerprint;
                    _authGeneration++;
                    _activeRequestStop?.Cancel();
                    CancelActivePublicationLocked();
                    ClearCachesLocked();
                    _rejectedKeyFingerprint = null;
                    _nextPollTimestamp = long.MinValue;
                    _retryNotBeforeTimestamp = long.MinValue;
                    _rateLimitFailures = 0;
                    ApplyAuthStateLocked(auth, now, refreshObservation: true);
                    _snapshotGeneration++;
                    CancelActivePublicationLocked();
                }
                else if (auth.Status != OpenCodeGoAuthStatus.Ready)
                {
                    ApplyAuthStateLocked(auth, now, refreshObservation: false);
                }

                ExpireCachesLocked(now, timestamp);
            }
            return auth;
        }
    }

    private void ApplyAuthStateLocked(OpenCodeGoAuthRead auth, DateTimeOffset observedAt, bool refreshObservation)
    {
        var point = auth.Status switch
        {
            OpenCodeGoAuthStatus.Ready or OpenCodeGoAuthStatus.Unavailable => OpenCodeGoQuotaPoint.Unavailable(),
            OpenCodeGoAuthStatus.Unsupported => OpenCodeGoQuotaPoint.Error(auth.ErrorCode ?? "auth_unsupported"),
            _ => OpenCodeGoQuotaPoint.Error(auth.ErrorCode ?? "auth_unavailable")
        };
        for (var i = 0; i < _points.Length; i++)
        {
            if (refreshObservation || !_points[i].Equals(point)) UpdatePointLocked(i, point, observedAt);
        }
    }

    private void ObserveClockLocked(DateTimeOffset now, long timestamp)
    {
        if (_lastUtc is DateTimeOffset previousUtc)
        {
            var wallElapsed = now - previousUtc;
            var monotonicElapsed = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
            if (wallElapsed < TimeSpan.Zero || monotonicElapsed < TimeSpan.Zero ||
                Math.Abs((wallElapsed - monotonicElapsed).TotalSeconds) > ClockDiscrepancyTolerance.TotalSeconds)
            {
                ClearCachesLocked();
                var clockPoint = OpenCodeGoQuotaPoint.Unavailable("clock_changed");
                for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, clockPoint, now);
                _snapshotGeneration++;
                CancelActivePublicationLocked();
            }
        }

        _lastUtc = now;
        _lastTimestamp = timestamp;
    }

    private void ExpireCachesLocked(DateTimeOffset now, long timestamp)
    {
        for (var bucket = 0; bucket < 3; bucket++)
        {
            var reset = _cache[bucket + 3];
            if (reset is not null && reset.Value.AsDateTimeOffset() <= now)
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
            ClearCachesLocked();
            var clockPoint = OpenCodeGoQuotaPoint.Unavailable("clock_changed");
            for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, clockPoint, now);
            _snapshotGeneration++;
            CancelActivePublicationLocked();
            return;
        }

        if (age >= MaximumCacheAge)
        {
            _cache[index] = null;
            UpdatePointLocked(index, OpenCodeGoQuotaPoint.Unavailable("cache_expired"), now);
        }
        else if (age >= StaleAfter && _points[index].State == MetricState.Fresh)
        {
            UpdatePointLocked(index, new OpenCodeGoQuotaPoint(MetricState.Stale, cached.Value, null), cached.ObservedAt);
        }
    }

    private void InvalidateBucketLocked(int bucket, DateTimeOffset now, string errorCode)
    {
        var expired = OpenCodeGoQuotaPoint.Unavailable(errorCode);
        _cache[bucket] = null;
        _cache[bucket + 3] = null;
        UpdatePointLocked(bucket, expired, now);
        UpdatePointLocked(bucket + 3, expired, now);
    }

    private void ApplyReading(OpenCodeGoQuotaReading reading, DateTimeOffset observedAt, long timestamp,
        long expectedAuthGeneration)
    {
        lock (_stateLock)
        {
            if (_disposed || _runStopped || _authGeneration != expectedAuthGeneration) return;
            for (var i = 0; i < reading.Points.Length; i++)
            {
                var point = reading.Points[i];
                _cache[i] = point.State == MetricState.Fresh && point.Value is MetricValue value
                    ? new CachedPoint(value, observedAt, timestamp)
                    : null;
                UpdatePointLocked(i, point, observedAt);
            }
            _retryNotBeforeTimestamp = long.MinValue;
            _rateLimitFailures = 0;
        }
    }

    private void ApplyFailure(string errorCode, MetricState noCacheState, TimeSpan? retryAfter,
        DateTimeOffset observedAt, long timestamp, string keyFingerprint, long expectedAuthGeneration)
    {
        lock (_stateLock)
        {
            if (_disposed || _runStopped || _authGeneration != expectedAuthGeneration) return;
            ExpireCachesLocked(observedAt, timestamp);

            if (errorCode is "auth_required" or "auth_forbidden")
            {
                _rejectedKeyFingerprint = keyFingerprint;
                ClearCachesLocked();
                var authError = OpenCodeGoQuotaPoint.Error(errorCode);
                for (var i = 0; i < _points.Length; i++) UpdatePointLocked(i, authError, observedAt);
                return;
            }

            if (errorCode == "rate_limited") SetRetryDeadlineLocked(retryAfter, timestamp);

            for (var i = 0; i < _points.Length; i++)
            {
                var cached = _cache[i];
                var point = cached is not null
                    ? new OpenCodeGoQuotaPoint(MetricState.Stale, cached.Value, errorCode)
                    : noCacheState == MetricState.Unavailable
                        ? OpenCodeGoQuotaPoint.Unavailable(errorCode)
                        : OpenCodeGoQuotaPoint.Error(errorCode);
                UpdatePointLocked(i, point, cached?.ObservedAt ?? observedAt);
            }
        }
    }

    private void SetRetryDeadlineLocked(TimeSpan? retryAfter, long timestamp)
    {
        TimeSpan delay;
        if (retryAfter is TimeSpan serverDelay)
        {
            _rateLimitFailures = 0;
            delay = serverDelay;
        }
        else
        {
            if (_rateLimitFailures < int.MaxValue) _rateLimitFailures++;
            var baseSeconds = Math.Min(900, _rateLimitFailures switch
            {
                1 => 60,
                2 => 120,
                3 => 240,
                4 => 480,
                _ => 900
            });
            var jitterMaximum = Math.Min(30, baseSeconds / 10);
            var jitter = Math.Clamp(_jitter(jitterMaximum), 0, jitterMaximum);
            delay = TimeSpan.FromSeconds(Math.Min(900, baseSeconds + jitter));
        }

        _retryNotBeforeTimestamp = AddTimestamp(timestamp, delay);
    }

    private void UpdatePointLocked(int index, OpenCodeGoQuotaPoint point, DateTimeOffset observedAt)
    {
        if (_points[index].Equals(point) && _pointObservedAt[index] == observedAt) return;
        _points[index] = point;
        _pointObservedAt[index] = observedAt;
        _snapshotGeneration++;
        CancelActivePublicationLocked();
    }

    private void ClearCachesLocked()
    {
        Array.Clear(_cache);
    }

    private long GetAuthGeneration()
    {
        lock (_stateLock) return _authGeneration;
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
        var result = new MetricSnapshot[_descriptors.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var point = _points[i];
            result[i] = new MetricSnapshot(_descriptors[i].Id, point.Value, _pointObservedAt[i], point.State,
                point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                source: "OpenCodeGo.Usage.ReadOnly.v1", errorCode: point.ErrorCode);
        }
        return result;
    }

    private void MarkStopped()
    {
        lock (_stateLock)
        {
            if (!_runStopped)
            {
                _runStopped = true;
                _authGeneration++;
                _activeKeyFingerprint = null;
                _activeAuthSourceFingerprint = null;
                _rejectedKeyFingerprint = null;
                _nextPollTimestamp = long.MinValue;
                _retryNotBeforeTimestamp = long.MinValue;
                _rateLimitFailures = 0;
                ClearCachesLocked();
                var now = _timeProvider.GetUtcNow();
                var stopped = new OpenCodeGoQuotaPoint(MetricState.Stopped, null, null);
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
            return;
        }
    }

    private static (string? ErrorCode, MetricState State) MapFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ("auth_required", MetricState.Error),
        HttpStatusCode.Forbidden => ("auth_forbidden", MetricState.Error),
        HttpStatusCode.RequestTimeout => ("timeout", MetricState.Error),
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            ("upstream_unavailable", MetricState.Error),
        _ when (int)status >= 500 => ("upstream_unavailable", MetricState.Error),
        _ => ("request_failed", MetricState.Error)
    };

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        if (retryAfter?.Delta is TimeSpan delta && delta >= TimeSpan.Zero) return delta;
        if (retryAfter?.Date is DateTimeOffset date)
        {
            var delay = date - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return null;
    }

    private long AddTimestamp(long timestamp, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return timestamp;
        var ticks = Math.Ceiling(delay.TotalSeconds * _timeProvider.TimestampFrequency);
        if (!double.IsFinite(ticks) || ticks >= long.MaxValue || timestamp > long.MaxValue - (long)ticks)
            return long.MaxValue;
        return timestamp + (long)ticks;
    }

    private static HttpClient CreateHttpClient() => CreateHttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = RequestTimeout,
        MaxResponseHeadersLength = 16
    });

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        new(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static async Task<(byte[]? Body, bool TooLarge)> ReadBoundedBodyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            return (null, true);

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

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            await Task.Delay(PublishInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ObserveAuthLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await ObserveLocalStateAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(PublishInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ObserveDetachedWorkerFaults(Task[] workers)
    {
        var combined = Task.WhenAll(workers);
        _ = combined.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            ClearCachesLocked();
            _activeKeyFingerprint = null;
            _activeAuthSourceFingerprint = null;
            _rejectedKeyFingerprint = null;
            _activeRequestStop?.Cancel();
            CancelActivePublicationLocked();
        }
        _shutdown.Cancel();
        _httpClient.Dispose();
    }

    private sealed record CachedPoint(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp);
}
