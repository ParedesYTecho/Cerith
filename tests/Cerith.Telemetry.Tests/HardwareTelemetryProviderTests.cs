#nullable enable

using Cerith.TelemetryService;
using LibreHardwareMonitor.Hardware;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class HardwareTelemetryProviderTests
{
    [TestMethod]
    public void NormalizesProviderUnitsAndRejectsInvalidReadings()
    {
        Assert.IsTrue(HardwareTelemetryProvider.TryNormalize(HardwareType.Storage, SensorType.Data, 1.5f, false, out var bytes));
        Assert.AreEqual(1_500_000_000d, bytes);
        Assert.IsTrue(HardwareTelemetryProvider.TryNormalize(HardwareType.Storage, SensorType.Throughput, 125f, false, out var rate));
        Assert.AreEqual(125d, rate);
        Assert.IsTrue(HardwareTelemetryProvider.TryNormalize(HardwareType.GpuAmd, SensorType.SmallData, 2f, false, out var vram));
        Assert.AreEqual(2_097_152d, vram);
        Assert.IsTrue(HardwareTelemetryProvider.TryNormalize(HardwareType.Network, SensorType.Load, 0f, false, out var zero));
        Assert.AreEqual(0d, zero);
        Assert.IsFalse(HardwareTelemetryProvider.TryNormalize(HardwareType.Storage, SensorType.Data, 0f, true, out _));
        Assert.IsFalse(HardwareTelemetryProvider.TryNormalize(HardwareType.Network, SensorType.Load, 101f, false, out _));
        Assert.IsFalse(HardwareTelemetryProvider.TryNormalize(HardwareType.Network, SensorType.Throughput, -1f, false, out _));
        Assert.IsFalse(HardwareTelemetryProvider.TryNormalize(HardwareType.Network, SensorType.Load, float.NaN, false, out _));
    }
}
