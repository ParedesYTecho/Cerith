#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal sealed class QoderCnQuotaProvider
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private const string Source = "QoderCN.CliControl.v1";

    private static readonly (string Id, string Name, string SemanticType, MetricValueKind Kind, string? Unit)[] Definitions =
    [
        ("ai.qoder-cn.quota.included.total", "Included Credits Total", "quota_limit", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.quota.included.used", "Included Credits Used", "quota_used", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.quota.included.remaining", "Included Credits Remaining", "quota_remaining", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.quota.add_on.total", "Add-on Credits Total", "quota_limit", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.quota.add_on.used", "Add-on Credits Used", "quota_used", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.quota.add_on.remaining", "Add-on Credits Remaining", "quota_remaining", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.plan.expires_at", "Plan Expires At", "timestamp", MetricValueKind.DateTimeOffset, null),
        ("ai.qoder-cn.org_resource_package.cap", "Organization Package Cap", "quota_limit", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.org_resource_package.used", "Organization Package Used", "quota_used", MetricValueKind.Double, "Credits"),
        ("ai.qoder-cn.org_resource_package.remaining", "Organization Package Remaining", "quota_remaining", MetricValueKind.Double, "Credits")
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly QoderCnCliClient _client;
    private readonly TimeProvider _timeProvider;
    private int _runState;

    public QoderCnQuotaProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new QoderCnCliClient(), TimeProvider.System)
    {
    }

    internal QoderCnQuotaProvider(MetricRegistry registry, MetricBus bus, QoderCnCliClient client,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _descriptors = new MetricDescriptor[Definitions.Length];
        for (var i = 0; i < Definitions.Length; i++)
        {
            var definition = Definitions[i];
            var descriptor = new MetricDescriptor(new MetricId(definition.Id), $"Qoder CN {definition.Name}",
                definition.SemanticType, definition.Kind, unit: definition.Unit);
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("Qoder CN quota metric registration failed.");
            _descriptors[i] = descriptor;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
            throw new InvalidOperationException("Qoder CN quota provider can run only once.");

        try
        {
            while (true)
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _runState, 2);
            await PublishStoppedAsync().ConfigureAwait(false);
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _runState) == 2, this);
        var reading = await _client.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await PublishAsync(reading, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync(QoderCnQuotaReading reading, DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < _descriptors.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = reading.Points[i];
            var snapshot = new MetricSnapshot(_descriptors[i].Id, point.Value, observedAt, point.State,
                point.Value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                source: Source, errorCode: point.ErrorCode);
            await _bus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishStoppedAsync()
    {
        var observedAt = _timeProvider.GetUtcNow();
        for (var i = 0; i < _descriptors.Length; i++)
        {
            var snapshot = new MetricSnapshot(_descriptors[i].Id, null, observedAt, MetricState.Stopped,
                MetricConfidence.Unknown, source: Source);
            await _bus.PublishAsync(snapshot).ConfigureAwait(false);
        }
    }
}
