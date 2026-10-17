#nullable enable

using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class WidgetThemeArgumentsTests
{
    [TestMethod]
    public void MissingOrSystemThemeKeepsWindowsPreference()
    {
        Assert.IsTrue(WidgetThemeArguments.TryParse([], out var absent));
        Assert.AreEqual(WidgetThemePreference.System, absent);
        Assert.IsTrue(WidgetThemeArguments.TryParse(["--secondary-display", "--theme=system"], out var explicitSystem));
        Assert.AreEqual(WidgetThemePreference.System, explicitSystem);
    }

    [TestMethod]
    public void LightAndDarkOverridesAreCaseInsensitiveAndIgnoreOtherOptions()
    {
        Assert.IsTrue(WidgetThemeArguments.TryParse(["--secondary-display", "--theme=LIGHT"], out var light));
        Assert.AreEqual(WidgetThemePreference.Light, light);
        Assert.IsTrue(WidgetThemeArguments.TryParse(["--theme=Dark"], out var dark));
        Assert.AreEqual(WidgetThemePreference.Dark, dark);
    }

    [TestMethod]
    public void MalformedOrRepeatedThemeOptionsAreRejectedWithoutChangingSystemFallback()
    {
        string[][] invalid =
        [
            ["--theme"],
            ["--theme="],
            ["--theme=contrast"],
            ["--theme=light", "--theme=dark"],
            ["--theme=system", "--theme=system"],
            ["--theme=dark", "--theme"]
        ];
        foreach (var arguments in invalid)
        {
            Assert.IsFalse(WidgetThemeArguments.TryParse(arguments, out var preference));
            Assert.AreEqual(WidgetThemePreference.System, preference);
        }
    }
}
