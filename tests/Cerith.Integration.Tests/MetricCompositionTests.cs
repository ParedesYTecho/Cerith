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
