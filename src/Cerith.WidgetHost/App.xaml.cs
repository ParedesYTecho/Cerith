#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Cerith.Core;
using Windows.Graphics;

namespace Cerith.WidgetHost;

public partial class App : Application
{
    private MainWindow? _window;
    private SingleInstance? _instance;

    public App()
    {
        InitializeComponent();
        if (!WidgetThemeArguments.TryParse(Environment.GetCommandLineArgs().Skip(1), out var theme))
        {
            Debug.WriteLine("Invalid or repeated --theme option; using the system theme.");
            return;
        }
        if (theme == WidgetThemePreference.Light)
            RequestedTheme = ApplicationTheme.Light;
        else if (theme == WidgetThemePreference.Dark)
            RequestedTheme = ApplicationTheme.Dark;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = SingleInstance.TryAcquire(SingleInstance.AppName,
            () => _window?.DispatcherQueue.TryEnqueue(() => _window.BringForward()));
        if (_instance is null)
        {
            // The user just launched this process, so it may hand its foreground right to the running one.
            AllowSetForegroundWindow(-1);
            SingleInstance.Signal(SingleInstance.AppName);
            Exit();
            return;
        }
        var secondaryOnly = Environment.GetCommandLineArgs().Skip(1).Contains("--secondary-display", StringComparer.Ordinal);
        DisplayArea? display = null;
        if (secondaryOnly)
        {
            var displays = DisplayArea.FindAll();
            for (var index = 0; index < displays.Count; index++)
                if (!displays[index].IsPrimary)
                {
                    display = displays[index];
                    break;
                }
        }
        if (secondaryOnly && display is null)
        {
            Exit();
            return;
        }
        _window = new MainWindow(display);
        if (display is not null)
            _window.AppWindow.MoveAndResize(new RectInt32(display.WorkArea.X, display.WorkArea.Y,
                Math.Min(_window.AppWindow.Size.Width, display.WorkArea.Width),
                Math.Min(_window.AppWindow.Size.Height, display.WorkArea.Height)));
        _window.Activate();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
