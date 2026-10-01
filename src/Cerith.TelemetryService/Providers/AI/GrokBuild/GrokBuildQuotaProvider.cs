#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.GrokBuild;

internal sealed class GrokBuildQuotaProvider : IDisposable
{
    internal static readonly Uri BillingEndpoint =
        new("https://cli-chat-proxy.grok.com/v1/billing?format=credits");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 1024 * 1024;
    internal const int MetricCount = 11;

    private static readonly string[] MetricIds =
    [
        "ai.grok_build.quota.included.used_percent",
        "ai.grok_build.quota.included.remaining_percent",
        "ai.grok_build.quota.included.period.type",
        "ai.grok_build.quota.included.period.starts_at",
        "ai.grok_build.quota.included.period.resets_at",
        "ai.grok_build.quota.included.period.duration",
        "ai.grok_build.balance.on_demand.used",
        "ai.grok_build.balance.on_demand.cap",
        "ai.grok_build.balance.prepaid",
        "ai.grok_build.quota.included.unified_billing",
        "ai.grok_build.usage.product.grok_build.used_percent"
    ];

    private static readonly MetricValueKind[] ValueKinds =
    [
        MetricValueKind.Double, MetricValueKind.Double, MetricValueKind.String,
        MetricValueKind.DateTimeOffset, MetricValueKind.DateTimeOffset, MetricValueKind.TimeSpan,
        MetricValueKind.Int64, MetricValueKind.Int64, MetricValueKind.Int64,
        MetricValueKind.Boolean, MetricValueKind.Double
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task<GrokBuildAuthObservation>> _observeAuth;
    private readonly Func<int, int> _jitter;
    private readonly GrokBuildAcpClient? _authClient;
    private readonly bool _ownsHttpClient;
    private readonly byte[] _identityKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _tokenKey = RandomNumberGenerator.GetBytes(32);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateAndPublish = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly GrokBuildQuotaPoint[] _points = new GrokBuildQuotaPoint[MetricCount];
    private readonly CachedPoint?[] _cache = new CachedPoint?[MetricCount];
    private readonly DateTimeOffset[] _pointObservedAt = new DateTimeOffset[MetricCount];
    private GrokBuildAuthIdentity? _activeIdentity;
    private string? _activeIdentityKey;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _rejectedTokenFingerprint;
    private long _authGeneration;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _rateLimitFailures;
    private int _requestFailures;
    private DateTimeOffset? _lastUtc;
    private long _lastTimestamp;
    private bool _disposed;
    private int _disposeStarted;

    public GrokBuildQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new GrokBuildAcpClient(), CreateHttpClient(), TimeProvider.System,
            static maximum => Random.Shared.Next(maximum + 1), ownsHttpClient: true)
    {
    }

    internal GrokBuildQuotaProvider(
        MetricRegistry registry,
        MetricBus bus,
        HttpClient httpClient,
        TimeProvider timeProvider,
        Func<CancellationToken, Task<GrokBuildAuthObservation>> observeAuth,
        Func<int, int>? jitter = null)
        : this(registry, bus, httpClient, timeProvider, observeAuth, jitter ?? (static _ => 0),
            ownsHttpClient: false)
    {
    }

    private GrokBuildQuotaProvider(
        MetricRegistry registry,
        MetricBus bus,
        GrokBuildAcpClient authClient,
        HttpClient httpClient,
        TimeProvider timeProvider,
        Func<int, int> jitter,
        bool ownsHttpClient)
        : this(registry, bus, httpClient, timeProvider, authClient.ObserveAsync, jitter, ownsHttpClient)
    {
        _authClient = authClient;
    }

    private GrokBuildQuotaProvider(
        MetricRegistry registry,
        MetricBus bus,
        HttpClient httpClient,
        TimeProvider timeProvider,
        Func<CancellationToken, Task<GrokBuildAuthObservation>> observeAuth,
        Func<int, int> jitter,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _observeAuth = observeAuth ?? throw new ArgumentNullException(nameof(observeAuth));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _ownsHttpClient = ownsHttpClient;

        var names = new[]
        {
            "Included Usage", "Included Remaining", "Included Period Type", "Included Period Start",
            "Included Period Reset", "Included Period Duration", "On-Demand Used", "On-Demand Cap",
            "Prepaid Balance", "Unified Included Billing", "Product Grok Build Usage"
        };
        var semanticTypes = new[]
        {
            "quota_usage", "quota_remaining", "billing_period", "timestamp", "timestamp", "duration",
            "balance", "balance_limit", "balance", "billing_mode", "product_usage"
        };
        var units = new string?[]
        {
            "%", "%", null, null, null, null, "USD cents", "USD cents", "USD cents", null, "%"
        };
        _descriptors = new MetricDescriptor[MetricCount];
        for (var i = 0; i < _descriptors.Length; i++)
        {
            _descriptors[i] = new MetricDescriptor(new MetricId(MetricIds[i]), $"Grok Build {names[i]}",
                semanticTypes[i], ValueKinds[i], unit: units[i]);
            if (!registry.TryRegister(_descriptors[i]))
                throw new InvalidOperationException("Grok Build quota metric registration failed.");
            _points[i] = GrokBuildQuotaPoint.Unavailable();
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
            await ObserveLocalStateAsync(stop.Token).ConfigureAwait(false);
            await PublishCurrentAsync(stop.Token).ConfigureAwait(false);
            workers =
            [
                PollLoopAsync(stop.Token),
                PublishLoopAsync(stop.Token),
                MonitorLocalStateLoopAsync(stop.Token)
            ];
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            if (workers.Length > 0)
                await Task.WhenAll(workers.Select(DrainWorkerAsync)).ConfigureAwait(false);

            _authClient?.Dispose();
            await PublishStoppedAsync().ConfigureAwait(false);
        }
    }

    private static async Task DrainWorkerAsync(Task worker)
    {
        await Task.WhenAny(worker).ConfigureAwait(false);
        if (worker.IsFaulted)
            _ = worker.Exception;
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
            var timestamp = _timeProvider.GetTimestamp();
            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                ObserveClockLocked(_timeProvider.GetUtcNow(), timestamp);
                ExpireCachesLocked(_timeProvider.GetUtcNow(), timestamp);
                if (timestamp < _nextPollTimestamp)
                    return;
            }
            finally
            {
                _stateAndPublish.Release();
            }

            var auth = await ReadAuthObservationAsync(admission.Token).ConfigureAwait(false);
            long requestGeneration;
            string tokenFingerprint;
            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var nowTimestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(now, nowTimestamp);
                ApplyAuthObservationLocked(auth, now);
                ExpireCachesLocked(now, nowTimestamp);
                if (auth.Identity is null || auth.AccessToken is null)
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }

                if (nowTimestamp < _retryNotBeforeTimestamp)
                {
                    ScheduleRetryCheckLocked();
                    return;
                }

                tokenFingerprint = Fingerprint(_tokenKey, auth.AccessToken);
                if (_rejectedTokenFingerprint is not null &&
                    StringComparer.Ordinal.Equals(_rejectedTokenFingerprint, tokenFingerprint))
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }

                requestGeneration = _authGeneration;
                requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = requestStop;
            }
            finally
            {
                _stateAndPublish.Release();
            }

            var startedTimestamp = _timeProvider.GetTimestamp();
            HttpResponseMessage? response = null;
            string? failureCode = null;
            var noCacheFailureState = MetricState.Error;
            var ownerCancelled = false;
            TimeSpan? retryAfter = null;
            GrokBuildQuotaReading? reading = null;
            DateTimeOffset observedAt = _timeProvider.GetUtcNow();

            using (var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(requestStop.Token, deadline.Token))
            {
                try
                {
                    using var request = CreateRequest(auth);
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        linked.Token).ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri != BillingEndpoint)
                        failureCode = "redirect_rejected";
                    else if (response.StatusCode == HttpStatusCode.Unauthorized)
                        failureCode = "auth_required";
                    else if (response.StatusCode == HttpStatusCode.Forbidden)
                        failureCode = "auth_forbidden";
                    else if ((int)response.StatusCode == 429)
                    {
                        failureCode = "rate_limited";
                        retryAfter = ReadRetryAfter(response, _timeProvider.GetUtcNow());
                    }
                    else if ((int)response.StatusCode is >= 300 and < 400)
                        failureCode = "redirect_rejected";
                    else if (!response.IsSuccessStatusCode)
                    {
                        failureCode = (int)response.StatusCode >= 500 ? "upstream_unavailable" : "http_failure";
                        noCacheFailureState = MetricState.Unavailable;
                    }
                    else
                    {
                        var result = await ReadResponseBodyAsync(response, linked.Token).ConfigureAwait(false);
                        body = result.Body;
                        if (result.TooLarge)
                            failureCode = "response_too_large";
                        else if (body is null)
                            failureCode = "response_empty";
                        else
                        {
                            observedAt = _timeProvider.GetUtcNow();
                            reading = GrokBuildQuotaParser.Parse(body, observedAt);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested ||
                    _shutdown.IsCancellationRequested)
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
                catch (HttpRequestException)
                {
                    failureCode = "offline";
                    noCacheFailureState = MetricState.Unavailable;
                }
                catch (IOException)
                {
                    failureCode = "offline";
                    noCacheFailureState = MetricState.Unavailable;
                }
                catch (Exception)
                {
                    failureCode = "request_failed";
                }
                finally
                {
                    response?.Dispose();
                }
            }

            if (ownerCancelled)
                return;

            var confirmedAuth = await ReadAuthObservationAsync(admission.Token).ConfigureAwait(false);
            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var nowTimestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(now, nowTimestamp);
                ApplyAuthObservationLocked(confirmedAuth, now);
                ExpireCachesLocked(now, nowTimestamp);
                if (requestStop.IsCancellationRequested || requestGeneration != _authGeneration)
                    return;

                if (failureCode is null && reading is not null)
                {
                    ReplaceFromReadingLocked(reading, observedAt, _timeProvider.GetTimestamp());
                    _requestFailures = 0;
                    _rateLimitFailures = 0;
                    _retryNotBeforeTimestamp = 0;
                    _rejectedTokenFingerprint = null;
                    ScheduleNextPollLocked(PollInterval);
                }
                else
                {
                    var code = failureCode ?? "request_failed";
                    PreserveCacheOnFailureLocked(code, noCacheFailureState, now);
                    if (code == "auth_required")
                        _rejectedTokenFingerprint = tokenFingerprint;

                    if (code == "rate_limited")
                    {
                        _rateLimitFailures++;
                        var delay = retryAfter ?? GetBackoffDelay(_rateLimitFailures);
                        SetRetryNotBeforeLocked(delay);
                        ScheduleNextPollLocked(delay);
                    }
                    else
                    {
                        _requestFailures++;
                        var delay = GetBackoffDelay(_requestFailures);
                        SetRetryNotBeforeLocked(delay);
                        ScheduleNextPollLocked(delay);
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(_activeRequestStop, requestStop))
                    _activeRequestStop = null;
                _stateAndPublish.Release();
            }
        }
        finally
        {
            if (body is not null)
                CryptographicOperations.ZeroMemory(body);
            if (requestStop is not null)
            {
                await _stateAndPublish.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activeRequestStop, requestStop))
                        _activeRequestStop = null;
                }
                finally
                {
                    _stateAndPublish.Release();
                }
                requestStop.Dispose();
            }
            _pollLock.Release();
        }
    }

    internal async ValueTask ObserveLocalStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var observation = await ReadAuthObservationAsync(admission.Token).ConfigureAwait(false);
        await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            var timestamp = _timeProvider.GetTimestamp();
            ObserveClockLocked(now, timestamp);
            ApplyAuthObservationLocked(observation, now);
            ExpireCachesLocked(now, timestamp);
        }
        finally
        {
            _stateAndPublish.Release();
        }
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
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(now, timestamp);
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
                        source: "GrokBuild.Billing.ReadOnly.v1", errorCode: point.ErrorCode);
                }
            }
            finally
            {
                _stateAndPublish.Release();
            }

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
                    if (ReferenceEquals(_activePublishStop, publication))
                        _activePublishStop = null;
                }
                finally
                {
                    _stateAndPublish.Release();
                }
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

    private async Task<GrokBuildAuthObservation> ReadAuthObservationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _observeAuth(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GrokBuildAuthObservation.Unavailable("auth_unavailable");
        }
    }

    private void ApplyAuthObservationLocked(GrokBuildAuthObservation observation, DateTimeOffset observedAt)
    {
        if (observation.Identity is null || observation.AccessToken is null ||
            string.IsNullOrEmpty(observation.ClientVersion))
        {
            if (_activeIdentity is not null)
            {
                InvalidateOwnerWorkLocked();
                ClearCacheLocked(observedAt);
                _rateLimitFailures = 0;
                _requestFailures = 0;
                _retryNotBeforeTimestamp = 0;
            }
            _activeIdentity = null;
            _activeIdentityKey = null;
            _rejectedTokenFingerprint = null;
            var code = observation.ErrorCode ?? "auth_unavailable";
            ClearToStateLocked(code == "auth_unavailable" || code == "auth_timeout"
                ? MetricState.Unavailable
                : MetricState.Error, code == "auth_unavailable" ? null : code, observedAt);
            return;
        }

        var identityKey = Fingerprint(_identityKey, IdentityBytes(observation.Identity));
        var changedOwner = _activeIdentityKey is not null &&
            !StringComparer.Ordinal.Equals(_activeIdentityKey, identityKey);
        if (changedOwner)
        {
            InvalidateOwnerWorkLocked();
            ClearCacheLocked(observedAt);
            ClearToStateLocked(MetricState.Unavailable, null, observedAt);
            _rejectedTokenFingerprint = null;
            _rateLimitFailures = 0;
            _requestFailures = 0;
            _retryNotBeforeTimestamp = 0;
        }

        var tokenFingerprint = Fingerprint(_tokenKey, observation.AccessToken);
        if (_rejectedTokenFingerprint is not null &&
            !StringComparer.Ordinal.Equals(_rejectedTokenFingerprint, tokenFingerprint))
            _rejectedTokenFingerprint = null;

        _activeIdentity = observation.Identity;
        _activeIdentityKey = identityKey;
    }

    private void InvalidateOwnerWorkLocked()
    {
        _authGeneration++;
        _activeRequestStop?.Cancel();
        _activePublishStop?.Cancel();
    }

    private void ReplaceFromReadingLocked(GrokBuildQuotaReading reading, DateTimeOffset observedAt, long timestamp)
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
                ? GrokBuildQuotaPoint.Unavailable()
                : GrokBuildQuotaPoint.Error(errorCode);
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
                MetricState.Error => GrokBuildQuotaPoint.Error(errorCode ?? "auth_unavailable"),
                MetricState.Stopped => new GrokBuildQuotaPoint(MetricState.Stopped, null, null),
                _ => GrokBuildQuotaPoint.Unavailable()
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
            _activeIdentity = null;
            _activeIdentityKey = null;
            _activeRequestStop = null;
            _rejectedTokenFingerprint = null;
            _retryNotBeforeTimestamp = 0;
            _rateLimitFailures = 0;
            _requestFailures = 0;
            ClearToStateLocked(MetricState.Stopped, null, now);
            var snapshots = _descriptors.Select(descriptor => new MetricSnapshot(descriptor.Id, null, now,
                MetricState.Stopped, MetricConfidence.Unknown, source: "GrokBuild.Billing.ReadOnly.v1")).ToArray();

            _stateAndPublish.Release();
            stateLockAcquired = false;
            await Task.WhenAll(snapshots.Select(snapshot => _bus.PublishAsync(snapshot, deadline.Token).AsTask()))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return; }
        catch (ObjectDisposedException) { return; }
        finally
        {
            if (stateLockAcquired)
                _stateAndPublish.Release();
            if (publishLockAcquired)
                _publishLock.Release();
        }
    }

    private void ClearCacheLocked(DateTimeOffset observedAt)
    {
        _activePublishStop?.Cancel();
        for (var i = 0; i < _cache.Length; i++)
        {
            _cache[i] = null;
            _points[i] = GrokBuildQuotaPoint.Unavailable();
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
        var resetAt = _cache[GrokBuildQuotaParser.PeriodEnd] is CachedPoint resetCache
            ? resetCache.Value.AsDateTimeOffset()
            : (DateTimeOffset?)null;
        var resetElapsed = resetAt.HasValue && utcNow >= resetAt.Value;
        var includedExpired = resetElapsed;
        for (var i = GrokBuildQuotaParser.IncludedUsedPercent;
             i <= GrokBuildQuotaParser.PeriodDuration; i++)
        {
            if (_cache[i] is CachedPoint cached &&
                _timeProvider.GetElapsedTime(cached.Timestamp, timestamp) >= MaximumCacheAge)
                includedExpired = true;
        }

        if (includedExpired)
        {
            _activePublishStop?.Cancel();
            var code = resetElapsed ? "reset_elapsed" : "cache_expired";
            for (var i = GrokBuildQuotaParser.IncludedUsedPercent;
                 i <= GrokBuildQuotaParser.PeriodDuration; i++)
            {
                _cache[i] = null;
                _points[i] = GrokBuildQuotaPoint.Unavailable(code);
                _pointObservedAt[i] = utcNow;
            }
        }

        for (var i = GrokBuildQuotaParser.OnDemandUsed; i <= GrokBuildQuotaParser.PrepaidBalance; i++)
        {
            if (_cache[i] is not CachedPoint cached ||
                _timeProvider.GetElapsedTime(cached.Timestamp, timestamp) < MaximumCacheAge)
                continue;
            _activePublishStop?.Cancel();
            _cache[i] = null;
            _points[i] = GrokBuildQuotaPoint.Unavailable("cache_expired");
            _pointObservedAt[i] = utcNow;
        }

        for (var i = GrokBuildQuotaParser.UnifiedBilling; i <= GrokBuildQuotaParser.ProductUsagePercent; i++)
        {
            if (_cache[i] is not CachedPoint cached ||
                _timeProvider.GetElapsedTime(cached.Timestamp, timestamp) < MaximumCacheAge)
                continue;
            _activePublishStop?.Cancel();
            _cache[i] = null;
            _points[i] = GrokBuildQuotaPoint.Unavailable("cache_expired");
            _pointObservedAt[i] = utcNow;
        }
    }

    private GrokBuildQuotaPoint EvaluateCache(CachedPoint cached, long timestamp)
    {
        var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
        return cached.ForceStale || age >= StaleAfter
            ? new GrokBuildQuotaPoint(MetricState.Stale, cached.Value, cached.ErrorCode)
            : new GrokBuildQuotaPoint(MetricState.Fresh, cached.Value, cached.ErrorCode);
    }

    private async ValueTask<TimeSpan> GetPollDelayAsync(CancellationToken cancellationToken)
    {
        await _stateAndPublish.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ticks = (double)_nextPollTimestamp - _timeProvider.GetTimestamp();
            if (ticks <= 0)
                return TimeSpan.Zero;
            var seconds = ticks / _timeProvider.TimestampFrequency;
            return !double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromSeconds(seconds);
        }
        finally
        {
            _stateAndPublish.Release();
        }
    }

    private void ScheduleNextPollLocked(TimeSpan delay)
    {
        if (delay < PollInterval)
            delay = PollInterval;
        _nextPollTimestamp = AddTimestampDelay(_timeProvider.GetTimestamp(), delay);
    }

    private void SetRetryNotBeforeLocked(TimeSpan delay) =>
        _retryNotBeforeTimestamp = AddTimestampDelay(_timeProvider.GetTimestamp(), delay);

    private void ScheduleRetryCheckLocked()
    {
        var regular = AddTimestampDelay(_timeProvider.GetTimestamp(), PollInterval);
        _nextPollTimestamp = Math.Min(_retryNotBeforeTimestamp, regular);
    }

    private long AddTimestampDelay(long timestamp, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return timestamp;
        var scaled = Math.Ceiling(delay.TotalSeconds * _timeProvider.TimestampFrequency);
        if (!double.IsFinite(scaled) || scaled >= long.MaxValue)
            return long.MaxValue;
        var delta = (long)scaled;
        return timestamp > long.MaxValue - delta ? long.MaxValue : timestamp + delta;
    }

    private TimeSpan GetBackoffDelay(int failures)
    {
        var seconds = 60d * Math.Pow(2, Math.Min(failures - 1, 20));
        seconds = Math.Min(seconds, TimeSpan.FromMinutes(15).TotalSeconds);
        var jitterCeiling = (int)Math.Min(15, Math.Floor(seconds / 10));
        var jitter = Math.Clamp(_jitter(jitterCeiling), 0, jitterCeiling);
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static HttpRequestMessage CreateRequest(GrokBuildAuthObservation auth)
    {
        if (auth.Identity is null || auth.AccessToken is null || auth.ClientVersion is null)
            throw new InvalidOperationException("Grok Build session is unavailable.");

        var request = new HttpRequestMessage(HttpMethod.Get, BillingEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Add("X-XAI-Token-Auth", "xai-grok-cli");
        request.Headers.Add("x-userid", auth.Identity.UserId);
        request.Headers.Add("x-grok-client-version", auth.ClientVersion);
        request.Headers.Add("x-grok-client-mode", "headless");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
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
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            return (null, true);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxResponseBytes + 1);
        try
        {
            var count = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count, MaxResponseBytes + 1 - count),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                count += read;
                if (count > MaxResponseBytes)
                    return (null, true);
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
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta)
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (retryAfter?.Date is DateTimeOffset date)
                return date <= now ? TimeSpan.Zero : date - now;
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string Fingerprint(byte[] key, string value) =>
        Fingerprint(key, Encoding.UTF8.GetBytes(value));

    private static string Fingerprint(byte[] key, byte[] value)
    {
        var digest = HMACSHA256.HashData(key, value);
        try
        {
            return Convert.ToHexString(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static byte[] IdentityBytes(GrokBuildAuthIdentity identity) =>
        JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            identity.MethodId, identity.UserId, identity.PrincipalType, identity.PrincipalId,
            identity.TeamId, identity.OrganizationId
        });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _authClient?.Dispose();
        _pollLock.Wait();
        _publishLock.Wait();
        _stateAndPublish.Wait();
        try
        {
            _activeIdentity = null;
            _activeIdentityKey = null;
            _activeRequestStop = null;
            _activePublishStop = null;
            _rejectedTokenFingerprint = null;
            _retryNotBeforeTimestamp = 0;
            _rateLimitFailures = 0;
            _requestFailures = 0;
            for (var i = 0; i < _cache.Length; i++)
            {
                _cache[i] = null;
                _points[i] = GrokBuildQuotaPoint.Unavailable();
                _pointObservedAt[i] = default;
            }
        }
        finally
        {
            _stateAndPublish.Release();
            _publishLock.Release();
            _pollLock.Release();
        }

        CryptographicOperations.ZeroMemory(_identityKey);
        CryptographicOperations.ZeroMemory(_tokenKey);
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private sealed record CachedPoint(
        MetricValue Value,
        string? ErrorCode,
        DateTimeOffset ObservedAt,
        long Timestamp,
        bool ForceStale = false);
}
