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

internal sealed class OpenCodeGoQuotaProvider : IDisposable
{
    internal static readonly Uri UsageEndpoint = new("https://opencode.ai/zen/go/v1/usage");
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal const int MaxResponseBytes = 128 * 1024;
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StoppedPublishTimeout = TimeSpan.FromSeconds(2);

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
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private MetricSnapshot[] _snapshots;
    private CancellationTokenSource? _activeRequestStop;
    private CancellationTokenSource? _activePublishStop;
    private string? _activeKeyFingerprint;
    private long _authGeneration;
    private long _snapshotGeneration;
    private bool _disposed;

    public OpenCodeGoQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new OpenCodeGoAuthReader(), CreateHttpClient(), TimeProvider.System)
    {
    }

    internal OpenCodeGoQuotaProvider(MetricRegistry registry, MetricBus bus, OpenCodeGoAuthReader authReader,
        HttpMessageHandler handler, TimeProvider timeProvider)
        : this(registry, bus, authReader, CreateHttpClient(handler), timeProvider)
    {
    }

    private OpenCodeGoQuotaProvider(MetricRegistry registry, MetricBus bus, OpenCodeGoAuthReader authReader,
        HttpClient httpClient, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _authReader = authReader ?? throw new ArgumentNullException(nameof(authReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
        _snapshots = CreateSnapshots(OpenCodeGoQuotaReading.Missing(), _timeProvider.GetUtcNow());
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        Task[] workers =
        [
            PollLoopAsync(stop.Token),
            PublishLoopAsync(stop.Token),
            ObserveAuthLoopAsync(stop.Token)
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
            finally
            {
                await PublishStoppedAsync().ConfigureAwait(false);
            }
        }
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
            var auth = ReadAndApplyAuth(_timeProvider.GetUtcNow());
            if (auth.Status != OpenCodeGoAuthStatus.Ready || auth.Credential is null)
            {
                SetAuthState(auth, _timeProvider.GetUtcNow());
                return;
            }

            var generation = GetAuthGeneration();
            using var deadline = new CancellationTokenSource();
            deadline.CancelAfter(RequestTimeout);
            ownerStop = new CancellationTokenSource();
            using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(
                admission.Token, deadline.Token, ownerStop.Token);
            lock (_stateLock)
            {
                if (_authGeneration != generation || _disposed) return;
                _activeRequestStop = ownerStop;
            }

            string? failureCode = null;
            MetricState failureState = MetricState.Error;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Credential.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("Cerith.TelemetryService");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    requestStop.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    (failureCode, failureState) = MapFailure(response.StatusCode);
                }
                else
                {
                    var result = await ReadBoundedBodyAsync(response, requestStop.Token).ConfigureAwait(false);
                    if (result.TooLarge) failureCode = "response_too_large";
                    else if (result.Body is null) failureCode = "response_invalid";
                    else body = result.Body;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                failureCode = "timeout";
                failureState = MetricState.Unavailable;
            }
            catch (OperationCanceledException) when (ownerStop.IsCancellationRequested)
            {
                return;
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
            catch (Exception)
            {
                failureCode = "request_failed";
            }

            var receivedAt = _timeProvider.GetUtcNow();
            var finalAuth = ReadAndApplyAuth(receivedAt);
            if (finalAuth.Status != OpenCodeGoAuthStatus.Ready || finalAuth.Credential is null ||
                !StringComparer.Ordinal.Equals(finalAuth.Credential.Fingerprint, auth.Credential.Fingerprint) ||
                GetAuthGeneration() != generation)
            {
                SetAuthState(finalAuth, receivedAt);
                return;
            }

            if (failureCode is not null)
                ReplaceSnapshots(CreateFailure(failureCode, failureState, receivedAt), generation);
            else if (body is null)
                ReplaceSnapshots(CreateFailure("response_invalid", MetricState.Error, receivedAt), generation);
            else
            {
                var reading = OpenCodeGoQuotaParser.Parse(body, receivedAt);
                ReplaceSnapshots(CreateSnapshots(reading, receivedAt), generation);
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
        var auth = ReadAndApplyAuth(_timeProvider.GetUtcNow());
        if (auth.Status != OpenCodeGoAuthStatus.Ready) SetAuthState(auth, _timeProvider.GetUtcNow());
        return ValueTask.CompletedTask;
    }

    internal async ValueTask PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await PublishSnapshotsAsync(admission.Token).ConfigureAwait(false);
    }

    private OpenCodeGoAuthRead ReadAndApplyAuth(DateTimeOffset now)
    {
        lock (_authReadLock)
        {
            var auth = _authReader.Read();
            lock (_stateLock)
            {
                var fingerprint = auth.Credential?.Fingerprint;
                if (!StringComparer.Ordinal.Equals(_activeKeyFingerprint, fingerprint))
                {
                    _activeKeyFingerprint = fingerprint;
                    _authGeneration++;
                    _activeRequestStop?.Cancel();
                    CancelActivePublicationLocked();
                    _snapshots = CreateSnapshots(OpenCodeGoQuotaReading.Missing(), now);
                    _snapshotGeneration++;
                }
            }
            return auth;
        }
    }

    private void SetAuthState(OpenCodeGoAuthRead auth, DateTimeOffset observedAt)
    {
        var generation = GetAuthGeneration();
        if (auth.Status == OpenCodeGoAuthStatus.Ready) return;
        var point = auth.Status switch
        {
            OpenCodeGoAuthStatus.Unavailable => OpenCodeGoQuotaPoint.Unavailable(),
            OpenCodeGoAuthStatus.Unsupported => OpenCodeGoQuotaPoint.Error(auth.ErrorCode ?? "auth_unsupported"),
            _ => OpenCodeGoQuotaPoint.Error(auth.ErrorCode ?? "auth_unavailable")
        };
        ReplaceSnapshots(CreateSnapshots(new OpenCodeGoQuotaReading(
            [point, point, point, point, point, point]), observedAt), generation);
    }

    private long GetAuthGeneration()
    {
        lock (_stateLock) return _authGeneration;
    }

    private void ReplaceSnapshots(MetricSnapshot[] snapshots, long expectedAuthGeneration)
    {
        lock (_stateLock)
        {
            if (_disposed || _authGeneration != expectedAuthGeneration) return;
            _snapshots = snapshots;
            _snapshotGeneration++;
            CancelActivePublicationLocked();
        }
    }

    private void CancelActivePublicationLocked() => _activePublishStop?.Cancel();

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
                _activePublishStop = publication;
                snapshots = (MetricSnapshot[])_snapshots.Clone();
                version = _snapshotGeneration;
            }

            foreach (var snapshot in snapshots)
            {
                publication.Token.ThrowIfCancellationRequested();
                lock (_stateLock)
                    if (version != _snapshotGeneration) publication.Cancel();
                await _bus.PublishAsync(snapshot, publication.Token).ConfigureAwait(false);
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

    private async Task PublishStoppedAsync()
    {
        lock (_stateLock)
        {
            _snapshots = CreateSnapshots(new OpenCodeGoQuotaReading(
                [new(MetricState.Stopped, null, null), new(MetricState.Stopped, null, null),
                 new(MetricState.Stopped, null, null), new(MetricState.Stopped, null, null),
                 new(MetricState.Stopped, null, null), new(MetricState.Stopped, null, null)]), _timeProvider.GetUtcNow());
            _snapshotGeneration++;
            CancelActivePublicationLocked();
        }

        using var deadline = new CancellationTokenSource(StoppedPublishTimeout);
        await PublishSnapshotsAsync(deadline.Token).ConfigureAwait(false);
    }

    private MetricSnapshot[] CreateSnapshots(OpenCodeGoQuotaReading reading, DateTimeOffset observedAt)
    {
        var result = new MetricSnapshot[_descriptors.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var point = reading.Points[i];
            result[i] = new MetricSnapshot(_descriptors[i].Id, point.Value, observedAt, point.State,
                point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                source: "OpenCodeGo.Usage.ReadOnly.v1", errorCode: point.ErrorCode);
        }
        return result;
    }

    private MetricSnapshot[] CreateFailure(string errorCode, MetricState state, DateTimeOffset observedAt)
    {
        var point = state == MetricState.Unavailable
            ? OpenCodeGoQuotaPoint.Unavailable(errorCode)
            : OpenCodeGoQuotaPoint.Error(errorCode);
        return CreateSnapshots(new OpenCodeGoQuotaReading([point, point, point, point, point, point]), observedAt);
    }

    private static (string? ErrorCode, MetricState State) MapFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ("auth_required", MetricState.Error),
        HttpStatusCode.Forbidden => ("auth_forbidden", MetricState.Error),
        HttpStatusCode.TooManyRequests => ("rate_limited", MetricState.Error),
        HttpStatusCode.RequestTimeout => ("timeout", MetricState.Unavailable),
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            ("upstream_unavailable", MetricState.Unavailable),
        _ when (int)status >= 500 => ("upstream_unavailable", MetricState.Unavailable),
        _ => ("request_failed", MetricState.Error)
    };

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
            await PublishCurrentAsync(cancellationToken).ConfigureAwait(false);
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

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _activeRequestStop?.Cancel();
            CancelActivePublicationLocked();
        }
        _shutdown.Cancel();
        _httpClient.Dispose();
    }
}
