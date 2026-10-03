#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.ArkAgentPlan;

internal sealed class ArkAgentPlanProvider : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ShutdownPublicationTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumResetWait = TimeSpan.FromDays(1);

    private static readonly (string Suffix, string Name, string SemanticType, MetricValueKind ValueKind, string? Unit)[]
        MetricDefinitions =
        [
            ("used", "Used", "quota_used", MetricValueKind.Decimal, "AFP"),
            ("total", "Total", "quota_limit", MetricValueKind.Decimal, "AFP"),
            ("used_percent", "Used Percentage", "quota_used_percent", MetricValueKind.Decimal, "%"),
            ("reset", "Reset At", "timestamp", MetricValueKind.DateTimeOffset, null)
        ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly Func<CancellationToken, Task<ArkCliResult>> _acquire;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public ArkAgentPlanProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, ArkAgentPlanCli.ReadAsync, TimeProvider.System)
    {
    }

    internal ArkAgentPlanProvider(MetricRegistry registry, MetricBus bus,
        Func<CancellationToken, Task<ArkCliResult>> acquire, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _descriptors = CreateDescriptors();
        foreach (var descriptor in _descriptors)
        {
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("Ark Agent Plan metric registration failed.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
            return;
        }
        finally
        {
            await PublishStoppedAsync().ConfigureAwait(false);
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollLock.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            var acquisition = await _acquire(stop.Token).WaitAsync(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            var observedAt = _timeProvider.GetUtcNow();
            ArkAgentPlanPoint[] points;
            if (acquisition.Payload is not null)
            {
                var reading = ArkAgentPlanParser.Parse(acquisition.Payload);
                if (reading.Identity is not null)
                {
                    var currentReading = acquisition.CurrentUsagePayload is null
                        ? null
                        : ArkAgentPlanParser.Parse(acquisition.CurrentUsagePayload);
                    if (currentReading?.Identity is null || !reading.Identity.CanCompareTo(currentReading.Identity))
                        reading = ArkAgentPlanReading.Failure("identity_unverified");
                    else if (!reading.Identity.MatchesCurrent(currentReading.Identity) ||
                             reading.IsSubscribed != currentReading.IsSubscribed ||
                             !StringComparer.Ordinal.Equals(reading.Tier, currentReading.Tier))
                        reading = ArkAgentPlanReading.Failure("identity_changed");
                }
                points = reading.Points;
                if (reading.Identity is not null && reading.IsSubscribed && !reading.HasDailyPeriod)
                {
                    ArkAgentPlanPoint[] dailyPoints;
                    if (acquisition.DailyPayload is not null)
                    {
                        dailyPoints = ArkAgentPlanParser.ParseDaily(acquisition.DailyPayload,
                            reading.Identity, reading.Tier);
                    }
                    else if (acquisition.DailyFailureCode is not null || acquisition.DailyIsUnavailable)
                    {
                        var dailyPoint = acquisition.DailyIsUnavailable
                            ? ArkAgentPlanPoint.Unavailable(acquisition.DailyFailureCode)
                            : ArkAgentPlanPoint.Error(acquisition.DailyFailureCode ?? "cli_failed");
                        dailyPoints = Enumerable.Repeat(dailyPoint, ArkAgentPlanReading.MetricsPerPeriod).ToArray();
                    }
                    else
                    {
                        dailyPoints = Enumerable.Repeat(ArkAgentPlanPoint.Unavailable(),
                            ArkAgentPlanReading.MetricsPerPeriod).ToArray();
                    }
                    Array.Copy(dailyPoints, 0, points, ArkAgentPlanReading.MetricsPerPeriod,
                        ArkAgentPlanReading.MetricsPerPeriod);
                }
            }
            else
            {
                points = Enumerable.Repeat(
                    acquisition.IsUnavailable
                        ? ArkAgentPlanPoint.Unavailable(acquisition.FailureCode)
                        : ArkAgentPlanPoint.Error(acquisition.FailureCode ?? "cli_failed"),
                    _descriptors.Length).ToArray();
            }

            InvalidateElapsedPeriods(points, observedAt);

            for (var index = 0; index < _descriptors.Length; index++)
                await PublishPointAsync(index, points, observedAt, stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _shutdown.Cancel();
    }

    private MetricDescriptor[] CreateDescriptors()
    {
        var descriptors = new MetricDescriptor[ArkAgentPlanReading.Periods.Length * MetricDefinitions.Length];
        var index = 0;
        foreach (var period in ArkAgentPlanReading.Periods)
        {
            foreach (var definition in MetricDefinitions)
            {
                descriptors[index++] = new MetricDescriptor(
                    new MetricId($"ai.ark.agent_plan.{period}.{definition.Suffix}"),
                    $"Ark Agent Plan {period} {definition.Name}",
                    definition.SemanticType,
                    definition.ValueKind,
                    unit: definition.Unit);
            }
        }
        return descriptors;
    }

    private static void InvalidateElapsedPeriods(ArkAgentPlanPoint[] points, DateTimeOffset observedAt)
    {
        for (var period = 0; period < ArkAgentPlanReading.Periods.Length; period++)
        {
            var offset = period * ArkAgentPlanReading.MetricsPerPeriod;
            var reset = points[offset + 3];
            if (reset.State != MetricState.Fresh || reset.Value is not MetricValue resetValue ||
                resetValue.Kind != MetricValueKind.DateTimeOffset ||
                resetValue.AsDateTimeOffset() > observedAt)
                continue;

            for (var index = 0; index < ArkAgentPlanReading.MetricsPerPeriod; index++)
                points[offset + index] = ArkAgentPlanPoint.Unavailable("reset_elapsed");
        }
    }

    private async Task PublishPointAsync(int index, ArkAgentPlanPoint[] points, DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            InvalidateElapsedPeriods(points, now);
            var point = points[index];
            var periodOffset = index / ArkAgentPlanReading.MetricsPerPeriod *
                               ArkAgentPlanReading.MetricsPerPeriod;
            var reset = points[periodOffset + ArkAgentPlanReading.MetricsPerPeriod - 1];
            var resetAt = point.State == MetricState.Fresh && reset.State == MetricState.Fresh &&
                reset.Value is MetricValue resetValue && resetValue.Kind == MetricValueKind.DateTimeOffset
                    ? resetValue.AsDateTimeOffset()
                    : (DateTimeOffset?)null;
            var confidence = point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown;
            var snapshot = new MetricSnapshot(_descriptors[index].Id, point.Value, observedAt,
                point.State, confidence, ArkAgentPlanCli.Source, point.ErrorCode);
            if (resetAt is not DateTimeOffset deadline || deadline <= now)
            {
                await _bus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
                return;
            }

            var resetWait = deadline - now;
            if (resetWait > MaximumResetWait) resetWait = MaximumResetWait;
            using var expiry = new CancellationTokenSource(resetWait, _timeProvider);
            using var publishStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                expiry.Token);
            try
            {
                await _bus.PublishAsync(snapshot, publishStop.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (expiry.IsCancellationRequested &&
                                                      !cancellationToken.IsCancellationRequested)
            {
                continue;
            }
        }
    }

    private async Task PublishStoppedAsync()
    {
        using var deadline = new CancellationTokenSource(ShutdownPublicationTimeout);
        var stoppedAt = _timeProvider.GetUtcNow();
        foreach (var descriptor in _descriptors)
        {
            try
            {
                await _bus.PublishAsync(new MetricSnapshot(descriptor.Id, null, stoppedAt, MetricState.Stopped,
                    MetricConfidence.Unknown, ArkAgentPlanCli.Source), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
