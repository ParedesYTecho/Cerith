#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System;
using System.Linq;
using Windows.Graphics;

namespace Cerith.WidgetHost;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
}
