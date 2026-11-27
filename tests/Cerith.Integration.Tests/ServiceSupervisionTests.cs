#nullable enable

using System;
using System.IO;
using System.Threading;
using Cerith.Core;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class ServiceSupervisionTests
{
    [TestMethod]
    public void ServiceIsExpectedNextToTheApp() =>
        Assert.AreEqual(Path.Combine(@"C:\app", "Cerith.TelemetryService.exe"),
            ServiceSupervisor.ResolvePath(["--theme=dark"], @"C:\app"));

    [TestMethod]
    public void ServicePathOptionWins() =>
        Assert.AreEqual(@"D:\build\svc.exe",
            ServiceSupervisor.ResolvePath([@"--service-path=D:\build\svc.exe"], @"C:\app"));

    [TestMethod]
    public void RestartDelayDoublesUpToFiveMinutes()
    {
        Assert.AreEqual(TimeSpan.Zero, ServiceSupervisor.Delay(0));
        Assert.AreEqual(TimeSpan.FromSeconds(2), ServiceSupervisor.Delay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(8), ServiceSupervisor.Delay(3));
        Assert.AreEqual(TimeSpan.FromMinutes(5), ServiceSupervisor.Delay(9));
        Assert.AreEqual(TimeSpan.FromMinutes(5), ServiceSupervisor.Delay(int.MaxValue));
    }

    [TestMethod]
    public void OnlyQuickExitsAccumulate()
    {
        Assert.AreEqual(3, ServiceSupervisor.NextFailures(2, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, ServiceSupervisor.NextFailures(2, TimeSpan.FromMinutes(2)));
    }

    [TestMethod]
    public void SecondInstanceIsRefusedUntilTheFirstEnds()
    {
        var name = $@"Local\Cerith.Test.{Guid.NewGuid():N}";
        Assert.IsFalse(SingleInstance.IsRunning(name));
        var first = SingleInstance.TryAcquire(name, () => { });
        Assert.IsNotNull(first);
        Assert.IsTrue(SingleInstance.IsRunning(name));
        Assert.IsNull(SingleInstance.TryAcquire(name, () => { }));
        first.Dispose();
        Assert.IsFalse(SingleInstance.IsRunning(name));
    }

    [TestMethod]
    public void SignalReachesTheRunningInstance()
    {
        var name = $@"Local\Cerith.Test.{Guid.NewGuid():N}";
        Assert.IsFalse(SingleInstance.Signal(name));
        using var received = new ManualResetEventSlim();
        using var instance = SingleInstance.TryAcquire(name, received.Set);
        Assert.IsTrue(SingleInstance.Signal(name));
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(5)));
    }
}
