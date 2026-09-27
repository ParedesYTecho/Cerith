#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using LibreHardwareMonitor.Hardware;

namespace Cerith.TelemetryService;

internal sealed class HardwareTelemetryProvider : IDisposable
{
    private const string HardwareSource = "LibreHardwareMonitor";
    private const string MemorySource = "GlobalMemoryStatusEx";
    private const double Mebibyte = 1024d * 1024d;
    private const double Gibibyte = 1024d * 1024d * 1024d;
    private const double Gigabyte = 1_000_000_000d;
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = true
    };
    private readonly MetricBus _bus;
    private readonly (IHardware Hardware, MetricDescriptor Descriptor, ISensor? Sensor, SensorType Type, bool Ambiguous)[] _sensors;
    private readonly MetricDescriptor[] _memory;

    public HardwareTelemetryProvider(MetricRegistry registry, MetricBus bus)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        try
        {
            _computer.Open();
            var bindings = new List<(IHardware, MetricDescriptor, ISensor?, SensorType, bool)>();
            var hardware = _computer.Hardware;
            foreach (var cpu in hardware.Where(h => h.HardwareType == HardwareType.Cpu))
                Add(cpu, "cpu.total.usage", "CPU Total Usage", "percentage", "%", MetricValueKind.Double,
                    SensorType.Load, "CPU Total");

            var gpus = hardware
                .Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
                .OrderBy(h => h.Identifier.ToString(), StringComparer.Ordinal)
                .ToArray();
            if (gpus.Select(h => h.Identifier.ToString()).Distinct(StringComparer.Ordinal).Count() != gpus.Length)
                throw new InvalidOperationException("Duplicate GPU identifiers.");

            for (var index = 0; index < gpus.Length; index++)
            {
                var gpu = gpus[index];
                var prefix = $"gpu.{index}";
                Add(gpu, $"{prefix}.usage", $"GPU {index} Usage", "percentage", "%", MetricValueKind.Double,
                    SensorType.Load, "GPU Core");
                Add(gpu, $"{prefix}.temperature.core", $"GPU {index} Core Temperature", "temperature", "°C", MetricValueKind.Double,
                    SensorType.Temperature, "GPU Core");
                Add(gpu, $"{prefix}.clock.core", $"GPU {index} Core Clock", "frequency", "MHz", MetricValueKind.Double,
                    SensorType.Clock, "GPU Core");
                Add(gpu, $"{prefix}.vram.used.bytes", $"GPU {index} VRAM Used", "bytes", "B", MetricValueKind.Double,
                    SensorType.SmallData, "GPU Memory Used");
                Add(gpu, $"{prefix}.vram.total.bytes", $"GPU {index} VRAM Total", "bytes", "B", MetricValueKind.Double,
                    SensorType.SmallData, "GPU Memory Total");
                Add(gpu, $"{prefix}.fan.rpm", $"GPU {index} Fan", "rate", "RPM", MetricValueKind.Double,
                    SensorType.Fan, null);
            }

            var storage = hardware.Where(h => h.HardwareType == HardwareType.Storage)
                .OrderBy(h => h.Identifier.ToString(), StringComparer.Ordinal).ToArray();
            var network = hardware.Where(h => h.HardwareType == HardwareType.Network)
                .OrderBy(h => h.Identifier.ToString(), StringComparer.Ordinal).ToArray();
            if (storage.Select(h => h.Identifier.ToString()).Distinct(StringComparer.Ordinal).Count() != storage.Length ||
                network.Select(h => h.Identifier.ToString()).Distinct(StringComparer.Ordinal).Count() != network.Length)
                throw new InvalidOperationException("Duplicate storage or network identifiers.");

            for (var index = 0; index < storage.Length; index++)
            {
                var disk = storage[index];
                var prefix = $"storage.{index}";
                Add(disk, $"{prefix}.usage", $"Storage {index} Usage", "percentage", "%", MetricValueKind.Double,
                    SensorType.Load, "Used Space");
                Add(disk, $"{prefix}.read.bytes_per_second", $"Storage {index} Read Rate", "rate", "B/s", MetricValueKind.Double,
                    SensorType.Throughput, "Read Rate");
                Add(disk, $"{prefix}.write.bytes_per_second", $"Storage {index} Write Rate", "rate", "B/s", MetricValueKind.Double,
                    SensorType.Throughput, "Write Rate");
                Add(disk, $"{prefix}.total.bytes", $"Storage {index} Total", "bytes", "B", MetricValueKind.Double,
                    SensorType.Data, "Total Space");
                Add(disk, $"{prefix}.free.bytes", $"Storage {index} Free", "bytes", "B", MetricValueKind.Double,
                    SensorType.Data, "Free Space");
            }

            for (var index = 0; index < network.Length; index++)
            {
                var nic = network[index];
                var prefix = $"network.{index}";
                Add(nic, $"{prefix}.download.bytes_per_second", $"Network {index} Download", "rate", "B/s", MetricValueKind.Double,
                    SensorType.Throughput, "Download Speed");
                Add(nic, $"{prefix}.upload.bytes_per_second", $"Network {index} Upload", "rate", "B/s", MetricValueKind.Double,
                    SensorType.Throughput, "Upload Speed");
                Add(nic, $"{prefix}.usage", $"Network {index} Usage", "percentage", "%", MetricValueKind.Double,
                    SensorType.Load, "Network Utilization");
            }

            _sensors = bindings.ToArray();
            _memory =
            [
                new(new MetricId("ram.used.bytes"), "RAM Used", "bytes", MetricValueKind.Int64, unit: "B"),
                new(new MetricId("ram.available.bytes"), "RAM Available", "bytes", MetricValueKind.Int64, unit: "B"),
                new(new MetricId("ram.total.bytes"), "RAM Total", "bytes", MetricValueKind.Int64, unit: "B"),
                new(new MetricId("ram.usage"), "RAM Usage", "percentage", MetricValueKind.Double, unit: "%")
            ];
            foreach (var descriptor in _sensors.Select(s => s.Descriptor).Concat(_memory))
                if (!registry.TryRegister(descriptor))
                    throw new InvalidOperationException($"Metric registration failed: {descriptor.Id}");
            void Add(IHardware hardware, string id, string title, string semantic, string unit, MetricValueKind valueKind,
                SensorType type, string? name)
            {
                var matches = hardware.Sensors.Where(s => s.SensorType == type && (name is null || s.Name == name)).ToArray();
                bindings.Add((hardware, new MetricDescriptor(new MetricId(id), title, semantic, valueKind, unit: unit),
                    matches.Length == 1 ? matches[0] : null, type, matches.Length > 1));
            }
        }
        catch
        {
            _computer.Close();
            throw;
        }
    }

    public async ValueTask PublishNextAsync(CancellationToken cancellationToken = default)
    {
        var present = _computer.Hardware.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var group in _sensors.GroupBy(s => s.Hardware))
        {
            string? refreshError = null;
            var removed = group.Key.HardwareType is HardwareType.Storage or HardwareType.Network && !present.Contains(group.Key);
            try { if (!removed) group.Key.Update(); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                refreshError = group.Key.HardwareType switch
                {
                    HardwareType.Cpu => "cpu_refresh_failed",
                    HardwareType.Storage => "storage_refresh_failed",
                    HardwareType.Network => "network_refresh_failed",
                    _ => "gpu_refresh_failed"
                };
            }
            foreach (var binding in group)
            {
                var state = MetricState.Unavailable;
                MetricValue? value = null;
                string? error = refreshError;
                if (error is null && binding.Ambiguous) error = "ambiguous_sensor";
                try
                {
                    // LHM 0.9.6 turns missing TotalFreeSize into a Free Space value of zero.
                    var freeSpaceKnown = !binding.Descriptor.Id.Value.EndsWith(".free.bytes", StringComparison.Ordinal) ||
                        binding.Hardware.Sensors.Any(s => s.SensorType == SensorType.Load && s.Name == "Used Space" && s.Value.HasValue);
                    if (!removed && freeSpaceKnown && error is null && binding.Sensor?.Value is float reading)
                    {
                        var positive = binding.Hardware.HardwareType == HardwareType.Storage &&
                            binding.Descriptor.Id.Value.EndsWith(".total.bytes", StringComparison.Ordinal);
                        if (TryNormalize(binding.Hardware.HardwareType, binding.Type, reading, positive, out var normalized))
                        {
                            value = MetricValue.From(normalized);
                            state = MetricState.Fresh;
                        }
                        else error = binding.Type == SensorType.Load ? "invalid_percentage" : "invalid_reading";
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { error = "sensor_read_failed"; }
                if (error is not null) state = MetricState.Error;
                await Publish(binding.Descriptor.Id, value, state, HardwareSource, error, cancellationToken);
            }
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memoryError = GlobalMemoryStatusEx(ref memory) ? null : "memory_read_failed";
        if (memoryError is null && (memory.TotalPhysical == 0 || memory.TotalPhysical > long.MaxValue ||
                                    memory.AvailablePhysical > memory.TotalPhysical))
            memoryError = "invalid_memory";
        var memoryState = memoryError is null ? MetricState.Fresh : MetricState.Error;
        var used = memory.TotalPhysical - memory.AvailablePhysical;
        MetricValue?[] memoryValues = memoryError is null
            ? [MetricValue.From((long)used), MetricValue.From((long)memory.AvailablePhysical),
               MetricValue.From((long)memory.TotalPhysical),
               MetricValue.From(100d * used / memory.TotalPhysical)]
            : [null, null, null, null];
        for (var index = 0; index < _memory.Length; index++)
            await Publish(_memory[index].Id, memoryValues[index], memoryState, MemorySource, memoryError, cancellationToken);
    }

    internal static bool TryNormalize(HardwareType hardwareType, SensorType type, float reading, bool positive, out double value)
    {
        value = 0;
        if (!float.IsFinite(reading) || (type == SensorType.Load ? reading is < 0 or > 100 :
            type != SensorType.Temperature && reading < 0) || (positive && reading <= 0))
            return false;

        var scale = type switch
        {
            SensorType.SmallData => Mebibyte,
            SensorType.Data when hardwareType == HardwareType.Storage => Gigabyte,
            SensorType.Data => Gibibyte,
            _ => 1d
        };
        value = reading * scale;
        return double.IsFinite(value);
    }

    private async ValueTask Publish(MetricId id, MetricValue? value, MetricState state, string source,
        string? errorCode, CancellationToken cancellationToken)
    {
        await _bus.PublishAsync(new MetricSnapshot(id, value, DateTimeOffset.UtcNow, state,
            value.HasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
            source: source, errorCode: errorCode), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _computer.Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
