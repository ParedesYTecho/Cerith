#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.GeminiCli;

internal sealed class GeminiCliQuotaProvider : IDisposable
{
    internal static readonly Uri LoadCodeAssistEndpoint = new("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
    internal static readonly Uri RetrieveUserQuotaEndpoint = new("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(15);
    internal const int MaxResponseBytes = 256 * 1024;
    internal const int MaxRegisteredModels = 64;
    internal const string SnapshotSource = "GeminiCli.CodeAssist.ReadOnly.v1";

    private readonly MetricBus _bus;
    private readonly MetricRegistry _registry;
    private readonly GeminiCliQuotaAuthReader _authReader;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string?> _selectedProjectReader;
    private readonly bool _ownsHttpClient;
    private readonly Dictionary<string, ModelState> _models = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private GeminiCliAuthIdentity? _activeAuth;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _rejectedAccessToken;
    private long _nextPollTimestamp;
    private long _retryNotBeforeTimestamp;
    private DateTimeOffset? _lastUtc;
    private long _lastTimestamp;
    private string? _activeProjectId;
    private string? _selectedProjectId;
    private string? _selectedProjectErrorCode;
    private GeminiCliAuthStatus? _lastAuthStatus;
    private string? _lastAuthErrorCode;
    private long _ownerGeneration;
    private int _disposeStarted;
    private bool _disposed;

    public GeminiCliQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new GeminiCliQuotaAuthReader(), CreateHttpClient(), TimeProvider.System,
            ReadSelectedProjectFromEnvironment, ownsHttpClient: true)
    {
    }

    internal GeminiCliQuotaProvider(MetricRegistry registry, MetricBus bus, GeminiCliQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, Func<string?>? selectedProjectReader = null)
        : this(registry, bus, authReader, httpClient, timeProvider,
            selectedProjectReader ?? ReadSelectedProjectFromEnvironment, ownsHttpClient: false)
    {
    }

    private GeminiCliQuotaProvider(MetricRegistry registry, MetricBus bus, GeminiCliQuotaAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider, Func<string?> selectedProjectReader, bool ownsHttpClient)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _selectedProjectReader = selectedProjectReader ?? throw new ArgumentNullException(nameof(selectedProjectReader));
        _ownsHttpClient = ownsHttpClient;
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
            workers = [MonitorLocalStateLoopAsync(stop.Token), PollLoopAsync(stop.Token), PublishLoopAsync(stop.Token)];
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

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
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
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _stateLock.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var timestamp = _timeProvider.GetTimestamp();
            ObserveClockLocked(now, timestamp);
            ApplyAuthReadLocked(_authReader.Read(), ReadProjectSelection(), now);
            ExpireCachesLocked(now, timestamp);
        }
        finally { _stateLock.Release(); }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(admission.Token).ConfigureAwait(false);
        CancellationTokenSource? requestStop = null;
        byte[]? quotaBody = null;
        ProjectSelection requestProject = ProjectSelection.None;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var nowTimestamp = _timeProvider.GetTimestamp();
            if (nowTimestamp < Volatile.Read(ref _nextPollTimestamp)) return;

            await _stateLock.WaitAsync(admission.Token).ConfigureAwait(false);
            GeminiCliAuthIdentity? requestAuth;
            try
            {
                var authRead = _authReader.Read();
                var projectSelection = ReadProjectSelection();
                var now = _timeProvider.GetUtcNow();
                ObserveClockLocked(now, _timeProvider.GetTimestamp());
                ApplyAuthReadLocked(authRead, projectSelection, now);
                ExpireCachesLocked(now, _timeProvider.GetTimestamp());
                requestAuth = authRead.Status == GeminiCliAuthStatus.Ready ? authRead.Identity : null;
                if (requestAuth is null)
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }
                if (projectSelection.ErrorCode is not null)
                {
                    ClearAllLocked(MetricState.Error, projectSelection.ErrorCode, now);
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }
                if (_rejectedAccessToken is not null &&
                    StringComparer.Ordinal.Equals(_rejectedAccessToken, requestAuth.AccessToken))
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }
                if (_timeProvider.GetTimestamp() < _retryNotBeforeTimestamp)
                {
                    ScheduleRetryCheckLocked();
                    return;
                }

                requestStop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activeRequestStop = requestStop;
                requestProject = projectSelection;
            }
            finally { _stateLock.Release(); }

            Acquisition acquisition;
            var requestCancellation = requestStop ?? throw new InvalidOperationException("Gemini request admission failed.");
            try { acquisition = await AcquireAsync(requestAuth, requestProject, requestCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!admission.IsCancellationRequested && requestCancellation.IsCancellationRequested)
            {
                acquisition = new Acquisition(null, null, MetricState.Unavailable, null, OwnerChanged: true);
            }
            quotaBody = acquisition.Body;
            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();

            var completedAt = _timeProvider.GetUtcNow();
            await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var finalRead = _authReader.Read();
                var finalProject = ReadProjectSelection();
                var commitTime = _timeProvider.GetUtcNow();
                ObserveClockLocked(commitTime, _timeProvider.GetTimestamp());
                ApplyAuthReadLocked(finalRead, finalProject, commitTime);
                var sameOwner = !acquisition.OwnerChanged && !requestCancellation.IsCancellationRequested &&
                    finalRead.Status == GeminiCliAuthStatus.Ready && finalRead.Identity is not null &&
                    StringComparer.Ordinal.Equals(finalRead.Identity.AccessToken, requestAuth.AccessToken) &&
                    requestProject == finalProject;
                if (!sameOwner)
                {
                    ScheduleNextPollLocked(PollInterval);
                    return;
                }

                if (acquisition.ErrorCode == "project_unavailable")
                {
                    _activeProjectId = null;
                    ClearAllLocked(MetricState.Unavailable, "project_unavailable", completedAt);
                }
                else if (acquisition.ProjectId is not null)
                {
                    if (_activeProjectId is not null &&
                        !StringComparer.Ordinal.Equals(_activeProjectId, acquisition.ProjectId))
                        ClearAllLocked(MetricState.Unavailable, null, completedAt);
                    _activeProjectId = acquisition.ProjectId;
                }

                if (acquisition.ErrorCode is not null)
                {
                    ApplyAcquisitionFailureLocked(acquisition, requestAuth, completedAt);
                }
                else if (quotaBody is null)
                {
                    ClearAllLocked(MetricState.Error, "response_invalid", completedAt);
                }
                else
                {
                    var reading = GeminiCliQuotaParser.Parse(quotaBody, completedAt);
                    if (!reading.IsValid)
                    {
                        ClearAllLocked(MetricState.Error, reading.ErrorCode ?? "schema_invalid", completedAt);
                    }
                    else
                    {
                        if (!ReplaceFromReadingLocked(reading, completedAt, _timeProvider.GetTimestamp()))
                            ClearAllLocked(MetricState.Error, "model_limit_exceeded", completedAt);
                    }
                    _rejectedAccessToken = null;
                    _retryNotBeforeTimestamp = 0;
                }
                ScheduleNextPollLocked(PollInterval);
            }
            finally { _stateLock.Release(); }
        }
        finally
        {
            if (requestStop is not null)
            {
                await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activeRequestStop, requestStop)) _activeRequestStop = null;
                }
                finally { _stateLock.Release(); }
                requestStop.Dispose();
            }
            if (quotaBody is not null) CryptographicOperations.ZeroMemory(quotaBody);
            _pollLock.Release();
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
            var authRead = _authReader.Read();
            var projectSelection = ReadProjectSelection();
            var now = _timeProvider.GetUtcNow();
            await _stateLock.WaitAsync(admission.Token).ConfigureAwait(false);
            long generation;
            try
            {
                var timestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(now, timestamp);
                ApplyAuthReadLocked(authRead, projectSelection, now);
                ExpireCachesLocked(now, timestamp);
                publication = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                _activePublishStop = publication;
                generation = _ownerGeneration;
            }
            finally { _stateLock.Release(); }

            var confirm = _authReader.Read();
            var confirmProject = ReadProjectSelection();
            await _stateLock.WaitAsync(admission.Token).ConfigureAwait(false);
            MetricSnapshot[] snapshots;
            try
            {
                var confirmTime = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();
                ObserveClockLocked(confirmTime, timestamp);
                ApplyAuthReadLocked(confirm, confirmProject, confirmTime);
                ExpireCachesLocked(confirmTime, timestamp);
                if (generation != _ownerGeneration || !SameOwner(authRead, confirm) ||
                    projectSelection != confirmProject || publication.IsCancellationRequested)
                    return;
                snapshots = CreateSnapshotsLocked(confirmTime, timestamp);
            }
            finally { _stateLock.Release(); }

            foreach (var snapshot in snapshots)
                await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (publication?.IsCancellationRequested == true &&
            !admission.IsCancellationRequested) { }
        finally
        {
            if (publication is not null)
            {
                await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_activePublishStop, publication)) _activePublishStop = null;
                }
                finally { _stateLock.Release(); }
                publication.Dispose();
            }
            _publishLock.Release();
        }
    }

    private async Task<Acquisition> AcquireAsync(GeminiCliAuthIdentity auth, ProjectSelection projectSelection,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var statusRequest = CreateLoadCodeAssistRequest(projectSelection.ProjectId);
            var status = await SendPostAsync(LoadCodeAssistEndpoint, statusRequest, auth.AccessToken, linked.Token)
                .ConfigureAwait(false);
            if (status.ErrorCode is not null)
                return new Acquisition(null, status.ErrorCode, status.NoCacheState, status.RetryAfter);
            if (status.Body is null) return new Acquisition(null, "response_invalid", MetricState.Error, null);

            var project = GeminiCliQuotaParser.ParseProject(status.Body, projectSelection.ProjectId);
            CryptographicOperations.ZeroMemory(status.Body);
            if (project.ErrorCode is not null)
                return new Acquisition(null, project.ErrorCode,
                    project.ErrorCode == "project_unavailable" ? MetricState.Unavailable : MetricState.Error, null);

            var currentAuth = _authReader.Read();
            if (!SameOwner(GeminiCliAuthRead.Ready(auth), currentAuth) ||
                projectSelection != ReadProjectSelection())
                return new Acquisition(null, null, MetricState.Unavailable, null, OwnerChanged: true);

            var quotaRequest = JsonSerializer.SerializeToUtf8Bytes(new { project = project.ProjectId });
            var quota = await SendPostAsync(RetrieveUserQuotaEndpoint, quotaRequest, auth.AccessToken, linked.Token)
                .ConfigureAwait(false);
            return new Acquisition(quota.Body, quota.ErrorCode, quota.NoCacheState, quota.RetryAfter,
                ProjectId: project.ProjectId);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new Acquisition(null, "timeout", MetricState.Error, null);
        }
        catch (HttpRequestException)
        {
            return new Acquisition(null, "offline", MetricState.Unavailable, null);
        }
        catch (IOException)
        {
            return new Acquisition(null, "offline", MetricState.Unavailable, null);
        }
    }

    private async Task<RemoteResponse> SendPostAsync(Uri endpoint, byte[] requestBody, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.RequestMessage?.RequestUri != endpoint)
            return RemoteResponse.Failure("redirect_rejected");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return RemoteResponse.Failure(response.StatusCode == HttpStatusCode.Unauthorized
                ? "auth_required" : "auth_forbidden");
        if ((int)response.StatusCode == 429)
            return RemoteResponse.Failure("rate_limited", MetricState.Error,
                ReadRetryAfter(response, _timeProvider.GetUtcNow()));
        if ((int)response.StatusCode is >= 300 and < 400)
            return RemoteResponse.Failure("redirect_rejected");
        if (!response.IsSuccessStatusCode)
            return (int)response.StatusCode >= 500
                ? RemoteResponse.Failure("upstream_unavailable")
                : RemoteResponse.Failure("http_failure");

        var body = await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return body.TooLarge
            ? RemoteResponse.Failure("response_too_large")
            : new RemoteResponse(body.Body, null, MetricState.Error, null);
    }

    private void ApplyAuthReadLocked(GeminiCliAuthRead read, ProjectSelection projectSelection,
        DateTimeOffset observedAt)
    {
        var projectChanged = !StringComparer.Ordinal.Equals(_selectedProjectId, projectSelection.ProjectId) ||
            !StringComparer.Ordinal.Equals(_selectedProjectErrorCode, projectSelection.ErrorCode);
        var authStateChanged = _lastAuthStatus != read.Status ||
            !StringComparer.Ordinal.Equals(_lastAuthErrorCode, read.ErrorCode);
        var tokenChanged = read.Status == GeminiCliAuthStatus.Ready && read.Identity is not null &&
            _activeAuth is not null &&
            !StringComparer.Ordinal.Equals(_activeAuth.AccessToken, read.Identity.AccessToken);

        if (projectChanged || authStateChanged || tokenChanged)
        {
            if (_models.Count > 0)
            {
                var state = read.Status switch
                {
                    GeminiCliAuthStatus.Unsupported or GeminiCliAuthStatus.Error => MetricState.Error,
                    GeminiCliAuthStatus.Ready when projectSelection.ErrorCode is not null => MetricState.Error,
                    _ => MetricState.Unavailable
                };
                ClearAllLocked(state, projectSelection.ErrorCode ?? read.ErrorCode, observedAt);
            }
            InvalidateOwnerWorkLocked();
            _activeProjectId = null;
            _rejectedAccessToken = null;
            _retryNotBeforeTimestamp = 0;
        }

        _selectedProjectId = projectSelection.ProjectId;
        _selectedProjectErrorCode = projectSelection.ErrorCode;
        _lastAuthStatus = read.Status;
        _lastAuthErrorCode = read.ErrorCode;
        if (read.Status != GeminiCliAuthStatus.Ready || read.Identity is null)
        {
            _activeAuth = null;
            return;
        }
        _activeAuth = read.Identity;
    }

    private void InvalidateOwnerWorkLocked()
    {
        _activeRequestStop?.Cancel();
        InvalidatePublicationLocked();
    }

    private void InvalidatePublicationLocked()
    {
        _ownerGeneration++;
        _activePublishStop?.Cancel();
    }

    private void ApplyAcquisitionFailureLocked(Acquisition acquisition, GeminiCliAuthIdentity requestAuth,
        DateTimeOffset observedAt)
    {
        switch (acquisition.ErrorCode)
        {
            case "auth_required":
            case "auth_forbidden":
                _rejectedAccessToken = requestAuth.AccessToken;
                _retryNotBeforeTimestamp = 0;
                ClearAllLocked(MetricState.Error, acquisition.ErrorCode, observedAt);
                break;
            case "rate_limited":
                _retryNotBeforeTimestamp = AddTimestamp(_timeProvider.GetTimestamp(),
                    acquisition.RetryAfter ?? PollInterval);
                PreserveCacheOnFailureLocked("rate_limited", MetricState.Error, observedAt);
                break;
            case "offline":
            case "timeout":
            case "upstream_unavailable":
                PreserveCacheOnFailureLocked(acquisition.ErrorCode, acquisition.NoCacheState, observedAt);
                break;
            case "project_unavailable":
                ClearAllLocked(MetricState.Unavailable, "project_unavailable", observedAt);
                break;
            default:
                ClearAllLocked(MetricState.Error, acquisition.ErrorCode, observedAt);
                break;
        }
    }

    private bool ReplaceFromReadingLocked(GeminiCliQuotaReading reading, DateTimeOffset observedAt, long timestamp)
    {
        var newModelCount = reading.Models.Keys.Count(modelId => !_models.ContainsKey(modelId));
        if (_models.Count + newModelCount > MaxRegisteredModels) return false;

        InvalidatePublicationLocked();
        foreach (var model in _models.Values)
        {
            model.RemainingCache = null;
            model.ResetCache = null;
            model.Remaining = GeminiCliQuotaPoint.Unavailable();
            model.Reset = GeminiCliQuotaPoint.Unavailable();
            model.PointObservedAt = observedAt;
        }

        foreach (var (modelId, quota) in reading.Models)
        {
            if (!_models.TryGetValue(modelId, out var model))
            {
                model = RegisterModel(modelId, observedAt);
                _models.Add(modelId, model);
            }

            model.Remaining = quota.Remaining;
            model.Reset = quota.Reset;
            model.PointObservedAt = observedAt;
            model.RemainingCache = CreateCache(quota.Remaining, observedAt, timestamp);
            model.ResetCache = CreateCache(quota.Reset, observedAt, timestamp);
        }
        return true;
    }

    private ModelState RegisterModel(string modelId, DateTimeOffset observedAt)
    {
        var prefix = $"ai.gemini_cli.code_assist.model.{modelId}";
        var remaining = new MetricDescriptor(new MetricId(prefix + ".remaining"),
            $"Gemini CLI {modelId} Quota Remaining", "quota_remaining", MetricValueKind.Double, unit: "%");
        var reset = new MetricDescriptor(new MetricId(prefix + ".reset"),
            $"Gemini CLI {modelId} Quota Reset", "timestamp", MetricValueKind.DateTimeOffset);
        if (!_registry.TryRegister(remaining) || !_registry.TryRegister(reset))
            throw new InvalidOperationException("Gemini CLI quota metric registration failed.");
        return new ModelState(remaining, reset, observedAt);
    }

    private MetricSnapshot[] CreateSnapshotsLocked(DateTimeOffset now, long timestamp)
    {
        var snapshots = new List<MetricSnapshot>(_models.Count * 2);
        foreach (var model in _models.Values)
        {
            var remaining = Evaluate(model.Remaining, model.RemainingCache, timestamp);
            var reset = Evaluate(model.Reset, model.ResetCache, timestamp);
            snapshots.Add(CreateSnapshot(model.RemainingDescriptor, remaining, model.PointObservedAt));
            snapshots.Add(CreateSnapshot(model.ResetDescriptor, reset, model.PointObservedAt));
        }
        return snapshots.ToArray();
    }

    private static MetricSnapshot CreateSnapshot(MetricDescriptor descriptor, GeminiCliQuotaPoint point,
        DateTimeOffset observedAt) =>
        new(descriptor.Id, point.Value, observedAt, point.State,
            point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
            source: SnapshotSource, errorCode: point.ErrorCode);

    private GeminiCliQuotaPoint Evaluate(GeminiCliQuotaPoint point, CachedPoint? cache, long timestamp)
    {
        if (cache is null) return point;
        var age = _timeProvider.GetElapsedTime(cache.Timestamp, timestamp);
        return new GeminiCliQuotaPoint(cache.ForceStale || age >= StaleAfter ? MetricState.Stale : MetricState.Fresh,
            cache.Value, cache.ErrorCode);
    }

    private static CachedPoint? CreateCache(GeminiCliQuotaPoint point, DateTimeOffset observedAt, long timestamp) =>
        point.State == MetricState.Fresh && point.Value.HasValue
            ? new CachedPoint(point.Value.Value, point.ErrorCode, observedAt, timestamp)
            : null;

    private void PreserveCacheOnFailureLocked(string errorCode, MetricState noCacheState, DateTimeOffset observedAt)
    {
        InvalidatePublicationLocked();
        foreach (var model in _models.Values)
        {
            model.RemainingCache = MarkStale(model.RemainingCache, errorCode);
            model.ResetCache = MarkStale(model.ResetCache, errorCode);
            if (model.RemainingCache is null)
                model.Remaining = noCacheState == MetricState.Unavailable
                    ? GeminiCliQuotaPoint.Unavailable()
                    : GeminiCliQuotaPoint.Error(errorCode);
            if (model.ResetCache is null)
                model.Reset = noCacheState == MetricState.Unavailable
                    ? GeminiCliQuotaPoint.Unavailable()
                    : GeminiCliQuotaPoint.Error(errorCode);
            if (model.RemainingCache is null && model.ResetCache is null) model.PointObservedAt = observedAt;
        }
    }

    private static CachedPoint? MarkStale(CachedPoint? cache, string errorCode) =>
        cache is null ? null : cache with { ForceStale = true, ErrorCode = errorCode };

    private void ClearAllLocked(MetricState state, string? errorCode, DateTimeOffset observedAt)
    {
        InvalidatePublicationLocked();
        foreach (var model in _models.Values)
        {
            model.RemainingCache = null;
            model.ResetCache = null;
            model.Remaining = PointForState(state, errorCode);
            model.Reset = PointForState(state, errorCode);
            model.PointObservedAt = observedAt;
        }
    }

    private static GeminiCliQuotaPoint PointForState(MetricState state, string? errorCode) => state switch
    {
        MetricState.Error => GeminiCliQuotaPoint.Error(errorCode ?? "provider_error"),
        MetricState.Stopped => new GeminiCliQuotaPoint(MetricState.Stopped, null, null),
        _ => GeminiCliQuotaPoint.Unavailable(errorCode)
    };

    private void ExpireCachesLocked(DateTimeOffset utcNow, long timestamp)
    {
        var changed = false;
        foreach (var model in _models.Values)
        {
            var resetAt = model.ResetCache is CachedPoint resetCache &&
                resetCache.Value.Kind == MetricValueKind.DateTimeOffset
                ? resetCache.Value.AsDateTimeOffset()
                : (DateTimeOffset?)null;
            if (resetAt.HasValue && utcNow >= resetAt.Value)
            {
                changed |= model.RemainingCache is not null || model.ResetCache is not null;
                model.RemainingCache = null;
                model.ResetCache = null;
                model.Remaining = GeminiCliQuotaPoint.Unavailable("reset_elapsed");
                model.Reset = GeminiCliQuotaPoint.Unavailable("reset_elapsed");
                model.PointObservedAt = utcNow;
                continue;
            }

            changed |= ExpirePoint(model, remaining: true, utcNow, timestamp);
            changed |= ExpirePoint(model, remaining: false, utcNow, timestamp);
        }
        if (changed) InvalidatePublicationLocked();
    }

    private bool ExpirePoint(ModelState model, bool remaining, DateTimeOffset now, long timestamp)
    {
        var cache = remaining ? model.RemainingCache : model.ResetCache;
        if (cache is null) return false;
        var age = _timeProvider.GetElapsedTime(cache.Timestamp, timestamp);
        if (age < MaximumCacheAge)
        {
            if (age < StaleAfter || cache.ForceStale) return false;
            if (remaining) model.RemainingCache = cache with { ForceStale = true };
            else model.ResetCache = cache with { ForceStale = true };
            return true;
        }
        if (remaining)
        {
            model.RemainingCache = null;
            model.Remaining = GeminiCliQuotaPoint.Unavailable("cache_expired");
        }
        else
        {
            model.ResetCache = null;
            model.Reset = GeminiCliQuotaPoint.Unavailable("cache_expired");
        }
        model.PointObservedAt = now;
        return true;
    }

    private void ObserveClockLocked(DateTimeOffset utcNow, long timestamp)
    {
        if (_lastUtc is DateTimeOffset previousUtc)
        {
            var monotonic = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
            var utcElapsed = utcNow - previousUtc;
            if (utcElapsed < TimeSpan.Zero || Math.Abs((utcElapsed - monotonic).TotalSeconds) > 2)
                ClearAllLocked(MetricState.Unavailable, null, utcNow);
        }
        _lastUtc = utcNow;
        _lastTimestamp = timestamp;
    }

    private void ScheduleNextPollLocked(TimeSpan delay)
    {
        if (delay < PollInterval) delay = PollInterval;
        var now = _timeProvider.GetTimestamp();
        var delta = ToTimestampTicks(delay);
        Volatile.Write(ref _nextPollTimestamp, AddTimestamp(now, delta));
    }

    private void ScheduleRetryCheckLocked()
    {
        var now = _timeProvider.GetTimestamp();
        var nextRegular = AddTimestamp(now, ToTimestampTicks(PollInterval));
        Volatile.Write(ref _nextPollTimestamp, Math.Min(_retryNotBeforeTimestamp, nextRegular));
    }

    private long ToTimestampTicks(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return 0;
        var ticks = delay.TotalSeconds * _timeProvider.TimestampFrequency;
        return !double.IsFinite(ticks) || ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    private long AddTimestamp(long timestamp, TimeSpan delay) => AddTimestamp(timestamp, ToTimestampTicks(delay));

    private static long AddTimestamp(long timestamp, long delta) =>
        delta > long.MaxValue - timestamp ? long.MaxValue : timestamp + delta;

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var publishLockAcquired = false;
        var stateLockAcquired = false;
        try
        {
            await _publishLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            publishLockAcquired = true;
            await _stateLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            stateLockAcquired = true;
            var now = _timeProvider.GetUtcNow();
            _activeAuth = null;
            _activeProjectId = null;
            _activeRequestStop = null;
            _rejectedAccessToken = null;
            _retryNotBeforeTimestamp = 0;
            ClearAllLocked(MetricState.Stopped, null, now);
            var snapshots = _models.Values.SelectMany(model => new[]
            {
                new MetricSnapshot(model.RemainingDescriptor.Id, null, now, MetricState.Stopped,
                    MetricConfidence.Unknown, source: SnapshotSource),
                new MetricSnapshot(model.ResetDescriptor.Id, null, now, MetricState.Stopped,
                    MetricConfidence.Unknown, source: SnapshotSource)
            }).ToArray();
            _stateLock.Release();
            stateLockAcquired = false;
            foreach (var snapshot in snapshots)
                await _bus.PublishAsync(snapshot, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally
        {
            if (stateLockAcquired) _stateLock.Release();
            if (publishLockAcquired) _publishLock.Release();
        }
    }

    private async Task<BodyRead> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) return new BodyRead(null, true);
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
                if (count > MaxResponseBytes) return new BodyRead(null, true);
            }
            var body = new byte[count];
            buffer.AsSpan(0, count).CopyTo(body);
            return new BodyRead(body, false);
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
            var delay = retry?.Delta ?? (retry?.Date is DateTimeOffset date
                ? date <= now ? TimeSpan.Zero : date - now
                : (TimeSpan?)null);
            if (delay is null) return null;
            return delay.Value < TimeSpan.Zero ? TimeSpan.Zero : delay.Value;
        }
        catch (FormatException) { return null; }
    }

    internal static string? ResolveSelectedProjectId(string? googleCloudProject, string? googleCloudProjectId) =>
        !string.IsNullOrEmpty(googleCloudProject) ? googleCloudProject :
        !string.IsNullOrEmpty(googleCloudProjectId) ? googleCloudProjectId : null;

    private static string? ReadSelectedProjectFromEnvironment() =>
        ResolveSelectedProjectId(Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT"),
            Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID"));

    private ProjectSelection ReadProjectSelection()
    {
        string? projectId;
        try { projectId = _selectedProjectReader(); }
        catch (System.Security.SecurityException) { return ProjectSelection.Invalid; }
        if (string.IsNullOrEmpty(projectId)) return ProjectSelection.None;
        return GeminiCliQuotaParser.IsSafeProjectId(projectId)
            ? new ProjectSelection(projectId, null)
            : ProjectSelection.Invalid;
    }

    private static byte[] CreateLoadCodeAssistRequest(string? projectId)
    {
        if (projectId is null)
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                metadata = new { ideType = "IDE_UNSPECIFIED", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI" }
            });

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            cloudaicompanionProject = projectId,
            metadata = new
            {
                ideType = "IDE_UNSPECIFIED",
                platform = "PLATFORM_UNSPECIFIED",
                pluginType = "GEMINI",
                duetProject = projectId
            }
        });
    }

    private static bool SameOwner(GeminiCliAuthRead left, GeminiCliAuthRead right) =>
        left.Status == right.Status && StringComparer.Ordinal.Equals(left.ErrorCode, right.ErrorCode) &&
        (left.Status != GeminiCliAuthStatus.Ready ||
         left.Identity is not null && right.Identity is not null &&
         StringComparer.Ordinal.Equals(left.Identity.AccessToken, right.Identity.AccessToken));

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        _shutdown.Cancel();
        _publishLock.Wait();
        _pollLock.Wait();
        _stateLock.Wait();
        try
        {
            _activeAuth = null;
            _activeRequestStop = null;
            _rejectedAccessToken = null;
            _retryNotBeforeTimestamp = 0;
            ClearAllLocked(MetricState.Unavailable, null, _timeProvider.GetUtcNow());
        }
        finally
        {
            _stateLock.Release();
            _pollLock.Release();
            _publishLock.Release();
        }
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed class ModelState(MetricDescriptor remainingDescriptor, MetricDescriptor resetDescriptor,
        DateTimeOffset observedAt)
    {
        public MetricDescriptor RemainingDescriptor { get; } = remainingDescriptor;
        public MetricDescriptor ResetDescriptor { get; } = resetDescriptor;
        public GeminiCliQuotaPoint Remaining { get; set; } = GeminiCliQuotaPoint.Unavailable();
        public GeminiCliQuotaPoint Reset { get; set; } = GeminiCliQuotaPoint.Unavailable();
        public CachedPoint? RemainingCache { get; set; }
        public CachedPoint? ResetCache { get; set; }
        public DateTimeOffset PointObservedAt { get; set; } = observedAt;
    }

    private sealed record CachedPoint(MetricValue Value, string? ErrorCode, DateTimeOffset ObservedAt,
        long Timestamp, bool ForceStale = false);

    private sealed record Acquisition(byte[]? Body, string? ErrorCode, MetricState NoCacheState,
        TimeSpan? RetryAfter, bool OwnerChanged = false, string? ProjectId = null);

    private sealed record RemoteResponse(byte[]? Body, string? ErrorCode, MetricState NoCacheState,
        TimeSpan? RetryAfter)
    {
        public static RemoteResponse Failure(string code, MetricState noCacheState = MetricState.Error,
            TimeSpan? retryAfter = null) => new(null, code, noCacheState, retryAfter);
    }

    private sealed record BodyRead(byte[]? Body, bool TooLarge);

    private sealed record ProjectSelection(string? ProjectId, string? ErrorCode)
    {
        public static ProjectSelection None { get; } = new(null, null);
        public static ProjectSelection Invalid { get; } = new(null, "project_invalid");
    }
}
