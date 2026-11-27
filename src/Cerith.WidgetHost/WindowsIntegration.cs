#nullable enable

using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Cerith.WidgetHost;

/// <summary>Per-user start with Windows through the Run key. Off until the user turns it on.</summary>
[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cerith";

    internal static bool IsEnabled(string keyPath = RunKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName) is string;
    }

    internal static void Set(bool enabled, string executablePath, string keyPath = RunKey)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\"");
        else key.DeleteValue(ValueName, false);
    }
}

/// <summary>Whether the official PresentMon service, which gaming metrics need, is installed.</summary>
[SupportedOSPlatform("windows")]
internal static class PresentMonInstallation
{
    internal static bool IsInstalled(params string[] serviceNames)
    {
        if (serviceNames.Length == 0) serviceNames = ["PresentMonSharedService", "PresentMonService"];
        return serviceNames.Any(name =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key is not null;
        });
    }
}
