#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Core.Tests;

[TestClass]
public sealed class MetricRegistryTests
{
    [TestMethod]
    public void StartsEmpty()
    {
        var registry = new MetricRegistry();

        Assert.AreEqual(0, registry.Count);
        Assert.AreEqual(0, registry.GetSnapshot().Count);
    }

    [TestMethod]
    public void RegistersAndLooksUpTheOriginalDescriptor()
    {
        var registry = new MetricRegistry();
        var descriptor = CreateDescriptor("cpu.total.usage");

        Assert.IsTrue(registry.TryRegister(descriptor));
        Assert.AreEqual(1, registry.Count);
        Assert.IsTrue(registry.TryGet(descriptor.Id, out var found));
        Assert.AreSame(descriptor, found);
    }

    [TestMethod]
    public void LookupOfUnknownIdReturnsFalseAndNull()
    {
        var registry = new MetricRegistry();

        Assert.IsFalse(registry.TryGet(new MetricId("unknown.metric"), out var found));
        Assert.IsNull(found);
    }

    [TestMethod]
    public void DuplicateIdDoesNotReplaceTheFirstDescriptor()
    {
        var registry = new MetricRegistry();
        var original = CreateDescriptor("cpu.total.usage", "CPU usage");
        var duplicate = CreateDescriptor("cpu.total.usage", "Replacement");

        Assert.IsTrue(registry.TryRegister(original));
        Assert.IsFalse(registry.TryRegister(duplicate));
        Assert.AreEqual(1, registry.Count);
        Assert.IsTrue(registry.TryGet(original.Id, out var found));
        Assert.AreSame(original, found);
        Assert.AreEqual("CPU usage", found!.DisplayName);
    }

    [TestMethod]
    public void IdsThatDifferOnlyByCasingAreDistinct()
    {
        var registry = new MetricRegistry();
        var lower = CreateDescriptor("cpu.total.usage");
        var upper = CreateDescriptor("CPU.total.usage");

        Assert.IsTrue(registry.TryRegister(lower));
        Assert.IsTrue(registry.TryRegister(upper));
        Assert.AreEqual(2, registry.Count);
        Assert.IsTrue(registry.TryGet(lower.Id, out var foundLower));
        Assert.IsTrue(registry.TryGet(upper.Id, out var foundUpper));
        Assert.AreSame(lower, foundLower);
        Assert.AreSame(upper, foundUpper);
    }

    [TestMethod]
    public void SnapshotIsReadOnlyAndUnaffectedByLaterRegistrations()
    {
        var registry = new MetricRegistry();
        var first = CreateDescriptor("first.metric");
        var second = CreateDescriptor("second.metric");
        Assert.IsTrue(registry.TryRegister(first));

        var snapshot = registry.GetSnapshot();
        CollectionAssert.AreEquivalent(new[] { first }, snapshot.ToArray());
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<MetricDescriptor>)snapshot).Add(second));

        Assert.IsTrue(registry.TryRegister(second));
        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(first, snapshot.Single());
        CollectionAssert.AreEquivalent(new[] { first, second }, registry.GetSnapshot().ToArray());
    }

    [TestMethod]
    public void RejectsNullDescriptorAndId()
    {
        var registry = new MetricRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.TryRegister(null!));
        Assert.Throws<ArgumentNullException>(() => registry.TryGet(null!, out _));
    }

    [TestMethod]
    public void ConcurrentRegistrationsOfDistinctIdsAreRetained()
    {
        const int descriptorCount = 32;
        var registry = new MetricRegistry();

        Parallel.For(0, descriptorCount, index =>
            registry.TryRegister(CreateDescriptor($"parallel.metric.{index}")));

        Assert.AreEqual(descriptorCount, registry.Count);
        Assert.AreEqual(descriptorCount, registry.GetSnapshot().Count);
        for (var index = 0; index < descriptorCount; index++)
        {
            Assert.IsTrue(registry.TryGet(new MetricId($"parallel.metric.{index}"), out _));
        }
    }

    private static MetricDescriptor CreateDescriptor(string id, string? displayName = null) => new(
        new MetricId(id),
        displayName ?? id,
        "test.metric",
        MetricValueKind.Double);
}
