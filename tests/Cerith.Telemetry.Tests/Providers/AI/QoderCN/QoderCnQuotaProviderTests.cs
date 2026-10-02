#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests;

[TestClass]
public sealed class QoderCnQuotaProviderTests
{
    private static readonly string[] MetricIds =
    [
        "ai.qoder-cn.quota.included.total",
        "ai.qoder-cn.quota.included.used",
        "ai.qoder-cn.quota.included.remaining",
        "ai.qoder-cn.quota.add_on.total",
        "ai.qoder-cn.quota.add_on.used",
        "ai.qoder-cn.quota.add_on.remaining",
        "ai.qoder-cn.plan.expires_at",
        "ai.qoder-cn.org_resource_package.cap",
        "ai.qoder-cn.org_resource_package.used",
        "ai.qoder-cn.org_resource_package.remaining"
    ];

    [TestMethod]
    public void RegistersSeparateIncludedAddOnAndValuelessOrganizationMetrics()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(16);
        _ = new QoderCnQuotaProvider(registry, bus, new QoderCnCliClient(null), TimeProvider.System);
        var descriptors = registry.GetSnapshot().OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(MetricIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            descriptors.Select(item => item.Id.Value).ToArray());
        Assert.AreEqual(10, descriptors.Length);
        Assert.IsTrue(descriptors.Where(item => item.ValueKind == MetricValueKind.Double)
            .All(item => item.Unit == "Credits"));
        Assert.AreEqual(MetricValueKind.DateTimeOffset,
            descriptors.Single(item => item.Id.Value == "ai.qoder-cn.plan.expires_at").ValueKind);
    }

    [TestMethod]
    public async Task MissingCliPublishesUnavailableThenCancellationPublishesStopped()
    {
        var registry = new MetricRegistry();
        using var bus = new MetricBus(4);
        var provider = new QoderCnQuotaProvider(registry, bus, new QoderCnCliClient(null), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        await using var reader = bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();
        var firstSnapshot = reader.MoveNextAsync().AsTask();

        var run = provider.RunAsync(cancellation.Token);
        Assert.IsTrue(await firstSnapshot.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Unavailable, reader.Current.State);
        Assert.AreEqual("cli_not_found", reader.Current.ErrorCode);
        Assert.IsNull(reader.Current.Value);

        var stoppedSnapshot = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        Assert.IsTrue(await stoppedSnapshot.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MetricState.Stopped, reader.Current.State);
        Assert.IsNull(reader.Current.Value);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task UntrustedCliPublishesNoQuotaAndStopsCleanlyOnCancellation()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qoder-cn-provider-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        var executablePath = System.IO.Path.Combine(directory, QoderCnCliClient.ExecutableFileName);
        try
        {
            await System.IO.File.WriteAllTextAsync(executablePath, "unsigned candidate");
            var client = new QoderCnCliClient(QoderCnCliClient.FindExecutableFromPath(directory));
            var registry = new MetricRegistry();
            using var bus = new MetricBus(4);
            var provider = new QoderCnQuotaProvider(registry, bus, client, TimeProvider.System);
            using var cancellation = new CancellationTokenSource();
            await using var reader = bus.Subscribe(new MetricId(MetricIds[0])).GetAsyncEnumerator();
            var failureSnapshot = reader.MoveNextAsync().AsTask();

            var run = provider.RunAsync(cancellation.Token);
            Assert.IsTrue(await failureSnapshot.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(MetricState.Error, reader.Current.State);
            Assert.AreEqual("cli_untrusted", reader.Current.ErrorCode);
            Assert.IsNull(reader.Current.Value);

            var stoppedSnapshot = reader.MoveNextAsync().AsTask();
            cancellation.Cancel();
            Assert.IsTrue(await stoppedSnapshot.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(MetricState.Stopped, reader.Current.State);
            Assert.IsNull(reader.Current.Value);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (System.IO.File.Exists(executablePath))
                System.IO.File.Delete(executablePath);
            System.IO.Directory.Delete(directory);
        }
    }
}
