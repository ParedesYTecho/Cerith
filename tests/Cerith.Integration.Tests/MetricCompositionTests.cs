#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class MetricCompositionTests
{
    [TestMethod]
    public void OverviewKeepsExactCaseOrderAndFirstConfiguredFormatWithoutChangingSavedRows()
    {
        var composition = new MetricComposition([
            new(CompositionKind.Bar, "CPU", "0.000", 0, 100),
            new(CompositionKind.Text, "ram", "0.0"),
            new(CompositionKind.Ring, "CPU", "0", 0, 100),
            new(CompositionKind.Text, "cpu", "0.00")]);
        string saved = composition.Serialize();
        var overview = composition.OverviewRows();
        CollectionAssert.AreEqual(new[] { "CPU", "ram", "cpu" }, overview.Select(row => row.Id).ToArray());
        Assert.AreEqual("0.000", overview[0].Format);
        Assert.AreEqual(saved, composition.Serialize());
        Assert.AreEqual(4, MetricComposition.Default.OverviewRows().Length);
    }

    [TestMethod]
    public void OverviewPagesUseMeasuredHeightsAndRetainEveryRowInOrder()
    {
        double[] heights = [40, 60, 100, 20, 80];
        var pages = MetricComposition.OverviewPages(heights, 100);
        CollectionAssert.AreEqual(new[] { (0, 2, true), (2, 1, true), (3, 2, true) }, pages.ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, heights.Length).ToArray(),
            pages.SelectMany(page => Enumerable.Range(page.Start, page.Count)).ToArray());
        var four = MetricComposition.OverviewPages([70, 70, 70, 70], 280);
        CollectionAssert.AreEqual(new[] { (0, 4, true) }, four.ToArray());
        CollectionAssert.AreEqual(new[] { (0, 3, true), (3, 1, true) },
            MetricComposition.OverviewPages([70, 70, 70, 70], 279.5).ToArray());
        CollectionAssert.AreEqual(new[] { (0, 4, true), (4, 1, true) },
            MetricComposition.OverviewPages([1, 1, 1, 1, 1], 100).ToArray());
    }

    [TestMethod]
    public void OversizedRowsRemainExplicitPagesAndRecoverWhenSpaceIncreases()
    {
        CollectionAssert.AreEqual(new[] { (0, 1, true), (1, 1, false), (2, 1, true) },
            MetricComposition.OverviewPages([40, 201, 40], 200).ToArray());
        CollectionAssert.AreEqual(new[] { (0, 3, true) },
            MetricComposition.OverviewPages([40, 201, 40], 281).ToArray());
        CollectionAssert.AreEqual(new[] { (0, 1, false), (1, 1, false) },
            MetricComposition.OverviewPages([70, 70], 0).ToArray());
        Assert.IsTrue(MetricComposition.OverviewPages([], 100).IsEmpty);
        foreach (double invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MetricComposition.OverviewPages([invalid], 100));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MetricComposition.OverviewPages([70], invalid));
        }
    }

    [TestMethod]
    public void ReconfigurationAndChangingHeightsProduceFreshPagesWithoutMutatingOldSelection()
    {
        var original = MetricComposition.Default.OverviewRows();
        var oldPages = MetricComposition.OverviewPages([70, 70, 70, 70], 280);
        var replacement = new MetricComposition([
            new(CompositionKind.Ring, "ram.usage", "0.00", 0, 100),
            new(CompositionKind.Text, "ram.usage", "0"),
            new(CompositionKind.Text, "RAM.usage", "0.000")]).OverviewRows();
        CollectionAssert.AreEqual(new[] { "ram.usage", "RAM.usage" }, replacement.Select(row => row.Id).ToArray());
        Assert.AreEqual("0.00", replacement[0].Format);
        CollectionAssert.AreEqual(new[] { (0, 2, true) }, MetricComposition.OverviewPages([70, 70], 280).ToArray());
        CollectionAssert.AreEqual(new[] { (0, 1, true), (1, 1, true) },
            MetricComposition.OverviewPages([220, 70], 280).ToArray());
        Assert.AreEqual(4, original.Length);
        CollectionAssert.AreEqual(new[] { (0, 4, true) }, oldPages.ToArray());
    }

    [TestMethod]
    public void DefaultRoundTripPreservesSevenOrderedRows()
    {
        var original = MetricComposition.Default;
        var copy = MetricComposition.Parse(Encoding.UTF8.GetBytes(original.Serialize()));
        Assert.AreEqual(7, copy.Rows.Length);
        CollectionAssert.AreEqual(original.Rows.ToArray(), copy.Rows.ToArray());
        Assert.AreEqual(CompositionKind.Ring, copy.Rows[2].Kind);
    }

    [TestMethod]
    public void UnicodeAndCaseSurviveRoundTripExactly()
    {
        var id = "Mixed.Emoji.😀.e\u0301";
        var copy = MetricComposition.Parse(Encoding.UTF8.GetBytes(new MetricComposition([
            new(CompositionKind.Text, id, "0.000")]).Serialize()));
        Assert.AreEqual(id, copy.Rows[0].Id);
        Assert.AreNotEqual(id.ToLowerInvariant(), copy.Rows[0].Id);
    }

    [TestMethod]
    public void ConfiguredSparklineRoundTripsInVersionOneWithoutChangingDefaults()
    {
        var original = new MetricComposition([
            new(CompositionKind.Text, "CPU.Mixed.é", "0"),
            new(CompositionKind.Sparkline, "CPU.Mixed.é", "0.000", -12.5, 125.25),
            new(CompositionKind.Ring, "ram.usage", "0.0", 0, 100)]);
        var json = original.Serialize();
        StringAssert.Contains(json, "\"version\":1");
        CollectionAssert.AreEqual(original.Rows.ToArray(), MetricComposition.Parse(Encoding.UTF8.GetBytes(json)).Rows.ToArray());
        Assert.AreEqual(7, MetricComposition.Default.Rows.Length);
        Assert.IsFalse(MetricComposition.Default.Rows.Any(row => row.Kind == CompositionKind.Sparkline));
    }

    [TestMethod]
    public void SparklineRequiresFiniteIncreasingRangeAndStrictDefinitionShape()
    {
        (double? Min, double? Max)[] invalidRanges = [
            (null, 100), (0, null), (5, 5), (6, 5),
            (double.NaN, 100), (0, double.PositiveInfinity),
            (double.NegativeInfinity, 100), (double.MinValue, double.MaxValue)];
        foreach (var (min, max) in invalidRanges)
            Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([
                new(CompositionKind.Sparkline, "CPU.Mixed", "0.0", min, max)]));
        string[] invalid = [
            """{"version":1,"rows":[{"kind":"Sparkline","id":"CPU.Mixed","format":"0.0"}]}""",
            """{"version":1,"rows":[{"kind":"Sparkline","id":"CPU.Mixed","format":"0.0","min":5,"max":5}]}""",
            """{"version":1,"rows":[{"kind":"sparkline","id":"CPU.Mixed","format":"0.0","min":0,"max":100}]}""",
            """{"version":1,"rows":[{"kind":"3","id":"CPU.Mixed","format":"0.0","min":0,"max":100}]}""",
            """{"version":1,"rows":[{"kind":"Sparkline","id":"CPU.Mixed","format":"0.0","min":0,"max":100,"samples":[]}]}"""];
        foreach (var json in invalid)
            Assert.ThrowsExactly<InvalidDataException>(() => MetricComposition.Parse(Encoding.UTF8.GetBytes(json)), json);
    }

    [TestMethod]
    public void RejectInvalidIdentifiersFormatsAndRanges()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Text, "x\uD800", "0")]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Text, "x\n", "0")]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Text, "x", "0.0000")]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Text, "x", "0", 0, 1)]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Bar, "x", "0", 5, 5)]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Ring, "x", "0", double.MinValue, double.MaxValue)]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition(Enumerable.Repeat(new CompositionRow(CompositionKind.Text, "x", "0"), 17)));
    }

    [TestMethod]
    public void RejectMalformedAndAmbiguousJson()
    {
        string[] invalid = [
            "{}",
            "{\"version\":1,\"rows\":[]}",
            "{\"version\":1,\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":\"x\",\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"0\",\"id\":\"x\",\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":\"x\",\"format\":\"0\",\"min\":0}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Bar\",\"id\":\"x\",\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":\"x\",\"format\":\"0\",\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":null,\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":\"x\",\"format\":\"0\"}],\"secret\":true}"
        ];
        foreach (var json in invalid)
            Assert.ThrowsExactly<InvalidDataException>(() => MetricComposition.Parse(Encoding.UTF8.GetBytes(json)), json);
        Assert.ThrowsExactly<NotSupportedException>(() => MetricComposition.Parse(Encoding.UTF8.GetBytes("{\"version\":2,\"rows\":[{\"kind\":\"Text\",\"id\":\"x\",\"format\":\"0\"}]}")));
        Assert.ThrowsExactly<InvalidDataException>(() => MetricComposition.Parse(new byte[65537]));
    }

    [TestMethod]
    public void AcceptBoundaryRowCountsFormatsAndExactUtf8ByteLimit()
    {
        foreach (var count in new[] { 1, 16 })
        {
            var original = new MetricComposition(Enumerable.Range(0, count).Select(index =>
                new CompositionRow((CompositionKind)(index % 3), new string('é', 128),
                    new[] { "0", "0.0", "0.00", "0.000" }[index % 4],
                    index % 3 == 0 ? null : -1.5, index % 3 == 0 ? null : 1.5)));
            CollectionAssert.AreEqual(original.Rows.ToArray(), MetricComposition.Parse(Encoding.UTF8.GetBytes(original.Serialize())).Rows.ToArray());
        }
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Text, new string('é', 128) + "x", "0")]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Text, null!, "0")]));
        Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new((CompositionKind)42, "x", "0")]));
        foreach (var number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.ThrowsExactly<InvalidDataException>(() => new MetricComposition([new(CompositionKind.Bar, "x", "0", number, 100)]));
    }

    [TestMethod]
    public void RejectWrongJsonTypesInvalidUnicodeAndExcessiveDepth()
    {
        string[] invalid =
        [
            "{",
            "{\"version\":1,\"rows\":null}",
            "{\"version\":1,\"rows\":[null]}",
            "{\"version\":1,\"rows\":[{\"kind\":0,\"id\":\"x\",\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"text\",\"id\":\"x\",\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":\"\\uD800\",\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":3,\"format\":\"0\"}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":\"x\",\"format\":null}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Bar\",\"id\":\"x\",\"format\":\"0\",\"min\":null,\"max\":100}]}",
            "{\"version\":1,\"rows\":[{\"kind\":\"Ring\",\"id\":\"x\",\"format\":\"0\",\"min\":0,\"max\":1e999}]}",
            "{\"version\":1,\"rows\":[[[[[[[]]]]]]]}"
        ];
        foreach (var json in invalid)
            Assert.ThrowsExactly<InvalidDataException>(() => MetricComposition.Parse(Encoding.UTF8.GetBytes(json)), json);
        byte[] prefix = Encoding.UTF8.GetBytes("{\"version\":1,\"rows\":[{\"kind\":\"Text\",\"id\":\"");
        byte[] suffix = Encoding.UTF8.GetBytes("\",\"format\":\"0\"}]}");
        Assert.ThrowsExactly<InvalidDataException>(() => MetricComposition.Parse([.. prefix, 0xff, .. suffix]));
    }
}
