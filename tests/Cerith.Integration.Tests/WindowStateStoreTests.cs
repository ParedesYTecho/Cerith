#nullable enable

using System.IO;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class WindowStateStoreTests
{
    [TestMethod]
    public void SavedStateRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "window.json");
        try
        {
            var state = new WindowState(-1200, 40, 420, 620, true);
            WindowStateStore.Save(path, state);
            Assert.AreEqual(state, WindowStateStore.Load(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public void MissingFileMeansNoState() =>
        Assert.IsNull(WindowStateStore.Load(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "window.json")));

    [TestMethod]
    public void CorruptOrIncompleteJsonMeansNoState()
    {
        Assert.IsNull(WindowStateStore.Parse("{ not json"));
        Assert.IsNull(WindowStateStore.Parse(""));
        Assert.IsNull(WindowStateStore.Parse("""{"X":1,"Y":2}"""));
        Assert.IsNull(WindowStateStore.Parse("""{"X":1,"Y":2,"Width":"wide","Height":5,"Compact":false}"""));
    }

    [TestMethod]
    public void NonPositiveOrAbsurdSizesAreRejected()
    {
        Assert.IsNull(WindowStateStore.Parse(WindowStateStore.Serialize(new(0, 0, 0, 620, false))));
        Assert.IsNull(WindowStateStore.Parse(WindowStateStore.Serialize(new(0, 0, 420, -5, false))));
        Assert.IsNull(WindowStateStore.Parse(WindowStateStore.Serialize(new(0, 0, 1_000_000, 620, false))));
        Assert.IsNotNull(WindowStateStore.Parse(WindowStateStore.Serialize(new(0, 0, 420, 620, false))));
    }

    [TestMethod]
    public void SizeMustFitBetweenMinimumAndWorkArea()
    {
        Assert.IsTrue(WindowStateStore.FitsWithin(new(0, 0, 420, 620, false), 360, 340, 1920, 1040));
        Assert.IsFalse(WindowStateStore.FitsWithin(new(0, 0, 300, 620, false), 360, 340, 1920, 1040));
        Assert.IsFalse(WindowStateStore.FitsWithin(new(0, 0, 420, 200, false), 360, 340, 1920, 1040));
        Assert.IsFalse(WindowStateStore.FitsWithin(new(0, 0, 2500, 620, false), 360, 340, 1920, 1040));
        Assert.IsFalse(WindowStateStore.FitsWithin(new(0, 0, 420, 1200, false), 360, 340, 1920, 1040));
    }
}
