#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Kimi;

internal sealed class KimiQuotaProvider : IDisposable
{
    internal static readonly Uri UsageEndpoint = new("https://api.kimi.com/coding/v1/usages");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 1024 * 1024;

    private static readonly string[] MetricIds =
    [
        "ai.kimi.quota.5h.remaining", "ai.kimi.quota.5h.reset",
        "ai.kimi.quota.7d.remaining", "ai.kimi.quota.7d.reset",
        "ai.kimi.quota.monthly.remaining", "ai.kimi.quota.monthly.reset"
    ];

    private static readonly MetricValueKind[] ValueKinds =
    [
        MetricValueKind.Double, MetricValueKind.DateTimeOffset,
        MetricValueKind.Double, MetricValueKind.DateTimeOffset,
        MetricValueKind.Double, MetricValueKind.DateTimeOffset
    ];

    private readonly object _stateGate = new();
    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly KimiQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Uri? _usageEndpoint;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly KimiQuotaPoint[] _points = new KimiQuotaPoint[6];
    private readonly DateTimeOffset[] _observedAt = new DateTimeOffset[6];
    private readonly CachedPoint?[] _cache = new CachedPoint?[6];
    // Usage is scoped to the authenticated Kimi Code account; no stable account or workspace ID is exposed here.
    // The token only separates in-memory request/cache generations; cache data is process-local and never persisted.
    private string? _activeAccessToken;
    private bool _activeHasRefreshToken;
    private string? _rejectedAccessToken;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private KimiAuthStatus? _activeAuthStatus;
    private long _authGeneration;
    private long _publicationGeneration;
    private long _nextPollTimestamp;
    private bool _disposed;
    private int _disposeStarted;

    public KimiQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new KimiQuotaAuthReader(), CreateHttpClient(), TimeProvider.System,
            RequestTimeout, ResolveUsageEndpoint(Environment.GetEnvironmentVariable("KIMI_CODE_BASE_URL")),
            ownsHttpClient: true)
    {
    }

    internal KimiQuotaProvider(MetricRegistry registry, MetricBus bus, KimiQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, TimeSpan? requestTimeout = null)
        : this(registry, bus, authReader, httpClient, timeProvider, requestTimeout ?? RequestTimeout,
            UsageEndpoint, ownsHttpClient: false)
    {
    }

    private KimiQuotaProvider(MetricRegistry registry, MetricBus bus, KimiQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, TimeSpan requestTimeout, Uri? usageEndpoint,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _usageEndpoint = usageEndpoint;
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _requestTimeout = requestTimeout;
        _ownsHttpClient = ownsHttpClient;

        var windows = new[] { "5-hour", "7-day", "monthly" };
        _descriptors = new MetricDescriptor[MetricIds.Length];
        for (var i = 0; i < MetricIds.Length; i++)
        {
            var isRemaining = i % 2 == 0;
            var window = windows[i / 2];
            _descriptors[i] = new MetricDescriptor(new MetricId(MetricIds[i]),
                $"Kimi Code {window} {(isRemaining ? "quota remaining" : "reset time")}",
                isRemaining ? "quota_remaining" : "timestamp", ValueKinds[i], unit: isRemaining ? "%" : null);
            if (!registry.TryRegister(_descriptors[i]))
                throw new InvalidOperationException("Kimi quota metric registration failed.");
            _points[i] = KimiQuotaPoint.Unavailable();
            _observedAt[i] = _timeProvider.GetUtcNow();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers =
        [
            PollLoopAsync(stop.Token),
            PublishLoopAsync(stop.Token),
            MonitorLocalStateLoopAsync(stop.Token)
        ];
        try
        {
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
        try { await PollCoreAsync(admission.Token).ConfigureAwait(false); }
        finally { _pollLock.Release(); }
        await PublishCurrentAsync(admission.Token).ConfigureAwait(false);
    }

    private async Task PollCoreAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        KimiAuthRead initialRead = _authReader.Read();
        string? accessToken;
        long generation;
        CancellationTokenSource requestStop;
        lock (_stateGate)
        {
            ApplyAuthReadLocked(initialRead, now);
            if (timestamp < _nextPollTimestamp) return;
            if (initialRead.Status != KimiAuthStatus.Ready || initialRead.Identity is null)
            {
                ScheduleNextPollLocked(PollInterval);
                return;
            }

            accessToken = initialRead.Identity.AccessToken;
            if (StringComparer.Ordinal.Equals(_rejectedAccessToken, accessToken))
            {
                ScheduleNextPollLocked(PollInterval);
                return;
            }
            if (_usageEndpoint is null)
            {
                ClearToStateLocked(MetricState.Error, "route_unsupported", now);
                ScheduleNextPollLocked(PollInterval);
                return;
            }

            generation = _authGeneration;
            requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRequestStop = requestStop;
        }

        byte[]? body = null;
        var failureCode = (string?)null;
        var failureState = MetricState.Error;
        TimeSpan? retryAfter = null;
        var ownerCancelled = false;
        try
        {
            using var deadline = new CancellationTokenSource(_requestTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestStop.Token, deadline.Token);
            HttpResponseMessage? response = null;
            try
            {
                using var request = CreateRequest(_usageEndpoint, accessToken);
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri != _usageEndpoint ||
                    response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                    failureCode = "redirect_rejected";
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    failureCode = "auth_rejected";
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                    failureCode = "auth_forbidden";
                else if ((int)response.StatusCode == 429)
                {
                    failureCode = "rate_limited";
                    retryAfter = ReadRetryAfter(response, _timeProvider.GetUtcNow());
                }
                else if (!response.IsSuccessStatusCode)
                {
                    failureCode = (int)response.StatusCode >= 500 ? "upstream_unavailable" : "http_failure";
                    if (failureCode == "upstream_unavailable") failureState = MetricState.Unavailable;
                }
                else
                {
                    var read = await ReadResponseBodyAsync(response, linked.Token).ConfigureAwait(false);
                    body = read.Body;
                    if (read.TooLarge) failureCode = "response_too_large";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (requestStop.IsCancellationRequested) { ownerCancelled = true; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { failureCode = "timeout"; }
            catch (HttpRequestException) { failureCode = "offline"; failureState = MetricState.Unavailable; }
            catch (IOException) { failureCode = "offline"; failureState = MetricState.Unavailable; }
            catch (Exception) { failureCode = "request_failed"; }
            finally { response?.Dispose(); }

            cancellationToken.ThrowIfCancellationRequested();
            var receivedAt = _timeProvider.GetUtcNow();
            var finalRead = _authReader.Read();
            lock (_stateGate)
            {
                ApplyAuthReadLocked(finalRead, receivedAt);
                var accepted = !ownerCancelled && _authGeneration == generation &&
                    finalRead.Status == KimiAuthStatus.Ready && finalRead.Identity is not null &&
                    StringComparer.Ordinal.Equals(finalRead.Identity.AccessToken, accessToken);
                if (accepted)
                {
                    if (failureCode is "auth_rejected" or "auth_forbidden")
                    {
                        _rejectedAccessToken = accessToken;
                        var authError = failureCode == "auth_rejected" && finalRead.Identity!.HasRefreshToken
                            ? "auth_refresh_pending"
                            : failureCode == "auth_rejected" ? "auth_required" : "auth_forbidden";
                        ClearToStateLocked(MetricState.Error, authError, receivedAt);
                    }
                    else if (failureCode is not null)
                    {
                        PreserveCacheOnFailureLocked(failureCode, failureState, receivedAt);
                    }
                    else if (body is null)
                    {
                        ClearToStateLocked(MetricState.Error, "response_invalid", receivedAt);
                    }
                    else
                    {
                        var reading = KimiQuotaParser.Parse(body, receivedAt);
                        ReplaceFromReadingLocked(reading, receivedAt, _timeProvider.GetTimestamp());
                    }
                }
                if (accepted) ScheduleNextPollLocked(PollDelay(retryAfter));
            }
        }
        finally
        {
            lock (_stateGate)
                if (ReferenceEquals(_activeRequestStop, requestStop)) _activeRequestStop = null;
            requestStop.Dispose();
            if (body is not null) CryptographicOperations.ZeroMemory(body);
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
            var now = _timeProvider.GetUtcNow();
            publication = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
            MetricSnapshot[] snapshots;
            long generation;
            lock (_stateGate)
            {
                ApplyAuthReadLocked(_authReader.Read(), now);
                ExpireCachesLocked(now, _timeProvider.GetTimestamp());
                InvalidatePendingPublicationLocked();
                _activePublishStop = publication;
                generation = _publicationGeneration;
                snapshots = new MetricSnapshot[_descriptors.Length];
                for (var i = 0; i < snapshots.Length; i++)
                {
                    var point = CurrentPointLocked(i, _timeProvider.GetTimestamp());
                    snapshots[i] = new MetricSnapshot(_descriptors[i].Id, point.Value,
                        _cache[i]?.ObservedAt ?? _observedAt[i], point.State,
                        point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                        source: "Kimi.Usage.ReadOnly.v1", errorCode: point.ErrorCode);
                }
            }

            try
            {
                foreach (var snapshot in snapshots)
                {
                    lock (_stateGate)
                        if (_publicationGeneration != generation) return;
                    await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
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
                lock (_stateGate)
                    if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null;
                publication.Dispose();
            }
            _publishLock.Release();
        }
    }

    internal ValueTask ObserveLocalStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _timeProvider.GetUtcNow();
        var read = _authReader.Read();
        lock (_stateGate)
        {
            ApplyAuthReadLocked(read, now);
            ExpireCachesLocked(now, _timeProvider.GetTimestamp());
        }
        return ValueTask.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorLocalStateLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await ObserveLocalStateAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyAuthReadLocked(KimiAuthRead read, DateTimeOffset now)
    {
        var nextToken = read.Status == KimiAuthStatus.Ready ? read.Identity?.AccessToken : null;
        var tokenChanged = !StringComparer.Ordinal.Equals(_activeAccessToken, nextToken);
        var nextHasRefreshToken = read.Status == KimiAuthStatus.Ready && read.Identity?.HasRefreshToken == true;
        var stateChanged = !_activeAuthStatus.HasValue || _activeAuthStatus.Value != read.Status || tokenChanged ||
            _activeHasRefreshToken != nextHasRefreshToken;
        if (stateChanged)
        {
            _authGeneration++;
            _activeRequestStop?.Cancel();
            InvalidatePendingPublicationLocked();
            ClearCacheLocked(now);
            _rejectedAccessToken = null;
            _nextPollTimestamp = 0;
        }

        _activeAuthStatus = read.Status;
        _activeAccessToken = nextToken;
        _activeHasRefreshToken = nextHasRefreshToken;
        if ((read.Status != KimiAuthStatus.Ready || read.Identity is null) && stateChanged)
        {
            var state = read.Status == KimiAuthStatus.Unavailable ? MetricState.Unavailable : MetricState.Error;
            ClearToStateLocked(state, state == MetricState.Error ? read.ErrorCode ?? "auth_unavailable" : null, now);
        }
    }

    private void ReplaceFromReadingLocked(KimiQuotaReading reading, DateTimeOffset observedAt, long timestamp)
    {
        InvalidatePendingPublicationLocked();
        for (var i = 0; i < _points.Length; i++)
        {
            var point = reading.Points[i];
            _points[i] = point;
            _observedAt[i] = observedAt;
            _cache[i] = point.State == MetricState.Fresh && point.Value.HasValue
                ? new CachedPoint(point.Value.Value, observedAt, timestamp)
                : null;
        }
    }

    private void PreserveCacheOnFailureLocked(string errorCode, MetricState noCacheState, DateTimeOffset now)
    {
        InvalidatePendingPublicationLocked();
        for (var i = 0; i < _points.Length; i++)
        {
            if (_cache[i] is CachedPoint cached)
            {
                _cache[i] = cached with { ForceStale = true, ErrorCode = errorCode };
                continue;
            }
            _points[i] = noCacheState == MetricState.Unavailable
                ? KimiQuotaPoint.Unavailable(errorCode)
                : KimiQuotaPoint.Error(errorCode);
            _observedAt[i] = now;
        }
    }

    private void ClearToStateLocked(MetricState state, string? errorCode, DateTimeOffset now)
    {
        InvalidatePendingPublicationLocked();
        for (var i = 0; i < _points.Length; i++)
        {
            _cache[i] = null;
            _points[i] = state switch
            {
                MetricState.Error => KimiQuotaPoint.Error(errorCode ?? "auth_unavailable"),
                MetricState.Stopped => new KimiQuotaPoint(MetricState.Stopped, null, null),
                _ => KimiQuotaPoint.Unavailable()
            };
            _observedAt[i] = now;
        }
    }

    private void ClearCacheLocked(DateTimeOffset now)
    {
        for (var i = 0; i < _cache.Length; i++)
        {
            _cache[i] = null;
            _points[i] = KimiQuotaPoint.Unavailable();
            _observedAt[i] = now;
        }
    }

    private void ExpireCachesLocked(DateTimeOffset now, long timestamp)
    {
        for (var remaining = 0; remaining < _cache.Length; remaining += 2)
        {
            var reset = _cache[remaining + 1] is CachedPoint resetCache
                ? resetCache.Value.AsDateTimeOffset()
                : (DateTimeOffset?)null;
            var resetElapsed = reset.HasValue && now >= reset.Value;
            var tooOld = Enumerable.Range(remaining, 2).Any(i => _cache[i] is CachedPoint cached &&
                _timeProvider.GetElapsedTime(cached.Timestamp, timestamp) >= MaximumCacheAge);
            if (!resetElapsed && !tooOld) continue;
            InvalidatePendingPublicationLocked();
            for (var i = remaining; i < remaining + 2; i++)
            {
                _cache[i] = null;
                _points[i] = KimiQuotaPoint.Unavailable(resetElapsed ? "reset_elapsed" : "cache_expired");
                _observedAt[i] = now;
            }
        }
    }

    private KimiQuotaPoint CurrentPointLocked(int index, long timestamp)
    {
        if (_cache[index] is not CachedPoint cached) return _points[index];
        var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
        return new KimiQuotaPoint(cached.ForceStale || age >= StaleAfter ? MetricState.Stale : MetricState.Fresh,
            cached.Value, cached.ErrorCode);
    }

    private void InvalidatePendingPublicationLocked()
    {
        _publicationGeneration++;
        _activePublishStop?.Cancel();
    }

    private void ScheduleNextPollLocked(TimeSpan delay)
    {
        if (delay < PollInterval) delay = PollInterval;
        var now = _timeProvider.GetTimestamp();
        var scaledTicks = delay.TotalSeconds * _timeProvider.TimestampFrequency;
        var ticks = !double.IsFinite(scaledTicks) || scaledTicks >= long.MaxValue
            ? long.MaxValue
            : (long)scaledTicks;
        _nextPollTimestamp = ticks > long.MaxValue - now ? long.MaxValue : now + ticks;
    }

    private static TimeSpan PollDelay(TimeSpan? retryAfter) => retryAfter is TimeSpan delay
        ? (delay < PollInterval ? PollInterval : delay)
        : PollInterval;

    internal static Uri? ResolveUsageEndpoint(string? configuredBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredBaseUrl)) return UsageEndpoint;
        if (!Uri.TryCreate(configuredBaseUrl.Trim(), UriKind.Absolute, out var configured) ||
            configured.Scheme != Uri.UriSchemeHttps || configured.UserInfo.Length != 0 ||
            configured.Query.Length != 0 || configured.Fragment.Length != 0)
            return null;

        var baseUrl = configured.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (StringComparer.OrdinalIgnoreCase.Equals(baseUrl, "https://api.kimi.com/coding/v1"))
            return UsageEndpoint;
        return null;
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var acquired = false;
        try
        {
            await _publishLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            var now = _timeProvider.GetUtcNow();
            MetricSnapshot[] snapshots;
            lock (_stateGate)
            {
                _activeAccessToken = null;
                _activeHasRefreshToken = false;
                _rejectedAccessToken = null;
                _activeAuthStatus = KimiAuthStatus.Unavailable;
                _activeRequestStop?.Cancel();
                InvalidatePendingPublicationLocked();
                ClearToStateLocked(MetricState.Stopped, null, now);
                snapshots = _descriptors.Select(descriptor => new MetricSnapshot(descriptor.Id, null, now,
                    MetricState.Stopped, MetricConfidence.Unknown, source: "Kimi.Usage.ReadOnly.v1")).ToArray();
            }
            foreach (var snapshot in snapshots)
                await _bus.PublishAsync(snapshot, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally { if (acquired) _publishLock.Release(); }
    }

    private static HttpRequestMessage CreateRequest(Uri endpoint, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
        }
        catch (FormatException) { }
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        _shutdown.Cancel();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record CachedPoint(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp,
        bool ForceStale = false, string? ErrorCode = null);
}
