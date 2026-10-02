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
            var acquisition = await _acquire(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            var observedAt = _timeProvider.GetUtcNow();
            var points = acquisition.Payload is not null
                ? ArkAgentPlanParser.Parse(acquisition.Payload).Points
                : Enumerable.Repeat(
                    acquisition.IsUnavailable
                        ? ArkAgentPlanPoint.Unavailable(acquisition.FailureCode)
                        : ArkAgentPlanPoint.Error(acquisition.FailureCode ?? "cli_failed"),
                    _descriptors.Length).ToArray();

            for (var index = 0; index < _descriptors.Length; index++)
            {
                var point = points[index];
                var confidence = point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown;
                await _bus.PublishAsync(new MetricSnapshot(_descriptors[index].Id, point.Value, observedAt,
                    point.State, confidence, ArkAgentPlanCli.Source, point.ErrorCode), stop.Token).ConfigureAwait(false);
            }
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
