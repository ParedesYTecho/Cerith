#nullable enable

using System;
using Cerith.WidgetHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Cerith.Integration.Tests;

[TestClass]
public sealed class WindowsIntegrationTests
{
    [TestMethod]
    public void StartWithWindowsWritesTheQuotedPathAndRemovesItAgain()
    {
        // A throwaway key: the real Run key is never touched by tests.
        var key = $@"Software\Cerith.Test.{Guid.NewGuid():N}";
        try
        {
            Assert.IsFalse(StartupRegistration.IsEnabled(key));
            StartupRegistration.Set(true, @"C:\a b\Cerith.WidgetHost.exe", key);
            Assert.IsTrue(StartupRegistration.IsEnabled(key));
            using (var written = Registry.CurrentUser.OpenSubKey(key)!)
                Assert.AreEqual("\"C:\\a b\\Cerith.WidgetHost.exe\"", written.GetValue("Cerith"));
            StartupRegistration.Set(false, @"C:\a b\Cerith.WidgetHost.exe", key);
            Assert.IsFalse(StartupRegistration.IsEnabled(key));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }

    [TestMethod]
    public void AServiceThatDoesNotExistIsNotReportedAsInstalled()
    {
        Assert.IsFalse(PresentMonInstallation.IsInstalled($"Cerith.Missing.{Guid.NewGuid():N}"));
        Assert.IsTrue(PresentMonInstallation.IsInstalled("EventLog"));
    }
}
