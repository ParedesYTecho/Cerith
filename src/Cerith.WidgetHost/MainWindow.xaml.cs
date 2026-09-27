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
        new("gpu.0.vram.used.bytes"), new("gpu.0.vram.total.bytes"), new("gpu.0.fan.rpm")
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
        try
        {
            await using var client = new NamedPipeMetricClient(MetricIpcProtocol.LocalPipeName);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(MetricIds[index], connect.Token);
            }

            await foreach (var snapshot in client.ReadSnapshotsAsync(cancellationToken))
            {
                if (snapshot.MetricId != MetricIds[index]) throw new InvalidDataException("Unexpected metric ID.");
                _values[index] = Format(index, snapshot);
                Render();
            }

            if (!_closing) { _values[index] = "unavailable"; Render(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            if (!_closing) { _values[index] = "unavailable"; Render(); }
        }
    }

    private static string Format(int index, MetricSnapshot snapshot)
    {
        if (snapshot.State is not (MetricState.Fresh or MetricState.Stale) || snapshot.Value is null)
            return "unavailable";
        var value = snapshot.Value.Value;
        var formatted = index switch
        {
            1 or 2 or 3 when value.Kind == MetricValueKind.Int64 =>
                (value.AsInt64() / 1_073_741_824d).ToString("F1", CultureInfo.InvariantCulture) + " GiB",
            8 or 9 when value.Kind == MetricValueKind.Double =>
                (value.AsDouble() / 1_073_741_824d).ToString("F1", CultureInfo.InvariantCulture) + " GiB",
            0 or 4 or 5 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F1", CultureInfo.InvariantCulture) + " %",
            6 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F1", CultureInfo.InvariantCulture) + " °C",
            7 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F0", CultureInfo.InvariantCulture) + " MHz",
            10 when value.Kind == MetricValueKind.Double =>
                value.AsDouble().ToString("F0", CultureInfo.InvariantCulture) + " RPM",
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
            $"Fan    {_values[10]}";
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
