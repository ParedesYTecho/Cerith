#nullable enable

using System;
using System.IO;
using System.Text.Json;

namespace Cerith.WidgetHost;

internal sealed record WindowState(int X, int Y, int Width, int Height, bool Compact);

internal static class WindowStateStore
{
    private const int MaximumDimension = 32768;

    internal static string DefaultPath => Path.Combine(Path.GetDirectoryName(MetricCompositionStore.DefaultPath)!, "window.json");

    internal static string Serialize(WindowState state) => JsonSerializer.Serialize(state);

    internal static WindowState? Parse(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<WindowState>(json);
            return state is { Width: > 0 and <= MaximumDimension, Height: > 0 and <= MaximumDimension } ? state : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool FitsWithin(WindowState state, int minimumWidth, int minimumHeight, int workWidth, int workHeight) =>
        state.Width >= minimumWidth && state.Height >= minimumHeight && state.Width <= workWidth && state.Height <= workHeight;

    internal static WindowState? Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void Save(string path, WindowState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(state));
    }
}
