#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal sealed class CursorQuotaProvider : IDisposable
{
    internal static readonly Uri UsageSummaryEndpoint = new("https://cursor.com/api/usage-summary");
    internal static readonly Uri SandAccessEndpoint = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandAccessStatus");
    internal static readonly Uri SandUsageEndpoint = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 64 * 1024;

    private static readonly string[] MetricIds =
    [
        "ai.cursor.quota.plan.total.used_percent",
        "ai.cursor.quota.plan.auto.used_percent",
        "ai.cursor.quota.plan.api.used_percent",
        "ai.cursor.quota.plan.reset",
        "ai.cursor.quota.plan.duration",
        "ai.cursor.quota.grok_bot.period.used_percent",
        "ai.cursor.quota.grok_bot.period.reset",
        "ai.cursor.quota.grok_bot.period.duration"
    ];

    private static readonly string[] Sources =
    [
        "Cursor.UsageSummary.v1", "Cursor.UsageSummary.v1", "Cursor.UsageSummary.v1",
        "Cursor.UsageSummary.v1", "Cursor.UsageSummary.v1",
        "Cursor.GrokBot.UsageStatus.v1", "Cursor.GrokBot.UsageStatus.v1", "Cursor.GrokBot.UsageStatus.v1"
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly CursorQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _ownsHttpClient;
    private readonly CursorQuotaPoint[] _points = new CursorQuotaPoint[MetricIds.Length];
    private readonly CachedPoint?[] _cache = new CachedPoint?[MetricIds.Length];
    private readonly DateTimeOffset[] _observedAt = new DateTimeOffset[MetricIds.Length];
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly SemaphoreSlim _stateAndPublish = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _activePublishStop;
    private string? _activeOwner;
    private long _ownerGeneration;
    private long _publicationGeneration;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _rateLimitFailures;
    private int _runStarted;
    private int _disposeStarted;
    private volatile bool _disposed;

    public CursorQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new CursorQuotaAuthReader(), CreateHttpClient(), TimeProvider.System,
            RequestTimeout, ownsHttpClient: true)
    {
    }

    internal CursorQuotaProvider(MetricRegistry registry, MetricBus bus, CursorQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, TimeSpan? requestTimeout = null)
        : this(registry, bus, authReader, httpClient, timeProvider, requestTimeout ?? RequestTimeout,
            ownsHttpClient: false)
    {
    }

    private CursorQuotaProvider(MetricRegistry registry, MetricBus bus, CursorQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, TimeSpan requestTimeout, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _requestTimeout = requestTimeout;
        _ownsHttpClient = ownsHttpClient;

        _descriptors =
        [
            Descriptor(0, "Cursor plan total used percent", MetricValueKind.Double, "quota_used", "%"),
            Descriptor(1, "Cursor plan Auto used percent", MetricValueKind.Double, "quota_used", "%"),
            Descriptor(2, "Cursor plan API used percent", MetricValueKind.Double, "quota_used", "%"),
            Descriptor(3, "Cursor plan reset", MetricValueKind.DateTimeOffset, "quota_reset", null),
            Descriptor(4, "Cursor plan duration", MetricValueKind.TimeSpan, "quota_duration", "s"),
            Descriptor(5, "Cursor Grok Bot period used percent", MetricValueKind.Double, "quota_used", "%"),
            Descriptor(6, "Cursor Grok Bot period reset", MetricValueKind.DateTimeOffset, "quota_reset", null),
            Descriptor(7, "Cursor Grok Bot period duration", MetricValueKind.TimeSpan, "quota_duration", "s")
        ];

        for (var i = 0; i < _descriptors.Length; i++)
        {
            _points[i] = CursorQuotaPoint.Unavailable();
            _observedAt[i] = _timeProvider.GetUtcNow();
            if (!registry.TryRegister(_descriptors[i]))
                throw new InvalidOperationException("Cursor quota metric registration failed.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("Cursor quota provider can only run once.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers = [];
        try
        {
            workers = [MonitorLoopAsync(linked.Token), PollLoopAsync(linked.Token), PublishLoopAsync(linked.Token)];
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            linked.Cancel();
            try
            {
                try { await Task.WhenAll(workers).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
            finally { await PublishStoppedAsync().ConfigureAwait(false); }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ObserveLocalStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            if (_timeProvider.GetTimestamp() >= Volatile.Read(ref _nextPollTimestamp))
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        byte[]? planBody = null;
        byte[]? accessBody = null;
        byte[]? sandBody = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CursorAuthRead authRead;
            long requestGeneration;
            var retryPending = false;
            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                authRead = _authReader.Read();
                ApplyAuthReadLocked(authRead, _timeProvider.GetUtcNow());
                requestGeneration = _ownerGeneration;
                var timestamp = _timeProvider.GetTimestamp();
                retryPending = timestamp < _retryNotBeforeTimestamp || timestamp < _nextPollTimestamp;
                if (authRead.Status != CursorAuthStatus.Ready || authRead.Identity is null || retryPending)
                {
                    if (!retryPending) ScheduleNextPoll();
                }
            }
            finally { _stateAndPublish.Release(); }

            if (authRead.Status != CursorAuthStatus.Ready || authRead.Identity is null || retryPending)
            {
                await PublishCurrentAsync(admission.Token).ConfigureAwait(false);
                return;
            }

            var auth = authRead.Identity!;
            var generationOwner = auth.AccountId;
            var planTask = SendUsageSummaryAsync(auth, admission.Token);
            var accessTask = SendSandAccessAsync(auth, admission.Token);
            try { await Task.WhenAll(planTask, accessTask).ConfigureAwait(false); }
            catch
            {
                if (planTask.IsCompletedSuccessfully) CryptographicOperations.ZeroMemory(planTask.Result.Body);
                if (accessTask.IsCompletedSuccessfully) CryptographicOperations.ZeroMemory(accessTask.Result.Body);
                throw;
            }
            var planResult = await planTask.ConfigureAwait(false);
            var accessResult = await accessTask.ConfigureAwait(false);
            planBody = planResult.Body;
            accessBody = accessResult.Body;

            var sandResult = accessResult;
            var granted = false;
            if (accessResult.Body is not null)
            {
                var access = CursorQuotaParser.ParseSandAccess(accessResult.Body);
                if (access.ErrorCode is not null)
                    sandResult = TransportResult.Failure(access.ErrorCode);
                else if (!access.Granted)
                    sandResult = TransportResult.Unavailable();
                else
                {
                    granted = true;
                    sandResult = await SendSandUsageAsync(auth, admission.Token).ConfigureAwait(false);
                    sandBody = sandResult.Body;
                }
            }

            var receivedAt = _timeProvider.GetUtcNow();
            CursorAuthRead finalAuth;
            var accepted = false;
            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                finalAuth = _authReader.Read();
                ApplyAuthReadLocked(finalAuth, receivedAt);
                accepted = _ownerGeneration == requestGeneration &&
                    finalAuth.Status == CursorAuthStatus.Ready && finalAuth.Identity is not null &&
                    StringComparer.Ordinal.Equals(generationOwner, finalAuth.Identity.AccountId);
                if (accepted)
                {
                    InvalidatePublicationLocked();
                    ApplyPlanResult(planResult, receivedAt);
                    ApplySandResult(sandResult, granted, receivedAt);
                    if (planResult.ErrorCode == "rate_limited" || sandResult.ErrorCode == "rate_limited")
                    {
                        _rateLimitFailures++;
                        SetRetryNotBefore(MaxRetryAfter(planResult.RetryAfter, sandResult.RetryAfter) ??
                            GetBackoffDelay(_rateLimitFailures));
                    }
                    else
                    {
                        _rateLimitFailures = 0;
                        _retryNotBeforeTimestamp = 0;
                    }
                }
                ScheduleNextPoll();
            }
            finally { _stateAndPublish.Release(); }

            await PublishCurrentAsync(admission.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(planBody);
            CryptographicOperations.ZeroMemory(accessBody);
            CryptographicOperations.ZeroMemory(sandBody);
            _pollLock.Release();
        }
    }

    internal async Task ObserveLocalStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _stateAndPublish.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            ApplyAuthReadLocked(_authReader.Read(), now);
            ExpireCachesLocked(now);
        }
        finally { _stateAndPublish.Release(); }
    }

    private bool ApplyAuthReadLocked(CursorAuthRead read, DateTimeOffset observedAt)
    {
        if (read.Status == CursorAuthStatus.Ready && read.Identity is not null)
        {
            if (_activeOwner is null)
            {
                _activeOwner = read.Identity.AccountId;
                _ownerGeneration++;
                ResetPollScheduleLocked();
                InvalidatePublicationLocked();
                return true;
            }
            if (StringComparer.Ordinal.Equals(_activeOwner, read.Identity.AccountId)) return false;

            ClearAll(observedAt, CursorQuotaPoint.Unavailable());
            _activeOwner = read.Identity.AccountId;
            _ownerGeneration++;
            ResetPollScheduleLocked();
            return true;
        }

        var next = read.Status == CursorAuthStatus.Unavailable
            ? CursorQuotaPoint.Unavailable()
            : CursorQuotaPoint.Error(read.ErrorCode ?? "auth_unavailable");
        var hadOwner = _activeOwner is not null;
        var changed = hadOwner || Array.Exists(_points, point => point.State != next.State || point.ErrorCode != next.ErrorCode);
        if (changed)
        {
            ClearAll(observedAt, next);
            _ownerGeneration++;
            ResetPollScheduleLocked();
        }
        _activeOwner = null;
        return changed;
    }

    private void ApplyPlanResult(TransportResult result, DateTimeOffset observedAt)
    {
        if (result.Body is null)
        {
            ApplyFailure(0, CursorQuotaParser.PlanPointCount, result, observedAt);
            return;
        }

        var points = CursorQuotaParser.ParseUsageSummary(result.Body, observedAt);
        var resetAt = points[3].Value is MetricValue resetValue && points[3].State == MetricState.Fresh
            ? resetValue.AsDateTimeOffset()
            : (DateTimeOffset?)null;
        for (var i = 0; i < points.Length; i++) SetPoint(i, points[i], observedAt, resetAt);
    }

    private void ApplySandResult(TransportResult result, bool granted, DateTimeOffset observedAt)
    {
        if (result.Body is null)
        {
            if (result.ErrorCode is null)
            {
                for (var i = CursorQuotaParser.PlanPointCount; i < MetricIds.Length; i++)
                    SetPoint(i, CursorQuotaPoint.Unavailable(), observedAt, null);
                return;
            }
            ApplyFailure(CursorQuotaParser.PlanPointCount, CursorQuotaParser.SandPointCount, result, observedAt);
            return;
        }

        var points = CursorQuotaParser.ParseSandUsage(result.Body, granted, observedAt);
        var resetAt = points[1].Value is MetricValue resetValue && points[1].State == MetricState.Fresh
            ? resetValue.AsDateTimeOffset()
            : (DateTimeOffset?)null;
        for (var i = 0; i < points.Length; i++)
            SetPoint(CursorQuotaParser.PlanPointCount + i, points[i], observedAt, resetAt);
    }

    private void ApplyFailure(int start, int count, TransportResult result, DateTimeOffset observedAt)
    {
        var cacheable = result.ErrorCode is "offline" or "timeout" or "upstream_unavailable" or "rate_limited";
        for (var i = start; i < start + count; i++)
        {
            if (cacheable && TryGetCache(i, observedAt, out var cached))
            {
                _points[i] = CursorQuotaPoint.Stale(cached.Value, result.ErrorCode!);
                _observedAt[i] = cached.ObservedAt;
            }
            else
            {
                _cache[i] = null;
                var state = result.ErrorCode == "offline" ? MetricState.Unavailable : MetricState.Error;
                _points[i] = state == MetricState.Unavailable
                    ? CursorQuotaPoint.Unavailable(result.ErrorCode)
                    : CursorQuotaPoint.Error(result.ErrorCode ?? "request_failed");
                _observedAt[i] = observedAt;
            }
        }
    }

    private void SetPoint(int index, CursorQuotaPoint point, DateTimeOffset observedAt, DateTimeOffset? resetAt)
    {
        _points[index] = point;
        _observedAt[index] = observedAt;
        _cache[index] = point.State == MetricState.Fresh && point.Value is MetricValue value
            ? new CachedPoint(value, observedAt, _timeProvider.GetTimestamp(), resetAt)
            : null;
    }

    private bool TryGetCache(int index, DateTimeOffset now, out CachedPoint cached)
    {
        var candidate = _cache[index];
        if (candidate is not null && IsCacheValid(candidate, now))
        {
            cached = candidate;
            return true;
        }
        _cache[index] = null;
        cached = null!;
        return false;
    }

    private bool ExpireCachesLocked(DateTimeOffset now)
    {
        var changed = false;
        for (var i = 0; i < _cache.Length; i++)
        {
            var cached = _cache[i];
            if (cached is null || IsCacheValid(cached, now)) continue;

            _cache[i] = null;
            var code = now < cached.ObservedAt ? "clock_discontinuity" :
                cached.ResetAt is DateTimeOffset reset && reset <= now ? "reset_elapsed" : "cache_expired";
            _points[i] = CursorQuotaPoint.Unavailable(code);
            _observedAt[i] = now;
            InvalidatePublicationLocked();
            changed = true;
        }
        return changed;
    }

    private bool IsCacheValid(CachedPoint cached, DateTimeOffset now) =>
        now >= cached.ObservedAt &&
        _timeProvider.GetElapsedTime(cached.Timestamp, _timeProvider.GetTimestamp()) < MaximumCacheAge &&
        (cached.ResetAt is null || cached.ResetAt > now);

    private void ResetPollScheduleLocked()
    {
        _retryNotBeforeTimestamp = 0;
        _nextPollTimestamp = 0;
        _rateLimitFailures = 0;
    }

    private void ClearAll(DateTimeOffset observedAt, CursorQuotaPoint point)
    {
        InvalidatePublicationLocked();
        Array.Clear(_cache);
        for (var i = 0; i < _points.Length; i++)
        {
            _points[i] = point;
            _observedAt[i] = observedAt;
        }
    }

    private void InvalidatePublicationLocked()
    {
        _publicationGeneration++;
        _activePublishStop?.Cancel();
    }

    private async Task<TransportResult> SendUsageSummaryAsync(CursorAuthIdentity auth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageSummaryEndpoint);
        request.Headers.TryAddWithoutValidation("Cookie", auth.CookieValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Referrer = new Uri("https://www.cursor.com/settings");
        return await SendAsync(request, UsageSummaryEndpoint, "application/json", cancellationToken).ConfigureAwait(false);
    }

    private Task<TransportResult> SendSandAccessAsync(CursorAuthIdentity auth, CancellationToken cancellationToken) =>
        SendSandRpcAsync(auth, SandAccessEndpoint, cancellationToken);

    private Task<TransportResult> SendSandUsageAsync(CursorAuthIdentity auth, CancellationToken cancellationToken) =>
        SendSandRpcAsync(auth, SandUsageEndpoint, cancellationToken);

    private Task<TransportResult> SendSandRpcAsync(CursorAuthIdentity auth, Uri endpoint,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");
        return SendAndDisposeAsync(request, endpoint, "application/proto", cancellationToken);
    }

    private async Task<TransportResult> SendAndDisposeAsync(HttpRequestMessage request, Uri endpoint,
        string expectedContentType, CancellationToken cancellationToken)
    {
        using (request)
            return await SendAsync(request, endpoint, expectedContentType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransportResult> SendAsync(HttpRequestMessage request, Uri endpoint,
        string expectedContentType, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_requestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != endpoint)
                return TransportResult.Failure("redirect_rejected");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return TransportResult.Failure("auth_rejected");
            if ((int)response.StatusCode == 429)
                return TransportResult.Failure("rate_limited", ReadRetryAfter(response, _timeProvider.GetUtcNow()));
            if ((int)response.StatusCode is >= 300 and < 400)
                return TransportResult.Failure("redirect_rejected");
            if (!response.IsSuccessStatusCode)
                return TransportResult.Failure((int)response.StatusCode >= 500
                    ? "upstream_unavailable"
                    : "http_failure");

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!StringComparer.OrdinalIgnoreCase.Equals(contentType, expectedContentType))
                return TransportResult.Failure("content_type_invalid");

            var body = await ReadResponseBodyAsync(response, linked.Token).ConfigureAwait(false);
            return body is null
                ? TransportResult.Failure("response_too_large")
                : TransportResult.Success(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return TransportResult.Failure("timeout");
        }
        catch (HttpRequestException) { return TransportResult.Failure("offline"); }
        catch (IOException) { return TransportResult.Failure("offline"); }
        catch (OperationCanceledException) { return TransportResult.Failure("request_failed"); }
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

    private static async Task<byte[]?> ReadResponseBodyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxResponseBytes + 1);
        try
        {
            var count = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count, MaxResponseBytes + 1 - count),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
                if (count > MaxResponseBytes) return null;
            }
            return buffer.AsSpan(0, count).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private TimeSpan GetBackoffDelay(int failures)
    {
        var seconds = Math.Min(60d * Math.Pow(2, Math.Min(failures - 1, 20)), TimeSpan.FromMinutes(15).TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private void SetRetryNotBefore(TimeSpan requestedDelay)
    {
        var timestampDelta = Math.Max(0d, requestedDelay.TotalSeconds) * _timeProvider.TimestampFrequency;
        var ticks = !double.IsFinite(timestampDelta) || timestampDelta >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(timestampDelta);
        var now = _timeProvider.GetTimestamp();
        _retryNotBeforeTimestamp = ticks > long.MaxValue - now ? long.MaxValue : now + ticks;
    }

    private static TimeSpan? MaxRetryAfter(TimeSpan? first, TimeSpan? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private void ScheduleNextPoll()
    {
        var now = _timeProvider.GetTimestamp();
        var intervalTicks = (long)(PollInterval.TotalSeconds * _timeProvider.TimestampFrequency);
        var regular = intervalTicks > long.MaxValue - now ? long.MaxValue : now + intervalTicks;
        _nextPollTimestamp = Math.Max(regular, _retryNotBeforeTimestamp);
    }

    private MetricDescriptor Descriptor(int index, string displayName, MetricValueKind kind,
        string semanticType, string? unit) => new(new MetricId(MetricIds[index]), displayName, semanticType,
        kind, unit: unit);

    internal async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _publishLock.WaitAsync(admission.Token).ConfigureAwait(false);
        using var publication = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
        var snapshots = new MetricSnapshot[_descriptors.Length];
        long generation;
        try
        {
            await _stateAndPublish.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = _timeProvider.GetUtcNow();
                ApplyAuthReadLocked(_authReader.Read(), now);
                _ = ExpireCachesLocked(now);
                InvalidatePublicationLocked();
                _activePublishStop = publication;
                generation = _publicationGeneration;
                for (var i = 0; i < _descriptors.Length; i++)
                {
                    var point = _points[i];
                    var hasValue = point.Value.HasValue;
                    snapshots[i] = new MetricSnapshot(_descriptors[i].Id, point.Value, _observedAt[i], point.State,
                        hasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                        source: Sources[i], errorCode: point.ErrorCode);
                }
            }
            finally { _stateAndPublish.Release(); }

            foreach (var snapshot in snapshots)
            {
                await _stateAndPublish.WaitAsync(publication.Token).ConfigureAwait(false);
                try
                {
                    if (generation != _publicationGeneration || !ReferenceEquals(_activePublishStop, publication))
                        return;
                }
                finally { _stateAndPublish.Release(); }
                await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!admission.IsCancellationRequested && publication.IsCancellationRequested)
        {
        }
        finally
        {
            await _stateAndPublish.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null;
            }
            finally { _stateAndPublish.Release(); }
            _publishLock.Release();
        }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250), _timeProvider);
        var publishLockAcquired = false;
        var stateLockAcquired = false;
        try
        {
            await _publishLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            publishLockAcquired = true;
            await _stateAndPublish.WaitAsync(deadline.Token).ConfigureAwait(false);
            stateLockAcquired = true;
            InvalidatePublicationLocked();
            _activeOwner = null;
            _ownerGeneration++;
            ClearAll(_timeProvider.GetUtcNow(), CursorQuotaPoint.Unavailable());
            var now = _timeProvider.GetUtcNow();
            var snapshots = new MetricSnapshot[_descriptors.Length];
            for (var i = 0; i < _descriptors.Length; i++)
                snapshots[i] = new MetricSnapshot(_descriptors[i].Id, null, now,
                    MetricState.Stopped, MetricConfidence.Unknown, source: Sources[i]);

            _stateAndPublish.Release();
            stateLockAcquired = false;
            var publications = new Task[snapshots.Length];
            for (var i = 0; i < snapshots.Length; i++)
                publications[i] = _bus.PublishAsync(snapshots[i], deadline.Token).AsTask();
            await Task.WhenAll(publications).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (stateLockAcquired) _stateAndPublish.Release();
            if (publishLockAcquired) _publishLock.Release();
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
            InvalidatePublicationLocked();
            _activeOwner = null;
            _ownerGeneration++;
            Array.Clear(_cache);
            Array.Clear(_points);
            Array.Clear(_observedAt);
        }
        finally
        {
            _stateAndPublish.Release();
            _publishLock.Release();
            _pollLock.Release();
        }
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record CachedPoint(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp,
        DateTimeOffset? ResetAt);

    private sealed record TransportResult(byte[]? Body, string? ErrorCode, TimeSpan? RetryAfter)
    {
        public static TransportResult Success(byte[] body) => new(body, null, null);
        public static TransportResult Failure(string code, TimeSpan? retryAfter = null) => new(null, code, retryAfter);
        public static TransportResult Unavailable() => new(null, null, null);
    }
}
