#nullable enable

using System;
using Cerith.Core;
using Cerith.TelemetryService;
using LibreHardwareMonitor.Hardware;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class HardwareTelemetryProviderTests
{
    [TestMethod]
    public void DiagnosticSummarySeparatesMeasuredZeroAbsenceAndKnownFailure()
    {
        var summary = new HardwareDiagnosticSummary();
        var id = new MetricId("cpu.total.usage");
        var now = DateTimeOffset.UtcNow;
        summary.Record(new MetricSnapshot(id, MetricValue.From(0d), now, MetricState.Fresh, MetricConfidence.Measured));
        summary.Record(new MetricSnapshot(id, null, now, MetricState.Unavailable, MetricConfidence.Unknown));
        summary.Record(new MetricSnapshot(id, null, now, MetricState.Error, MetricConfidence.Unknown,
            errorCode: "cpu_refresh_failed"));

        Assert.AreEqual("fresh=1 stale=0 unavailable=1 error=1 codes=cpu_refresh_failed:1", summary.ToString());
    }

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
