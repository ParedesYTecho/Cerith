#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

public sealed class SyntheticProvider
{
    private const string SyntheticSource = "synthetic";

    private readonly MetricRegistry _registry;
    private readonly MetricBus _bus;
    private readonly TimeProvider _timeProvider;
    // ponytail: single-caller sequence; serialize calls if runtime scheduling ever overlaps publications.
    private long _nextValue;
    private bool _sequenceExhausted;

    public SyntheticProvider(
        MetricRegistry registry,
        MetricBus bus,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);

        _registry = registry;
        _bus = bus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Descriptor = new MetricDescriptor(
            new MetricId("synthetic.counter"),
            "Synthetic Counter",
            "count",
            MetricValueKind.Int64);
    }

    public MetricDescriptor Descriptor { get; }

    public bool TryRegister() => _registry.TryRegister(Descriptor);

    public async ValueTask PublishNextAsync(CancellationToken cancellationToken = default)
    {
        if (_sequenceExhausted)
        {
            throw new InvalidOperationException("The synthetic counter exhausted the Int64 range.");
        }

        var snapshot = new MetricSnapshot(
            Descriptor.Id,
            MetricValue.From(_nextValue),
            _timeProvider.GetUtcNow(),
            MetricState.Fresh,
            MetricConfidence.Unknown,
            source: SyntheticSource);

        await _bus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (_nextValue == long.MaxValue)
        {
            _sequenceExhausted = true;
        }
        else
        {
            _nextValue++;
        }
    }
}
