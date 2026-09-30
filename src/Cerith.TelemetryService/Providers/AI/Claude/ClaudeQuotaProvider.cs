#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Claude;

internal sealed class ClaudeQuotaProvider : IDisposable
{
    internal static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    internal static readonly Uri ProfileEndpoint = new("https://api.anthropic.com/api/oauth/profile");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxUsageResponseBytes = 512 * 1024;
    internal const int MaxProfileResponseBytes = 128 * 1024;

    private const string Source = "Claude.OAuthUsage.ReadOnly.R1";
    private static readonly (string Id, string Name, string Semantic, MetricValueKind Kind, string? Unit)[] BaseMetrics =
    [
        ("ai.claude.session.remaining", "Claude Session Remaining", "quota_remaining", MetricValueKind.Double, "%"),
        ("ai.claude.session.reset", "Claude Session Reset At", "timestamp", MetricValueKind.DateTimeOffset, null),
        ("ai.claude.weekly.remaining", "Claude Weekly Remaining", "quota_remaining", MetricValueKind.Double, "%"),
        ("ai.claude.weekly.reset", "Claude Weekly Reset At", "timestamp", MetricValueKind.DateTimeOffset, null)
    ];

    private readonly MetricBus _bus;
    private readonly MetricRegistry _registry;
    private readonly ClaudeQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, MetricDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedPoint> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClaudeQuotaPoint> _points = new(StringComparer.Ordinal);
    private ClaudeAuthIdentity? _authIdentity;
    private ClaudeProfileIdentity? _owner;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _rejectedToken;
    private long _generation;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _rateLimitFailures;
    private DateTimeOffset? _lastClockUtc;
    private long _lastClockTimestamp;
    private bool _clockUncertain;
    private bool _disposed;

    public ClaudeQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new ClaudeQuotaAuthReader(), CreateHttpClient(), TimeProvider.System, ownsHttpClient: true)
    {
    }

    internal ClaudeQuotaProvider(MetricRegistry registry, MetricBus bus, ClaudeQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider)
        : this(registry, bus, authReader, httpClient, timeProvider, ownsHttpClient: false)
    {
    }

    private ClaudeQuotaProvider(MetricRegistry registry, MetricBus bus, ClaudeQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, bool ownsHttpClient)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownsHttpClient = ownsHttpClient;
        foreach (var metric in BaseMetrics)
        {
            RegisterDescriptor(new MetricDescriptor(new MetricId(metric.Id), metric.Name, metric.Semantic,
                metric.Kind, unit: metric.Unit));
            _points[metric.Id] = ClaudeQuotaReading.Unavailable(metric.Id, metric.Name, metric.Kind);
        }
        _lastClockUtc = _timeProvider.GetUtcNow();
        _lastClockTimestamp = _timeProvider.GetTimestamp();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Exception? failure = null;
        Task[] workers = [];
        try
        {
            workers = [PollLoopAsync(stop.Token), MonitorLocalStateLoopAsync(stop.Token)];
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try { stop.Cancel(); }
            catch (Exception stopError)
            {
                failure = failure is null ? stopError : new AggregateException("Provider cancellation failed.", failure, stopError);
            }
            try
            {
                var drain = Task.WhenAll(workers);
                try { await drain.ConfigureAwait(false); }
                catch (Exception drainError) { failure = drain.Exception ?? failure ?? drainError; }
            }
            finally
            {
                try { await PublishStoppedAsync().ConfigureAwait(false); }
                catch (Exception stoppedError)
                {
                    failure = failure is null ? stoppedError :
                        new AggregateException("Provider execution and stopped publication failed.", failure, stoppedError);
                }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(PublishInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorLocalStateLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var auth = _authReader.Read();
            lock (_stateLock)
            {
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(now, timestamp);
                ApplyAuthReadLocked(auth, now);
                ExpireCacheLocked(now, timestamp);
            }
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? requestStop = null;
        ClaudeAuthIdentity? requestAuth = null;
        try
        {
            var authRead = _authReader.Read();
            bool shouldRequest;
            lock (_stateLock)
            {
                var initialUtc = _timeProvider.GetUtcNow();
                var initialTimestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(initialUtc, initialTimestamp);
                ApplyAuthReadLocked(authRead, initialUtc);
                ExpireCacheLocked(initialUtc, initialTimestamp);
                requestAuth = authRead.Status == ClaudeAuthStatus.Ready ? authRead.Identity : null;
                shouldRequest = requestAuth is not null &&
                    !StringComparer.Ordinal.Equals(_rejectedToken, requestAuth.AccessToken) &&
                    initialTimestamp >= _nextPollTimestamp && initialTimestamp >= _retryNotBeforeTimestamp;
                if (shouldRequest)
                {
                    requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                    _activeRequestStop = requestStop;
                    _nextPollTimestamp = AddTimestamp(initialTimestamp, PollInterval);
                }
            }

            if (!shouldRequest || requestAuth is null || requestStop is null)
            {
                await PublishCurrentAsync(admission.Token).ConfigureAwait(false);
                return;
            }

            using var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestStop.Token, deadline.Token);
            AcquisitionOutcome outcome;
            try { outcome = await AcquireAsync(requestAuth, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !requestStop.IsCancellationRequested)
            {
                outcome = AcquisitionOutcome.Failure("timeout", MetricState.Error);
            }
            admission.Token.ThrowIfCancellationRequested();

            var finalRead = _authReader.Read();
            var now = _timeProvider.GetUtcNow();
            var timestamp = _timeProvider.GetTimestamp();
            List<MetricSnapshot> snapshots;
            long generation;
            lock (_stateLock)
            {
                ObserveClockLocked(now, timestamp);
                ApplyAuthReadLocked(finalRead, now);
                ExpireCacheLocked(now, timestamp);
                var stillSameAuth = finalRead.Status == ClaudeAuthStatus.Ready &&
                    SameAuth(requestAuth, finalRead.Identity!);
                var stillSameRequest = stillSameAuth && !requestStop.IsCancellationRequested;

                if (!stillSameRequest)
                {
                    if (stillSameAuth && outcome.Profile is not null && _owner is null)
                        SetOwnerLocked(outcome.Profile.Value, now);
                }
                else if (outcome.FailureCode is "identity_changed" or "identity_mismatch" or "identity_unavailable")
                {
                    ClearCacheLocked(now);
                    _owner = null;
                    InvalidateGenerationLocked(cancelRequest: false);
                    SetAllStateLocked(MetricState.Error, outcome.FailureCode, now);
                }
                else if (outcome.FailureCode is "auth_required" or "auth_forbidden")
                {
                    ClearCacheLocked(now);
                    SetAllStateLocked(MetricState.Error, outcome.FailureCode, now);
                    _rejectedToken = requestAuth.AccessToken;
                    _retryNotBeforeTimestamp = 0;
                    _rateLimitFailures = 0;
                }
                else if (outcome.FailureCode is not null)
                {
                    ApplyFailureLocked(outcome.FailureCode, outcome.NoCacheState, now, timestamp);
                    if (outcome.FailureCode == "rate_limited")
                    {
                        _rateLimitFailures++;
                        _retryNotBeforeTimestamp = AddTimestamp(timestamp,
                            outcome.RetryAfter ?? BackoffDelay(_rateLimitFailures));
                    }
                    else
                    {
                        _rateLimitFailures = 0;
                        _retryNotBeforeTimestamp = 0;
                    }
                }
                else if (outcome.Reading is not null && outcome.Profile is not null &&
                    _owner is not null && _owner == outcome.Profile)
                {
                    ReplaceReadingLocked(outcome.Reading, now, timestamp);
                    _rateLimitFailures = 0;
                    _retryNotBeforeTimestamp = 0;
                    _rejectedToken = null;
                }
                else
                {
                    ClearCacheLocked(now);
                    _owner = null;
                    InvalidateGenerationLocked(cancelRequest: false);
                    SetAllStateLocked(MetricState.Error, "identity_mismatch", now);
                }
                snapshots = CreateSnapshotsLocked(now, timestamp);
                generation = _generation;
            }
            await PublishGenerationAsync(snapshots, generation, admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
            !_shutdown.IsCancellationRequested && requestStop?.IsCancellationRequested == true)
        {
            await PublishCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (requestStop is not null)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activeRequestStop, requestStop)) _activeRequestStop = null;
                }
                requestStop.Dispose();
            }
            _pollLock.Release();
        }
    }

    internal async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var authRead = _authReader.Read();
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        List<MetricSnapshot> snapshots;
        long generation;
        lock (_stateLock)
        {
            ObserveClockLocked(now, timestamp);
            ApplyAuthReadLocked(authRead, now);
            ExpireCacheLocked(now, timestamp);
            snapshots = CreateSnapshotsLocked(now, timestamp);
            generation = _generation;
        }
        await PublishGenerationAsync(snapshots, generation, admission.Token).ConfigureAwait(false);
    }

    internal Task ObserveLocalStateAsync(CancellationToken cancellationToken = default) =>
        PublishCurrentAsync(cancellationToken);

    private async Task<AcquisitionOutcome> AcquireAsync(ClaudeAuthIdentity auth, CancellationToken cancellationToken)
    {
        var firstProfile = await FetchProfileAsync(auth.AccessToken, cancellationToken).ConfigureAwait(false);
        if (firstProfile.FailureCode is not null) return firstProfile;
        var profile = firstProfile.Profile!.Value;

        var currentRead = _authReader.Read();
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        List<MetricSnapshot>? transition = null;
        long generation = 0;
        lock (_stateLock)
        {
            ApplyAuthReadLocked(currentRead, now);
            if (currentRead.Status != ClaudeAuthStatus.Ready || !SameAuth(auth, currentRead.Identity!))
                return AcquisitionOutcome.Cancelled;
            if (_owner is not null && _owner != profile)
            {
                ClearCacheLocked(now);
                SetOwnerLocked(profile, now);
                InvalidateGenerationLocked(cancelRequest: false);
                transition = CreateSnapshotsLocked(now, timestamp);
            }
            else if (_owner is null)
            {
                SetOwnerLocked(profile, now);
                InvalidateGenerationLocked(cancelRequest: false);
            }
            generation = _generation;
        }
        if (transition is not null)
            await PublishGenerationAsync(transition, generation, cancellationToken).ConfigureAwait(false);

        var usage = await FetchUsageAsync(auth.AccessToken, cancellationToken).ConfigureAwait(false);
        if (usage.FailureCode is not null) return usage with { Profile = profile };
        try
        {
            var lastProfile = await FetchProfileAsync(auth.AccessToken, cancellationToken).ConfigureAwait(false);
            if (lastProfile.FailureCode is not null) return lastProfile with { Profile = profile };
            if (lastProfile.Profile != profile)
                return AcquisitionOutcome.Failure("identity_changed", MetricState.Error);

            var reading = ClaudeQuotaParser.Parse(usage.Body!, _timeProvider.GetUtcNow());
            return AcquisitionOutcome.Success(profile, reading);
        }
        finally { CryptographicOperations.ZeroMemory(usage.Body!); }
    }

    private Task<AcquisitionOutcome> FetchUsageAsync(string token, CancellationToken cancellationToken) =>
        FetchJsonAsync(UsageEndpoint, token, MaxUsageResponseBytes, cancellationToken);

    private async Task<AcquisitionOutcome> FetchProfileAsync(string token, CancellationToken cancellationToken)
    {
        var outcome = await FetchJsonAsync(ProfileEndpoint, token, MaxProfileResponseBytes, cancellationToken).ConfigureAwait(false);
        if (outcome.FailureCode is not null) return outcome;
        try
        {
            using var document = JsonDocument.Parse(outcome.Body!, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadUuid(root, "account", out var account) || !TryReadUuid(root, "organization", out var organization))
                return AcquisitionOutcome.Failure("identity_unavailable", MetricState.Error);
            return AcquisitionOutcome.ProfileOnly(new ClaudeProfileIdentity(account, organization));
        }
        catch (JsonException) { return AcquisitionOutcome.Failure("schema_invalid", MetricState.Error); }
        finally { CryptographicOperations.ZeroMemory(outcome.Body!); }
    }

    private async Task<AcquisitionOutcome> FetchJsonAsync(Uri endpoint, string token, int maxBytes,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.RequestMessage?.RequestUri != endpoint)
                return AcquisitionOutcome.Failure("redirect_rejected", MetricState.Error);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return AcquisitionOutcome.Failure("auth_required", MetricState.Error);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return AcquisitionOutcome.Failure("auth_forbidden", MetricState.Error);
            if ((int)response.StatusCode == 429)
                return AcquisitionOutcome.Failure("rate_limited", MetricState.Error, ReadRetryAfter(response));
            if ((int)response.StatusCode is >= 300 and < 400)
                return AcquisitionOutcome.Failure("redirect_rejected", MetricState.Error);
            if (!response.IsSuccessStatusCode)
            {
                var unavailable = (int)response.StatusCode >= 500;
                return AcquisitionOutcome.Failure(unavailable ? "upstream_unavailable" : "http_failure",
                    unavailable ? MetricState.Unavailable : MetricState.Error);
            }

            var read = await ReadBoundedBodyAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
            return read.TooLarge
                ? AcquisitionOutcome.Failure("response_too_large", MetricState.Error)
                : AcquisitionOutcome.BodyOnly(read.Body!);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return AcquisitionOutcome.Failure("offline", MetricState.Unavailable); }
        catch (IOException) { return AcquisitionOutcome.Failure("offline", MetricState.Unavailable); }
        finally { response?.Dispose(); }
    }

    private static async Task<BodyRead> ReadBoundedBodyAsync(HttpResponseMessage response, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            return BodyRead.Oversized;

        var buffer = new byte[maximumBytes + 1];
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var offset = 0;
            while (offset <= maximumBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, Math.Min(8192, buffer.Length - offset)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            if (offset > maximumBytes) return BodyRead.Oversized;
            var body = new byte[offset];
            Buffer.BlockCopy(buffer, 0, body, 0, offset);
            return new BodyRead(body, false);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static bool TryReadUuid(JsonElement root, string containerName, out Guid value)
    {
        value = default;
        return root.TryGetProperty(containerName, out var container) && container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String &&
            Guid.TryParse(uuid.GetString(), out value) && value != Guid.Empty;
    }

    private void ApplyAuthReadLocked(ClaudeAuthRead read, DateTimeOffset observedAt)
    {
        if (read.Status != ClaudeAuthStatus.Ready || read.Identity is null)
        {
            _activeRequestStop?.Cancel();
            var changed = _authIdentity is not null || _owner is not null || _cache.Count > 0;
            _authIdentity = null;
            _owner = null;
            _rejectedToken = null;
            _retryNotBeforeTimestamp = 0;
            _rateLimitFailures = 0;
            if (changed) InvalidateGenerationLocked(cancelRequest: false);
            ClearCacheLocked(observedAt);
            var state = read.Status == ClaudeAuthStatus.Unavailable ? MetricState.Unavailable : MetricState.Error;
            SetAllStateLocked(state, read.ErrorCode, observedAt);
            return;
        }

        if (_authIdentity is not null && SameAuth(_authIdentity, read.Identity)) return;
        InvalidateGenerationLocked(cancelRequest: true);
        _authIdentity = read.Identity;
        _owner = null;
        _rejectedToken = null;
        _retryNotBeforeTimestamp = 0;
        _rateLimitFailures = 0;
        ClearCacheLocked(observedAt);
        SetAllStateLocked(MetricState.Unavailable, null, observedAt);
    }

    private static bool SameAuth(ClaudeAuthIdentity left, ClaudeAuthIdentity right)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(left.ConfigDirectory, right.ConfigDirectory)) return false;
        var first = System.Text.Encoding.UTF8.GetBytes(left.AccessToken);
        var second = System.Text.Encoding.UTF8.GetBytes(right.AccessToken);
        try { return first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second); }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    private void SetOwnerLocked(ClaudeProfileIdentity owner, DateTimeOffset observedAt)
    {
        _owner = owner;
        SetAllStateLocked(MetricState.Unavailable, null, observedAt);
    }

    private void InvalidateGenerationLocked(bool cancelRequest)
    {
        _generation++;
        _activePublishStop?.Cancel();
        if (cancelRequest) _activeRequestStop?.Cancel();
    }

    private void ReplaceReadingLocked(ClaudeQuotaReading reading, DateTimeOffset observedAt, long timestamp)
    {
        var updated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in reading.Points)
        {
            EnsurePointDescriptor(point);
            updated.Add(point.Id);
            if (point.State == MetricState.Fresh && point.Value is MetricValue value)
            {
                _cache[point.Id] = new CachedPoint(value, observedAt, timestamp, point.ResetAt, false, null);
                _points[point.Id] = point;
            }
            else
            {
                _cache.Remove(point.Id);
                _points[point.Id] = point with { ResetAt = null };
            }
        }

        foreach (var id in _descriptors.Keys)
        {
            if (updated.Contains(id)) continue;
            _cache.Remove(id);
            var descriptor = _descriptors[id];
            _points[id] = ClaudeQuotaReading.Unavailable(id, descriptor.DisplayName, descriptor.ValueKind);
        }
        InvalidateGenerationLocked(cancelRequest: false);
    }

    private void EnsurePointDescriptor(ClaudeQuotaPoint point)
    {
        if (_descriptors.ContainsKey(point.Id)) return;
        var semantic = point.ValueKind == MetricValueKind.Double ? "quota_remaining" : "timestamp";
        var unit = point.ValueKind == MetricValueKind.Double ? "%" : null;
        RegisterDescriptor(new MetricDescriptor(new MetricId(point.Id), point.DisplayName, semantic, point.ValueKind, unit: unit));
        _points[point.Id] = ClaudeQuotaReading.Unavailable(point.Id, point.DisplayName, point.ValueKind);
    }

    private void RegisterDescriptor(MetricDescriptor descriptor)
    {
        if (_registry.TryRegister(descriptor))
        {
            _descriptors.Add(descriptor.Id.Value, descriptor);
            return;
        }
        if (!_registry.TryGet(descriptor.Id, out var existing) || existing is null ||
            existing.ValueKind != descriptor.ValueKind || existing.SemanticType != descriptor.SemanticType ||
            existing.Unit != descriptor.Unit || existing.DisplayName != descriptor.DisplayName)
            throw new InvalidOperationException("Claude quota metric registration failed.");
        _descriptors.Add(descriptor.Id.Value, existing);
    }

    private void ApplyFailureLocked(string code, MetricState noCacheState, DateTimeOffset observedAt, long timestamp)
    {
        var canServeStale = code is "offline" or "timeout" or "upstream_unavailable" or "rate_limited";
        foreach (var id in _descriptors.Keys)
        {
            if (canServeStale && _cache.TryGetValue(id, out var cached) && IsCacheEligible(cached, observedAt, timestamp))
            {
                _cache[id] = cached with { ForceStale = true, ErrorCode = code };
                continue;
            }
            _cache.Remove(id);
            var descriptor = _descriptors[id];
            _points[id] = new ClaudeQuotaPoint(id, descriptor.DisplayName, descriptor.ValueKind, null,
                noCacheState, code, null);
        }
        InvalidateGenerationLocked(cancelRequest: false);
    }

    private void ClearCacheLocked(DateTimeOffset observedAt)
    {
        _cache.Clear();
        SetAllStateLocked(MetricState.Unavailable, null, observedAt);
        InvalidateGenerationLocked(cancelRequest: false);
    }

    private void SetAllStateLocked(MetricState state, string? errorCode, DateTimeOffset observedAt)
    {
        _cache.Clear();
        foreach (var descriptor in _descriptors.Values)
        {
            _points[descriptor.Id.Value] = new ClaudeQuotaPoint(descriptor.Id.Value, descriptor.DisplayName,
                descriptor.ValueKind, null, state, errorCode, null);
        }
    }

    private List<MetricSnapshot> CreateSnapshotsLocked(DateTimeOffset now, long timestamp)
    {
        var snapshots = new List<MetricSnapshot>(_descriptors.Count);
        var validityChanged = false;
        foreach (var descriptor in _descriptors.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            MetricValue? value = null;
            MetricState state;
            string? errorCode;
            DateTimeOffset observedAt;
            if (_cache.TryGetValue(descriptor.Id.Value, out var cached))
            {
                var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
                if (_clockUncertain || age < TimeSpan.Zero || age >= MaximumCacheAge)
                {
                    _cache.Remove(descriptor.Id.Value);
                    validityChanged = true;
                    state = MetricState.Unavailable;
                    errorCode = "cache_expired";
                    observedAt = cached.ObservedAt;
                }
                else if (cached.ResetAt is DateTimeOffset resetAt && resetAt <= now)
                {
                    _cache.Remove(descriptor.Id.Value);
                    validityChanged = true;
                    state = MetricState.Unavailable;
                    errorCode = "reset_elapsed";
                    observedAt = cached.ObservedAt;
                }
                else
                {
                    value = cached.Value;
                    state = cached.ForceStale || age >= StaleAfter ? MetricState.Stale : MetricState.Fresh;
                    errorCode = cached.ErrorCode;
                    observedAt = cached.ObservedAt;
                }
            }
            else if (_points.TryGetValue(descriptor.Id.Value, out var point))
            {
                state = point.State;
                errorCode = point.ErrorCode;
                observedAt = _lastClockUtc ?? now;
            }
            else
            {
                state = MetricState.Unavailable;
                errorCode = null;
                observedAt = now;
            }

            snapshots.Add(new MetricSnapshot(descriptor.Id, value, observedAt, state,
                value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                source: Source, errorCode: errorCode));
        }
        if (validityChanged) InvalidateGenerationLocked(cancelRequest: false);
        return snapshots;
    }

    private bool IsCacheEligible(CachedPoint cached, DateTimeOffset now, long timestamp)
    {
        var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
        return !_clockUncertain && age >= TimeSpan.Zero && age < MaximumCacheAge &&
            (cached.ResetAt is null || cached.ResetAt > now);
    }

    private void ExpireCacheLocked(DateTimeOffset now, long timestamp)
    {
        var expired = false;
        foreach (var (id, cached) in _cache.ToArray())
        {
            if (IsCacheEligible(cached, now, timestamp)) continue;
            expired = true;
            _cache.Remove(id);
            _points[id] = ClaudeQuotaReading.Unavailable(id, _descriptors[id].DisplayName,
                _descriptors[id].ValueKind, cached.ResetAt is DateTimeOffset reset && reset <= now ? "reset_elapsed" : "cache_expired");
        }
        if (expired) InvalidateGenerationLocked(cancelRequest: false);
    }

    private void ObserveClockLocked(DateTimeOffset now, long timestamp)
    {
        if (_lastClockUtc is DateTimeOffset previousUtc)
        {
            var utcElapsed = now - previousUtc;
            var monotonicElapsed = _timeProvider.GetElapsedTime(_lastClockTimestamp, timestamp);
            if (monotonicElapsed < TimeSpan.Zero || Math.Abs((utcElapsed - monotonicElapsed).TotalSeconds) > 2)
            {
                if (_cache.Count > 0)
                {
                    _cache.Clear();
                    SetAllStateLocked(MetricState.Unavailable, "clock_changed", now);
                    InvalidateGenerationLocked(cancelRequest: false);
                }
                _clockUncertain = true;
            }
            else if (_clockUncertain)
            {
                _clockUncertain = false;
            }
        }
        _lastClockUtc = now;
        _lastClockTimestamp = timestamp;
    }

    private async Task PublishGenerationAsync(IReadOnlyList<MetricSnapshot> snapshots, long generation,
        CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? publication = null;
        CancellationTokenSource? validityDeadline = null;
        try
        {
            lock (_stateLock)
            {
                if (generation != _generation) return;
                _activePublishStop?.Cancel();
                var lifetime = PublicationLifetimeLocked(snapshots);
                if (lifetime is TimeSpan remaining)
                {
                    if (remaining <= TimeSpan.Zero)
                    {
                        InvalidateGenerationLocked(cancelRequest: false);
                        return;
                    }
                    validityDeadline = new CancellationTokenSource(remaining, _timeProvider);
                }
                publication = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token,
                    validityDeadline?.Token ?? CancellationToken.None);
                _activePublishStop = publication;
            }

            foreach (var snapshot in snapshots)
            {
                lock (_stateLock)
                {
                    if (generation != _generation)
                    {
                        publication.Cancel();
                        return;
                    }
                }
                await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
            !_shutdown.IsCancellationRequested &&
            (generation != ReadGeneration() || validityDeadline?.IsCancellationRequested == true))
        {
            if (validityDeadline?.IsCancellationRequested == true)
            {
                var auth = _authReader.Read();
                lock (_stateLock)
                {
                    if (generation != _generation) return;
                    var now = _timeProvider.GetUtcNow();
                    var timestamp = _timeProvider.GetTimestamp();
                    ApplyAuthReadLocked(auth, now);
                    ExpireCacheLocked(now, timestamp);
                    if (generation == _generation) InvalidateGenerationLocked(cancelRequest: false);
                }
            }
        }
        finally
        {
            if (publication is not null)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null;
                }
                publication.Dispose();
            }
            validityDeadline?.Dispose();
            _publishGate.Release();
        }
    }

    private TimeSpan? PublicationLifetimeLocked(IReadOnlyList<MetricSnapshot> snapshots)
    {
        TimeSpan? lifetime = null;
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Value is null || !_cache.TryGetValue(snapshot.MetricId.Value, out var cached)) continue;
            var age = _timeProvider.GetElapsedTime(cached.Timestamp, timestamp);
            var remaining = MaximumCacheAge - age;
            if (snapshot.State == MetricState.Fresh && StaleAfter - age < remaining) remaining = StaleAfter - age;
            if (cached.ResetAt is DateTimeOffset reset && reset - now < remaining) remaining = reset - now;
            if (_authIdentity is not null)
            {
                var authRemaining = DateTimeOffset.FromUnixTimeMilliseconds(_authIdentity.ExpiresAtUnixMilliseconds)
                    - now - TimeSpan.FromSeconds(30);
                if (authRemaining < remaining) remaining = authRemaining;
            }
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (lifetime is null || remaining < lifetime) lifetime = remaining;
        }
        return lifetime;
    }

    private long ReadGeneration()
    {
        lock (_stateLock) return _generation;
    }

    private async Task PublishStoppedAsync()
    {
        lock (_stateLock) InvalidateGenerationLocked(cancelRequest: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await _publishGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                List<MetricSnapshot> snapshots;
                var now = _timeProvider.GetUtcNow();
                lock (_stateLock)
                {
                    _authIdentity = null;
                    _owner = null;
                    _rejectedToken = null;
                    _activeRequestStop?.Cancel();
                    _activeRequestStop = null;
                    _retryNotBeforeTimestamp = 0;
                    _rateLimitFailures = 0;
                    SetAllStateLocked(MetricState.Stopped, null, now);
                    snapshots = CreateSnapshotsLocked(now, _timeProvider.GetTimestamp());
                }
                foreach (var snapshot in snapshots)
                    await _bus.PublishAsync(snapshot, deadline.Token).ConfigureAwait(false);
            }
            finally { _publishGate.Release(); }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is TimeSpan delta && delta > TimeSpan.Zero) return delta;
        if (retry?.Date is DateTimeOffset date)
        {
            var remaining = date - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero) return remaining;
        }
        return null;
    }

    private static TimeSpan BackoffDelay(int failures)
    {
        var seconds = Math.Min(60d * Math.Pow(2, Math.Min(failures - 1, 10)), TimeSpan.FromMinutes(15).TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private long AddTimestamp(long timestamp, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return timestamp;
        var ticks = delay.TotalSeconds * _timeProvider.TimestampFrequency;
        if (ticks >= long.MaxValue - timestamp) return long.MaxValue;
        return timestamp + (long)Math.Ceiling(ticks);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        lock (_stateLock)
        {
            _activeRequestStop?.Cancel();
            _activeRequestStop = null;
            _authIdentity = null;
            _owner = null;
            _rejectedToken = null;
            _cache.Clear();
            _points.Clear();
        }
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private readonly record struct ClaudeProfileIdentity(Guid Account, Guid Organization);

    private sealed record CachedPoint(MetricValue Value, DateTimeOffset ObservedAt, long Timestamp,
        DateTimeOffset? ResetAt, bool ForceStale, string? ErrorCode);

    private readonly record struct BodyRead(byte[]? Body, bool TooLarge)
    {
        public static BodyRead Oversized => new(null, true);
    }

    private sealed record AcquisitionOutcome(byte[]? Body, ClaudeProfileIdentity? Profile,
        ClaudeQuotaReading? Reading, string? FailureCode, MetricState NoCacheState, TimeSpan? RetryAfter)
    {
        public static AcquisitionOutcome BodyOnly(byte[] body) => new(body, null, null, null, MetricState.Error, null);
        public static AcquisitionOutcome ProfileOnly(ClaudeProfileIdentity profile) => new(null, profile, null, null, MetricState.Error, null);
        public static AcquisitionOutcome Success(ClaudeProfileIdentity profile, ClaudeQuotaReading reading) =>
            new(null, profile, reading, null, MetricState.Error, null);
        public static AcquisitionOutcome Failure(string code, MetricState state, TimeSpan? retryAfter = null) =>
            new(null, null, null, code, state, retryAfter);
        public static AcquisitionOutcome Cancelled => Failure("request_cancelled", MetricState.Unavailable);
    }
}
