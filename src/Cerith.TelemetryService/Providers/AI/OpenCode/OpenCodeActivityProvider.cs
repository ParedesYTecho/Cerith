#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.OpenCode;

internal sealed class OpenCodeActivityProvider
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopPublicationTimeout = TimeSpan.FromMilliseconds(250);
    private const string Source = "OpenCode.Activity.Local.v1";

    private static readonly (string Id, string Name, string SemanticType, MetricValueKind Kind, string? Unit)[] Definitions =
    [
        ("ai.opencode.activity.latest.tokens.input", "Latest Input Tokens", "count", MetricValueKind.Int64, "tokens"),
        ("ai.opencode.activity.latest.tokens.output", "Latest Output Tokens", "count", MetricValueKind.Int64, "tokens"),
        ("ai.opencode.activity.latest.tokens.reasoning", "Latest Reasoning Tokens", "count", MetricValueKind.Int64, "tokens"),
        ("ai.opencode.activity.latest.tokens.cache.read", "Latest Cache Read Tokens", "count", MetricValueKind.Int64, "tokens"),
        ("ai.opencode.activity.latest.tokens.cache.write", "Latest Cache Write Tokens", "count", MetricValueKind.Int64, "tokens"),
        ("ai.opencode.activity.latest.model.id", "Latest Model ID", "text", MetricValueKind.String, null),
        ("ai.opencode.activity.latest.model.provider", "Latest Model Provider", "text", MetricValueKind.String, null),
        ("ai.opencode.activity.latest.occurred_at", "Latest Activity Time", "timestamp", MetricValueKind.DateTimeOffset, null)
    ];

    private readonly MetricBus _bus;
    private readonly MetricDescriptor[] _descriptors;
    private readonly OpenCodeActivityReader _reader;
    private readonly TimeProvider _timeProvider;
    private int _runState;

    public OpenCodeActivityProvider(MetricRegistry registry, MetricBus bus)
        : this(registry, bus, new OpenCodeActivityReader(), TimeProvider.System)
    {
    }

    internal OpenCodeActivityProvider(MetricRegistry registry, MetricBus bus, OpenCodeActivityReader reader,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _descriptors = new MetricDescriptor[Definitions.Length];
        for (var i = 0; i < Definitions.Length; i++)
        {
            var definition = Definitions[i];
            var descriptor = new MetricDescriptor(new MetricId(definition.Id), $"OpenCode {definition.Name}",
                definition.SemanticType, definition.Kind, unit: definition.Unit);
            if (!registry.TryRegister(descriptor))
                throw new InvalidOperationException("OpenCode activity metric registration failed.");
            _descriptors[i] = descriptor;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
            throw new InvalidOperationException("OpenCode activity provider can run only once.");

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
            await PublishStoppedBestEffortAsync().ConfigureAwait(false);
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _runState) == 2, this);
        var reading = _reader.Read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await PublishAsync(reading, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync(OpenCodeActivityReadResult reading, DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var values = reading.Status == OpenCodeActivityReadStatus.Ready
            ? CreateValues(reading.Sample!)
            : new MetricValue?[_descriptors.Length];
        var state = reading.Status switch
        {
            OpenCodeActivityReadStatus.Ready => MetricState.Fresh,
            OpenCodeActivityReadStatus.Unavailable => MetricState.Unavailable,
            OpenCodeActivityReadStatus.Error => MetricState.Error,
            _ => throw new InvalidOperationException("Unknown OpenCode activity read status.")
        };

        for (var i = 0; i < _descriptors.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new MetricSnapshot(_descriptors[i].Id, values[i], observedAt, state,
                values[i].HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
                source: Source, errorCode: reading.ErrorCode);
            await _bus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    private static MetricValue?[] CreateValues(OpenCodeActivitySample sample) =>
    [
        MetricValue.From(sample.InputTokens),
        MetricValue.From(sample.OutputTokens),
        MetricValue.From(sample.ReasoningTokens),
        MetricValue.From(sample.CacheReadTokens),
        MetricValue.From(sample.CacheWriteTokens),
        MetricValue.From(sample.ModelId),
        MetricValue.From(sample.ProviderId),
        MetricValue.From(sample.OccurredAt)
    ];

    private async Task PublishStoppedBestEffortAsync()
    {
        using var deadline = new CancellationTokenSource(StopPublicationTimeout);
        try
        {
            var observedAt = _timeProvider.GetUtcNow();
            foreach (var descriptor in _descriptors)
            {
                await _bus.PublishAsync(new MetricSnapshot(descriptor.Id, null, observedAt, MetricState.Stopped,
                    MetricConfidence.Unknown, source: Source), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return;
        }
    }
}
