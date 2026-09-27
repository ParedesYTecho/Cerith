#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using LibreHardwareMonitor.Hardware;

namespace Cerith.TelemetryService;

internal sealed class CpuUsageProvider : IDisposable
{
    private const string Source = "LibreHardwareMonitor";
    private readonly Computer _computer = new() { IsCpuEnabled = true };
    private readonly MetricBus _bus;
    private readonly IHardware? _cpu;
    private readonly ISensor? _sensor;

    public MetricDescriptor Descriptor { get; } = new(
        new MetricId("cpu.total.usage"), "CPU Total Usage", "percentage", MetricValueKind.Double,
        unit: "%");

    public CpuUsageProvider(MetricRegistry registry, MetricBus bus)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        try
        {
            _computer.Open();
            var matches = _computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Cpu)
                .SelectMany(h => h.Sensors
                    .Where(s => s.SensorType == SensorType.Load && s.Name == "CPU Total")
                    .Select(s => (Hardware: h, Sensor: s)))
                .ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous CPU Total sensor.");
            if (matches.Length == 1) (_cpu, _sensor) = matches[0];
            if (!registry.TryRegister(Descriptor)) throw new InvalidOperationException("CPU metric registration failed.");
        }
        catch
        {
            _computer.Close();
            throw;
        }
    }

    public async ValueTask<MetricState> PublishNextAsync(CancellationToken cancellationToken = default)
    {
        MetricValue? value = null;
        var state = MetricState.Unavailable;
        string? errorCode = null;
        if (_sensor is not null)
        {
            try
            {
                _cpu!.Update();
                var reading = _sensor.Value;
                if (reading.HasValue)
                {
                    if (float.IsFinite(reading.Value) && reading.Value is >= 0 and <= 100)
                    {
                        value = MetricValue.From((double)reading.Value);
                        state = MetricState.Fresh;
                    }
                    else
                    {
                        state = MetricState.Error;
                        errorCode = "invalid_percentage";
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                state = MetricState.Error;
                errorCode = "cpu_refresh_failed";
            }
        }

        var snapshot = new MetricSnapshot(Descriptor.Id, value, DateTimeOffset.UtcNow,
            state, value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
            source: Source, errorCode: errorCode);
        await _bus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return state;
    }

    public void Dispose() => _computer.Close();
}
