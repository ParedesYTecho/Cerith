#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Cerith.WidgetHost;

public sealed partial class MainWindow : Window
{
    private static readonly MetricId[] MetricIds =
    [
        new("cpu.total.usage"),
        new("ram.used.bytes"), new("ram.available.bytes"), new("ram.total.bytes"), new("ram.usage"),
        new("gpu.0.usage"), new("gpu.0.temperature.core"), new("gpu.0.clock.core"),
        new("gpu.0.vram.used.bytes"), new("gpu.0.vram.total.bytes"), new("gpu.0.fan.rpm"),
        new("storage.0.usage"), new("storage.0.total.bytes"), new("storage.0.free.bytes"),
        new("network.0.download.bytes_per_second"), new("network.0.upload.bytes_per_second"), new("network.0.usage"),
        new("game.fps.display"), new("game.frametime.display"), new("game.fps.display.p01")
    ];
    private readonly string[] _values = Enumerable.Repeat("Connecting…", MetricIds.Length).ToArray();
    private readonly CancellationTokenSource _stop = new();
    private Task? _readTask;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Cerith";
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        if (!_closing) _readTask = Task.WhenAll(Enumerable.Range(0, MetricIds.Length).Select(i => ReadAsync(i, _stop.Token)));
    }

    private async Task ReadAsync(int index, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var client = new NamedPipeMetricClient(MetricIpcProtocol.LocalPipeName);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(5));
                    await client.ConnectAsync(MetricIds[index], connect.Token);
                }

                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await foreach (var snapshot in client.ReadSnapshotsAsync(receiveTimeout.Token))
                {
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    if (snapshot.MetricId != MetricIds[index]) throw new InvalidDataException("Unexpected metric ID.");
                    _values[index] = Format(index, snapshot);
                    Render();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                Debug.WriteLine("Metric subscription interrupted; reconnecting.");
            }

            if (!_closing) { _values[index] = "unavailable"; Render(); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string Format(int index, MetricSnapshot snapshot)
    {
        if (snapshot.State == MetricState.Stopped) return "stopped";
        if (snapshot.State == MetricState.Error)
            return snapshot.ErrorCode is null ? "error" : $"error ({snapshot.ErrorCode})";
        if (snapshot.State is not (MetricState.Fresh or MetricState.Stale) || snapshot.Value is null)
            return "unavailable";
        var value = snapshot.Value.Value;
        var formatted = index switch
        {
            1 or 2 or 3 when value.Kind == MetricValueKind.Int64 =>
                (value.AsInt64() / 1_073_741_824d).ToString("F1", CultureInfo.InvariantCulture) + " GiB",
            8 or 9 or 12 or 13 when value.Kind == MetricValueKind.Double =>
                (value.AsDouble() / 1_073_741_824d).ToString("F1", CultureInfo.InvariantCulture) + " GiB",
            0 or 4 or 5 or 11 or 16 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F1", CultureInfo.InvariantCulture) + " %",
            14 or 15 when value.Kind == MetricValueKind.Double =>
                (value.AsDouble() / 1_000_000d).ToString("F2", CultureInfo.InvariantCulture) + " MB/s",
            6 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F1", CultureInfo.InvariantCulture) + " °C",
            7 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F0", CultureInfo.InvariantCulture) + " MHz",
            10 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F0", CultureInfo.InvariantCulture) + " RPM",
            17 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F1", CultureInfo.InvariantCulture) + " FPS",
            18 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F2", CultureInfo.InvariantCulture) + " ms",
            19 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F1", CultureInfo.InvariantCulture) + " FPS",
            _ => "unavailable"
        };
        return snapshot.State == MetricState.Stale && formatted != "unavailable" ? formatted + " (stale)" : formatted;
    }

    private void Render()
    {
        CounterText.Text = $"CPU    {_values[0]}\n" +
            $"RAM    {_values[1]} / {_values[3]} · {_values[4]}\n" +
            $"Available    {_values[2]}\n" +
            $"GPU 0  {_values[5]} · {_values[6]} · {_values[7]}\n" +
            $"VRAM   {_values[8]} / {_values[9]}\n" +
            $"Fan    {_values[10]}\n" +
            $"Disk 0 {_values[11]} · {_values[12]} total · {_values[13]} free\n" +
            $"Net 0  ↓ {_values[14]} · ↑ {_values[15]} · {_values[16]}\n" +
            $"Displayed FPS    {_values[17]}\n" +
            $"Displayed frame time    {_values[18]}\n" +
            $"Displayed FPS P1 (10 s)    {_values[19]}";
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        _stop.Cancel();
        try
        {
            if (_readTask is not null) await _readTask;
        }
        catch (Exception error) { Debug.WriteLine(error); }
        finally
        {
            _stop.Dispose();
            AppWindow.Closing -= OnClosing;
            Close();
        }
    }
}
