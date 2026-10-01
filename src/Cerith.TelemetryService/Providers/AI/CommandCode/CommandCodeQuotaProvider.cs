#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.CommandCode;

namespace Cerith.TelemetryService;

public sealed class CommandCodeQuotaProvider : IDisposable
{
    internal static readonly Uri ApiBase = new("https://api.commandcode.ai/");
    internal static readonly Uri WhoAmIEndpoint = new("https://api.commandcode.ai/alpha/whoami?limits=1");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(2);
    internal const int MaxResponseBytes = 64 * 1024;

    private static readonly string[] MetricIds =
    [
        "ai.commandcode.quota.5h.used_percent",
        "ai.commandcode.quota.5h.reset",
        "ai.commandcode.quota.weekly.used_percent",
        "ai.commandcode.quota.weekly.reset",
        "ai.commandcode.subscription.status",
        "ai.commandcode.subscription.plan"
    ];

    private const string Source = "CommandCode.ObservedBillingApi.v1";
    private const string UserAgentProductToken = "Cerith";
    private const int MaximumResponseHeadersKilobytes = 64;

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly CommandCodeQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _ownsHttpClient;
    private readonly object _stateLock = new();
    private readonly PointState[] _points = new PointState[MetricIds.Length];
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly SemaphoreSlim _publicationRequested = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublicationStop;
    private string? _credentialFingerprint;
    private CommandCodeCredential.OwnerIdentity? _credentialIdentity;
    private string? _knownOrgId;
    private bool _hasCredentialScope;
    private bool _hasObservedOrgScope;
    private string? _authErrorCode;
    private string? _rejectedFingerprint;
    private long _ownerGeneration;
    private long _publicationGeneration;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private int _runStarted;
    private int _disposeStarted;
    private volatile bool _disposed;

    public CommandCodeQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new CommandCodeQuotaAuthReader(), CreateHttpClient(), TimeProvider.System,
            RequestTimeout, ownsHttpClient: true)
    {
    }

    internal CommandCodeQuotaProvider(
        MetricRegistry registry,
        MetricBus bus,
        CommandCodeQuotaAuthReader authReader,
        HttpClient httpClient,
        TimeProvider timeProvider,
        TimeSpan? requestTimeout = null)
        : this(registry, bus, authReader, httpClient, timeProvider, requestTimeout ?? RequestTimeout,
            ownsHttpClient: false)
    {
    }

    private CommandCodeQuotaProvider(
        MetricRegistry registry,
        MetricBus bus,
        CommandCodeQuotaAuthReader authReader,
        HttpClient httpClient,
        TimeProvider timeProvider,
        TimeSpan requestTimeout,
        bool ownsHttpClient)
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
            Descriptor(0, "Command Code 5-hour used percent", MetricValueKind.Double, "quota_used", "%"),
            Descriptor(1, "Command Code 5-hour reset", MetricValueKind.DateTimeOffset, "quota_reset", null),
            Descriptor(2, "Command Code weekly used percent", MetricValueKind.Double, "quota_used", "%"),
            Descriptor(3, "Command Code weekly reset", MetricValueKind.DateTimeOffset, "quota_reset", null),
            Descriptor(4, "Command Code subscription status", MetricValueKind.String, "subscription_status", null),
            Descriptor(5, "Command Code subscription plan", MetricValueKind.String, "subscription_plan", null)
        ];

        var now = _timeProvider.GetUtcNow();
        for (var index = 0; index < _descriptors.Length; index++)
        {
            _points[index] = PointState.Unavailable(now);
            if (!registry.TryRegister(_descriptors[index]))
                throw new InvalidOperationException("Command Code quota metric registration failed.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("Command Code quota provider can only run once.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers = [];
        ExceptionDispatchInfo? unexpectedFault = null;
        try
        {
            workers =
            [
                PollLoopAsync(stop.Token),
                CredentialMonitorLoopAsync(stop.Token),
                ValidityMonitorLoopAsync(stop.Token),
                PublishLoopAsync(stop.Token)
            ];
            var completed = await Task.WhenAny(workers).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            unexpectedFault = ExceptionDispatchInfo.Capture(exception);
            try { SetProviderFailure(); }
            catch (Exception stateException) { unexpectedFault ??= ExceptionDispatchInfo.Capture(stateException); }
        }
        finally
        {
            stop.Cancel();
            InvalidateActiveWork();
            if (workers.Length > 0)
            {
                var drain = Task.WhenAll(workers);
                try { await drain.WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { InvalidateActiveWork(); }
                catch (TimeoutException)
                {
                    InvalidateActiveWork();
                    ObserveLateFault(drain);
                }
                catch (Exception exception)
                {
                    unexpectedFault ??= ExceptionDispatchInfo.Capture(exception);
                    try { SetProviderFailure(); }
                    catch (Exception stateException) { unexpectedFault ??= ExceptionDispatchInfo.Capture(stateException); }
                }
            }

            try { await PublishStoppedAsync().ConfigureAwait(false); }
            catch (Exception exception) { unexpectedFault ??= ExceptionDispatchInfo.Capture(exception); }
        }

        unexpectedFault?.Throw();
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? requestStop = null;
        var requestGeneration = -1L;
        try
        {
            var authRead = _authReader.Read();
            var scopeChanged = ApplyCredentialRead(authRead);
            if (scopeChanged) RequestPublication();
            if (authRead.Status != CommandCodeAuthStatus.Available || authRead.Credential is null)
            {
                ScheduleNextPoll();
                return;
            }

            var credential = authRead.Credential;
            lock (_stateLock)
            {
                if (string.Equals(_rejectedFingerprint, credential.Fingerprint, StringComparison.Ordinal))
                {
                    ScheduleNextPollLocked();
                    return;
                }
                if (_timeProvider.GetTimestamp() < _retryNotBeforeTimestamp)
                {
                    ScheduleNextPollLocked();
                    return;
                }

                requestGeneration = _ownerGeneration;
                requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = requestStop;
            }

            using var whoAmI = await GetJsonAsync(WhoAmIEndpoint, credential.ApiKey, requestStop!.Token)
                .ConfigureAwait(false);
            var orgScope = CommandCodeQuotaParser.ParseOrgScope(whoAmI.RootElement);
            if (!orgScope.IsValid)
            {
                ApplySchemaFailure(requestGeneration);
                return;
            }

            if (!ApplyOrgScope(credential, orgScope.OrgId, ref requestGeneration)) return;

            var creditsTask = GetJsonAsync(BuildOrgUri("/alpha/billing/credits", orgScope.OrgId),
                credential.ApiKey, requestStop!.Token);
            var subscriptionTask = GetJsonAsync(BuildOrgUri("/alpha/billing/subscriptions", orgScope.OrgId),
                credential.ApiKey, requestStop!.Token);
            JsonDocument? credits = null;
            JsonDocument? subscription = null;
            try
            {
                await Task.WhenAll(creditsTask, subscriptionTask).ConfigureAwait(false);
                credits = await creditsTask.ConfigureAwait(false);
                subscription = await subscriptionTask.ConfigureAwait(false);
                CommitIfOwnerStillMatches(credential, orgScope.OrgId, requestGeneration,
                    credits.RootElement, subscription.RootElement, requestStop.Token);
            }
            finally
            {
                credits?.Dispose();
                subscription?.Dispose();
                if (credits is null && creditsTask.IsCompletedSuccessfully)
                    creditsTask.Result.Dispose();
                if (subscription is null && subscriptionTask.IsCompletedSuccessfully)
                    subscriptionTask.Result.Dispose();
            }
        }
        catch (OperationCanceledException) when (admission.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (requestStop?.IsCancellationRequested == true)
        {
            return;
        }
        catch (CommandCodeRequestException exception)
        {
            ApplyRequestFailure(exception, requestGeneration >= 0 ? requestGeneration : CurrentOwnerGeneration());
        }
        catch (JsonException)
        {
            ApplyRequestFailure(new CommandCodeRequestException("invalid_json"),
                requestGeneration >= 0 ? requestGeneration : CurrentOwnerGeneration());
        }
        finally
        {
            if (requestStop is not null)
            {
                lock (_stateLock)
                    if (ReferenceEquals(_activeRequestStop, requestStop)) _activeRequestStop = null;
                requestStop.Dispose();
            }
            ScheduleNextPoll();
            _pollLock.Release();
        }
    }

    internal async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _publishLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? publicationStop = null;
        MetricSnapshot[] snapshots;
        long generation;
        try
        {
            lock (_stateLock)
            {
                publicationStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activePublicationStop = publicationStop;
                generation = _publicationGeneration;
                snapshots = CreateSnapshotsLocked();
            }

            foreach (var snapshot in snapshots)
            {
                lock (_stateLock)
                {
                    if (generation != _publicationGeneration ||
                        !ReferenceEquals(_activePublicationStop, publicationStop))
                        return;
                }

                await _bus.PublishAsync(snapshot, publicationStop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!admission.IsCancellationRequested &&
                                                  publicationStop?.IsCancellationRequested == true)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            if (publicationStop is not null)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activePublicationStop, publicationStop))
                        _activePublicationStop = null;
                    publicationStop.Dispose();
                }
            }
            _publishLock.Release();
        }
    }

    internal async Task ObserveAuthOnceAsync()
    {
        var changed = ApplyCredentialRead(_authReader.Read());
        if (changed) RequestPublication();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal async Task ExpireValidityOnceAsync()
    {
        var changed = false;
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        lock (_stateLock)
        {
            for (var index = 0; index < _points.Length; index++)
            {
                var point = _points[index];
                if (!point.Value.HasValue || !point.CachedAtTimestamp.HasValue) continue;

                var resetElapsed = index < 4 && point.ResetAt.HasValue && point.ResetAt.Value <= now;
                var age = _timeProvider.GetElapsedTime(point.CachedAtTimestamp.Value, timestamp);
                if (resetElapsed)
                {
                    var usedIndex = index < 2 ? 0 : 2;
                    SetUnavailableLocked(usedIndex, now, "reset_elapsed");
                    SetUnavailableLocked(usedIndex + 1, now, "reset_elapsed");
                    changed = true;
                    continue;
                }

                if (age < TimeSpan.Zero || age >= MaximumCacheAge)
                {
                    SetUnavailableLocked(index, now, "cache_expired");
                    changed = true;
                }
                else if (point.State == MetricState.Fresh && age >= StaleAfter)
                {
                    point.State = MetricState.Stale;
                    changed = true;
                }
            }

            if (changed) InvalidatePublicationLocked();
        }

        if (changed) RequestPublication();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        lock (_stateLock)
        {
            _shutdown.Cancel();
            _ownerGeneration++;
            InvalidateActiveRequestLocked();
            InvalidatePublicationLocked();
            _hasCredentialScope = false;
            _credentialFingerprint = null;
            _credentialIdentity = null;
            _knownOrgId = null;
            _hasObservedOrgScope = false;
            _rejectedFingerprint = null;
            _authErrorCode = null;
            ClearAllLocked(_timeProvider.GetUtcNow(), MetricState.Stopped, "provider_stopped");
        }
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var dueTimestamp = Math.Max(Volatile.Read(ref _nextPollTimestamp), Volatile.Read(ref _retryNotBeforeTimestamp));
            if (_timeProvider.GetTimestamp() >= dueTimestamp)
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CredentialMonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ObserveAuthOnceAsync().ConfigureAwait(false);
    }

    private async Task ValidityMonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ExpireValidityOnceAsync().ConfigureAwait(false);
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        while (await _publicationRequested.WaitAsync(Timeout.Infinite, cancellationToken).ConfigureAwait(false))
        {
            try { await PublishCurrentAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgentProductToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CommandCodeRequestException("request_timeout");
        }
        catch (HttpRequestException)
        {
            throw new CommandCodeRequestException("offline");
        }
        catch (IOException)
        {
            throw new CommandCodeRequestException("offline");
        }
        catch (SocketException)
        {
            throw new CommandCodeRequestException("offline");
        }

        using (response)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (timeout.IsCancellationRequested)
                throw new CommandCodeRequestException("request_timeout");

            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != endpoint)
                throw new CommandCodeRequestException("redirect_rejected");

            var retryAfter = ReadRetryAfter(response.Headers.RetryAfter, _timeProvider.GetUtcNow());
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CommandCodeRequestException("auth_expired", authRejected: true);
            if ((int)response.StatusCode == 429)
                throw new CommandCodeRequestException("rate_limited", retryAfter: retryAfter);
            if ((int)response.StatusCode is >= 300 and <= 399)
                throw new CommandCodeRequestException("redirect_rejected");
            if (!response.IsSuccessStatusCode)
                throw new CommandCodeRequestException("upstream_unavailable", retryAfter: retryAfter);

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new CommandCodeRequestException("response_too_large");

            byte[] bytes;
            try
            {
                bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CommandCodeRequestException("request_timeout");
            }
            try
            {
                return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
                throw new CommandCodeRequestException("invalid_json");
            }
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(MaxResponseBytes, 4096));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new CommandCodeRequestException("response_too_large");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private void CommitIfOwnerStillMatches(
        CommandCodeCredential credential,
        string? orgId,
        long requestGeneration,
        JsonElement credits,
        JsonElement subscription,
        CancellationToken cancellationToken)
    {
        var latestAuth = _authReader.Read();
        if (latestAuth.Status != CommandCodeAuthStatus.Available || latestAuth.Credential is null ||
            !SameCredential(credential, latestAuth.Credential))
        {
            if (ApplyCredentialRead(latestAuth)) RequestPublication();
            return;
        }

        var parsed = CommandCodeQuotaParser.Parse(credits, subscription);
        var observedAt = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        var committed = false;
        lock (_stateLock)
        {
            if (cancellationToken.IsCancellationRequested ||
                requestGeneration != _ownerGeneration ||
                _credentialIdentity is null ||
                !_credentialIdentity.Matches(credential.Identity) ||
                !string.Equals(_knownOrgId, orgId, StringComparison.Ordinal))
                return;

            ApplyWindowLocked(0, parsed.FiveHour, observedAt, timestamp);
            ApplyWindowLocked(2, parsed.Weekly, observedAt, timestamp);
            ApplyPointLocked(4, parsed.SubscriptionStatus, observedAt, timestamp);
            ApplyPointLocked(5, parsed.SubscriptionPlan, observedAt, timestamp);
            _retryNotBeforeTimestamp = 0;
            _rejectedFingerprint = null;
            InvalidatePublicationLocked();
            committed = true;
        }

        if (committed) RequestPublication();
    }

    private bool ApplyCredentialRead(CommandCodeAuthRead authRead)
    {
        var changed = false;
        var now = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            if (authRead.Status == CommandCodeAuthStatus.Available && authRead.Credential is { } credential)
            {
                if (_hasCredentialScope &&
                    _credentialIdentity is { } currentIdentity &&
                    currentIdentity.Matches(credential.Identity))
                {
                    _authErrorCode = null;
                    return false;
                }

                _hasCredentialScope = true;
                _credentialFingerprint = credential.Fingerprint;
                _credentialIdentity = credential.Identity;
                _knownOrgId = null;
                _hasObservedOrgScope = false;
                _authErrorCode = null;
                _rejectedFingerprint = null;
                _retryNotBeforeTimestamp = 0;
                _ownerGeneration++;
                ClearAllLocked(now, MetricState.Unavailable, "account_changed");
                InvalidateActiveRequestLocked();
                InvalidatePublicationLocked();
                changed = true;
            }
            else
            {
                var errorCode = authRead.ErrorCode ?? "auth_unavailable";
                var state = authRead.Status == CommandCodeAuthStatus.Missing
                    ? MetricState.Unavailable
                    : MetricState.Error;
                if (_hasCredentialScope || !string.Equals(_authErrorCode, errorCode, StringComparison.Ordinal))
                {
                    _hasCredentialScope = false;
                    _credentialFingerprint = null;
                    _credentialIdentity = null;
                    _knownOrgId = null;
                    _hasObservedOrgScope = false;
                    _rejectedFingerprint = null;
                    _retryNotBeforeTimestamp = 0;
                    _authErrorCode = errorCode;
                    _ownerGeneration++;
                    ClearAllLocked(now, state, errorCode);
                    InvalidateActiveRequestLocked();
                    InvalidatePublicationLocked();
                    changed = true;
                }
            }
        }
        return changed;
    }

    private bool ApplyOrgScope(CommandCodeCredential credential, string? orgId, ref long requestGeneration)
    {
        var changed = false;
        var now = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            if (!_hasCredentialScope ||
                _credentialIdentity is null ||
                !_credentialIdentity.Matches(credential.Identity))
                return false;

            if (!_hasObservedOrgScope || !string.Equals(_knownOrgId, orgId, StringComparison.Ordinal))
            {
                _knownOrgId = orgId;
                _hasObservedOrgScope = true;
                _retryNotBeforeTimestamp = 0;
                _ownerGeneration++;
                requestGeneration = _ownerGeneration;
                ClearAllLocked(now, MetricState.Unavailable, "account_changed");
                InvalidatePublicationLocked();
                changed = true;
            }
            else
            {
                requestGeneration = _ownerGeneration;
            }
        }

        if (changed) RequestPublication();
        return true;
    }

    private void ApplyWindowLocked(
        int usedIndex,
        CommandCodeParsedWindow window,
        DateTimeOffset observedAt,
        long timestamp)
    {
        if (window.ResetAt is { } resetAt && resetAt <= observedAt)
        {
            SetUnavailableLocked(usedIndex, observedAt, "reset_elapsed");
            SetUnavailableLocked(usedIndex + 1, observedAt, "reset_elapsed");
            return;
        }

        ApplyPointLocked(usedIndex, window.UsedPercent, observedAt, timestamp);
        ApplyPointLocked(usedIndex + 1, window.Reset, observedAt, timestamp);
        _points[usedIndex].ResetAt = window.ResetAt;
        _points[usedIndex + 1].ResetAt = window.ResetAt;
    }

    private void ApplyPointLocked(
        int index,
        CommandCodeParsedPoint parsed,
        DateTimeOffset observedAt,
        long timestamp)
    {
        var point = _points[index];
        point.Value = parsed.Value;
        point.State = parsed.State;
        point.Confidence = parsed.Confidence;
        point.ErrorCode = parsed.ErrorCode;
        point.ObservedAt = observedAt;
        point.CachedAtTimestamp = parsed.Value.HasValue ? timestamp : null;
        point.ResetAt = null;
    }

    private void ApplyRequestFailure(CommandCodeRequestException failure, long requestGeneration)
    {
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        lock (_stateLock)
        {
            if (requestGeneration != _ownerGeneration) return;
            if (failure.AuthRejected)
            {
                _rejectedFingerprint = _credentialFingerprint;
                ClearAllLocked(now, MetricState.Error, "auth_expired");
            }
            else if (failure.ErrorCode is "invalid_json" or "response_too_large" or "redirect_rejected" or "schema_invalid")
            {
                ClearAllLocked(now, MetricState.Error, failure.ErrorCode);
            }
            else
            {
                var failureState = failure.ErrorCode == "offline" ? MetricState.Unavailable : MetricState.Error;
                for (var index = 0; index < _points.Length; index++)
                {
                    var point = _points[index];
                    if (CanServeStaleLocked(point, now, timestamp))
                    {
                        point.State = MetricState.Stale;
                        point.ErrorCode = failure.ErrorCode;
                    }
                    else
                    {
                        SetUnavailableLocked(index, now, failure.ErrorCode, failureState);
                    }
                }
            }

            if (failure.RetryAfter.HasValue)
            {
                var deadline = AddMonotonic(nowTimestamp: timestamp, delay: failure.RetryAfter.Value);
                _retryNotBeforeTimestamp = Math.Max(_retryNotBeforeTimestamp, deadline);
            }
            else if (failure.ErrorCode == "rate_limited")
            {
                var deadline = AddMonotonic(timestamp, PollInterval);
                _retryNotBeforeTimestamp = Math.Max(_retryNotBeforeTimestamp, deadline);
            }

            InvalidatePublicationLocked();
        }
        RequestPublication();
    }

    private void ApplySchemaFailure(long requestGeneration)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            if (requestGeneration != _ownerGeneration) return;
            ClearAllLocked(now, MetricState.Error, "schema_invalid");
            InvalidatePublicationLocked();
        }
        RequestPublication();
    }

    private void SetProviderFailure()
    {
        ApplyRequestFailure(new CommandCodeRequestException("provider_failure"), CurrentOwnerGeneration());
    }

    private void ClearAllLocked(DateTimeOffset now, MetricState state, string errorCode)
    {
        for (var index = 0; index < _points.Length; index++)
        {
            var point = _points[index];
            point.Value = null;
            point.State = state;
            point.Confidence = MetricConfidence.Unknown;
            point.ErrorCode = errorCode;
            point.ObservedAt = now;
            point.CachedAtTimestamp = null;
            point.ResetAt = null;
        }
    }

    private void SetUnavailableLocked(int index, DateTimeOffset now, string? errorCode,
        MetricState state = MetricState.Unavailable)
    {
        var point = _points[index];
        point.Value = null;
        point.State = state;
        point.Confidence = MetricConfidence.Unknown;
        point.ErrorCode = errorCode;
        point.ObservedAt = now;
        point.CachedAtTimestamp = null;
        point.ResetAt = null;
    }

    private bool CanServeStaleLocked(PointState point, DateTimeOffset now, long timestamp)
    {
        if (!point.Value.HasValue || !point.CachedAtTimestamp.HasValue) return false;
        var age = _timeProvider.GetElapsedTime(point.CachedAtTimestamp.Value, timestamp);
        return age >= TimeSpan.Zero && age < MaximumCacheAge &&
               (!point.ResetAt.HasValue || point.ResetAt.Value > now);
    }

    private void InvalidatePublicationLocked()
    {
        _publicationGeneration++;
        _activePublicationStop?.Cancel();
    }

    private void InvalidateActiveRequestLocked() => _activeRequestStop?.Cancel();

    private void InvalidateActiveWork()
    {
        lock (_stateLock)
        {
            _ownerGeneration++;
            InvalidateActiveRequestLocked();
            InvalidatePublicationLocked();
        }
    }

    private void RequestPublication()
    {
        try { _publicationRequested.Release(); }
        catch (SemaphoreFullException) { return; }
        catch (ObjectDisposedException) { return; }
    }

    private MetricSnapshot[] CreateSnapshotsLocked()
    {
        var snapshots = new MetricSnapshot[_descriptors.Length];
        for (var index = 0; index < snapshots.Length; index++)
        {
            var point = _points[index];
            snapshots[index] = new MetricSnapshot(_descriptors[index].Id, point.Value, point.ObservedAt,
                point.State, point.Confidence, source: Source, errorCode: point.ErrorCode);
        }
        return snapshots;
    }

    private async Task PublishStoppedAsync()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            ClearAllLocked(now, MetricState.Stopped, "provider_stopped");
            InvalidatePublicationLocked();
        }

        using var deadline = new CancellationTokenSource();
        var publication = PublishCurrentAsync(deadline.Token);
        try { await publication.WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested || _shutdown.IsCancellationRequested) { return; }
        catch (ObjectDisposedException) { return; }
        catch (TimeoutException)
        {
            deadline.Cancel();
            ObserveLateFault(publication);
        }
    }

    private static MetricDescriptor Descriptor(int index, string displayName, MetricValueKind kind,
        string semanticType, string? unit) =>
        new(new MetricId(MetricIds[index]), displayName, semanticType, kind, unit: unit);

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(CreateHttpHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Proxy = null,
        Credentials = null,
        MaxResponseHeadersLength = MaximumResponseHeadersKilobytes,
        ConnectTimeout = RequestTimeout
    };

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Uri BuildOrgUri(string route, string? orgId)
    {
        var suffix = string.IsNullOrEmpty(orgId) ? string.Empty : "?orgId=" + Uri.EscapeDataString(orgId);
        return new Uri(ApiBase, route + suffix);
    }

    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (retryAfter.Date is { } date && date > now) return date - now;
        return null;
    }

    private long AddMonotonic(long nowTimestamp, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return nowTimestamp;
        var additional = Math.Ceiling(delay.TotalSeconds * _timeProvider.TimestampFrequency);
        if (!double.IsFinite(additional) || additional >= long.MaxValue - (double)nowTimestamp)
            return long.MaxValue;
        return nowTimestamp + (long)additional;
    }

    private void ScheduleNextPoll()
    {
        lock (_stateLock) ScheduleNextPollLocked();
    }

    private void ScheduleNextPollLocked() =>
        Volatile.Write(ref _nextPollTimestamp, AddMonotonic(_timeProvider.GetTimestamp(), PollInterval));

    private long CurrentOwnerGeneration()
    {
        lock (_stateLock) return _ownerGeneration;
    }

    private static bool SameCredential(CommandCodeCredential left, CommandCodeCredential right) =>
        left.Identity.Matches(right.Identity);

    private sealed class PointState
    {
        private PointState(MetricState state, DateTimeOffset observedAt)
        {
            State = state;
            Confidence = MetricConfidence.Unknown;
            ObservedAt = observedAt;
        }

        internal MetricValue? Value { get; set; }
        internal MetricState State { get; set; }
        internal MetricConfidence Confidence { get; set; }
        internal string? ErrorCode { get; set; }
        internal DateTimeOffset ObservedAt { get; set; }
        internal long? CachedAtTimestamp { get; set; }
        internal DateTimeOffset? ResetAt { get; set; }

        internal static PointState Unavailable(DateTimeOffset now) => new(MetricState.Unavailable, now);
    }

    private sealed class CommandCodeRequestException : Exception
    {
        internal CommandCodeRequestException(string errorCode, bool authRejected = false, TimeSpan? retryAfter = null)
            : base(errorCode)
        {
            ErrorCode = errorCode;
            AuthRejected = authRejected;
            RetryAfter = retryAfter;
        }

        internal string ErrorCode { get; }
        internal bool AuthRejected { get; }
        internal TimeSpan? RetryAfter { get; }
    }
}
