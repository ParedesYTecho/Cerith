#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.GitHubCopilot;

internal sealed class CopilotQuotaProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan StopPublishTimeout = TimeSpan.FromMilliseconds(250);

    private const string Source = "GitHubCopilot.GhCli.ReadOnly.v1";

    private static readonly string[] Buckets = ["chat", "completions", "premium_interactions"];
    private static readonly MetricDefinition[] Definitions = CreateDefinitions();

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly CopilotQuotaAcquirer _acquirer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private string? _activeAccountKey;
    private long _generation;
    private int _runStarted;
    private bool _disposed;

    internal CopilotQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new CopilotQuotaAcquirer(), TimeProvider.System)
    {
    }

    internal CopilotQuotaProvider(MetricRegistry registry, MetricBus bus,
        CopilotQuotaAcquirer acquirer, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _descriptors = Definitions.Select(definition => new MetricDescriptor(
            new MetricId(definition.Id), definition.DisplayName, definition.SemanticType,
            definition.ValueKind, definition.Description, definition.Unit)).ToArray();
        foreach (var descriptor in _descriptors)
        {
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("GitHub Copilot metric registration failed.");
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("GitHub Copilot provider can only be run once.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            while (true)
            {
                await PollOnceAsync(stop.Token).ConfigureAwait(false);
                await Task.Delay(PollInterval, _timeProvider, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Increment(ref _generation);
            await PublishStoppedAsync().ConfigureAwait(false);
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(stop.Token).ConfigureAwait(false);
        var requestGeneration = Interlocked.Read(ref _generation);
        try
        {
            CopilotQuotaReading reading;
            try
            {
                reading = await _acquirer.AcquireAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                throw;
            }
            catch (CopilotQuotaAcquisitionException exception)
            {
                if (exception.ErrorCode is "identity_mismatch" or "identity_unavailable")
                {
                    _activeAccountKey = null;
                    requestGeneration = Interlocked.Increment(ref _generation);
                }

                var state = IsUnavailable(exception.ErrorCode) ? MetricState.Unavailable : MetricState.Error;
                await PublishEmptyAsync(state, state == MetricState.Error ? exception.ErrorCode : null,
                    _timeProvider.GetUtcNow(), stop.Token, requestGeneration).ConfigureAwait(false);
                return;
            }

            if (stop.IsCancellationRequested || requestGeneration != Interlocked.Read(ref _generation)) return;
            var accountChanged = _activeAccountKey is not null &&
                !StringComparer.Ordinal.Equals(_activeAccountKey, reading.StableAccountKey);
            if (accountChanged)
                Interlocked.Increment(ref _generation);
            _activeAccountKey = reading.StableAccountKey;
            requestGeneration = Interlocked.Read(ref _generation);

            if (accountChanged)
            {
                await PublishEmptyAsync(MetricState.Unavailable, null, _timeProvider.GetUtcNow(), stop.Token,
                    requestGeneration).ConfigureAwait(false);
                if (stop.IsCancellationRequested || requestGeneration != Interlocked.Read(ref _generation)) return;
            }

            await PublishReadingAsync(reading, _timeProvider.GetUtcNow(), stop.Token, requestGeneration)
                .ConfigureAwait(false);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _generation);
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task PublishReadingAsync(CopilotQuotaReading reading, DateTimeOffset observedAt,
        CancellationToken cancellationToken, long generation)
    {
        var values = new Dictionary<string, MetricValue>(StringComparer.Ordinal)
        {
            ["ai.github_copilot.billing.token_based"] = MetricValue.From(reading.TokenBasedBilling)
        };

        foreach (var bucketName in Buckets)
        {
            if (!reading.Buckets.TryGetValue(bucketName, out var bucket)) continue;
            var prefix = $"ai.github_copilot.quota.{bucketName}.";
            if (bucket.PercentRemaining is double percent)
                values[prefix + "percent_remaining"] = MetricValue.From(percent);
            if (bucket.ResetAt is DateTimeOffset resetAt)
                values[prefix + "reset_at"] = MetricValue.From(resetAt);
            values[prefix + "unlimited"] = MetricValue.From(bucket.Unlimited);
            if (bucket.HasQuota is bool hasQuota)
                values[prefix + "has_quota"] = MetricValue.From(hasQuota);
        }

        foreach (var descriptor in _descriptors)
        {
            if (generation != Interlocked.Read(ref _generation)) return;
            var hasValue = values.TryGetValue(descriptor.Id.Value, out var value);
            var snapshot = new MetricSnapshot(descriptor.Id, hasValue ? value : null, observedAt,
                hasValue ? MetricState.Fresh : MetricState.Unavailable,
                hasValue ? MetricConfidence.Measured : MetricConfidence.Unknown, source: Source);
            await _bus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishEmptyAsync(MetricState state, string? errorCode, DateTimeOffset observedAt,
        CancellationToken cancellationToken, long generation)
    {
        foreach (var descriptor in _descriptors)
        {
            if (generation != Interlocked.Read(ref _generation)) return;
            await _bus.PublishAsync(new MetricSnapshot(descriptor.Id, null, observedAt, state,
                MetricConfidence.Unknown, source: Source, errorCode: errorCode), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(StopPublishTimeout);
        var observedAt = _timeProvider.GetUtcNow();
        _activeAccountKey = null;
        foreach (var descriptor in _descriptors)
        {
            try
            {
                await _bus.PublishAsync(new MetricSnapshot(descriptor.Id, null, observedAt,
                    MetricState.Stopped, MetricConfidence.Unknown, source: Source), deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static bool IsUnavailable(string errorCode) => errorCode is
        "auth_unavailable" or "gh_unavailable" or "unsupported_host" or "identity_mismatch" or "identity_unavailable";

    private static MetricDefinition[] CreateDefinitions()
    {
        // Per-bucket amounts remain parser-only until their units are documented by a first-party source.
        var definitions = new List<MetricDefinition>
        {
            new("ai.github_copilot.billing.token_based", "Copilot token-based billing", "billing_mode",
                MetricValueKind.Boolean, "Whether the source reports token-based billing.", null)
        };

        foreach (var bucket in Buckets)
        {
            var prefix = $"ai.github_copilot.quota.{bucket}.";
            var label = bucket.Replace('_', ' ');
            definitions.Add(new MetricDefinition(prefix + "percent_remaining", $"Copilot {label} quota remaining",
                "quota_percent_remaining", MetricValueKind.Double,
                "Direct source-reported percentage; no quota amount is inferred.", "%"));
            definitions.Add(new MetricDefinition(prefix + "reset_at", $"Copilot {label} quota reset",
                "timestamp", MetricValueKind.DateTimeOffset,
                "Direct source-reported per-bucket reset time.", null));
            definitions.Add(new MetricDefinition(prefix + "unlimited", $"Copilot {label} unlimited",
                "quota_unlimited", MetricValueKind.Boolean, "Direct source-reported unlimited flag.", null));
            definitions.Add(new MetricDefinition(prefix + "has_quota", $"Copilot {label} has-quota flag",
                "quota_admission_flag", MetricValueKind.Boolean,
                "Direct source flag; independent of amounts and not authoritative for token-based snapshots.", null));
        }

        return definitions.ToArray();
    }

    private sealed record MetricDefinition(string Id, string DisplayName, string SemanticType,
        MetricValueKind ValueKind, string? Description, string? Unit);
}
