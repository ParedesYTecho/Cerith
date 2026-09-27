#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Cerith.WidgetHost;

public sealed partial class MainWindow : Window
{
    private static readonly MetricId CpuUsageId = new("cpu.total.usage");
    private readonly CancellationTokenSource _stop = new();
    private Task? _readTask;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Cerith";
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        if (!_closing) _readTask = ReadAsync(_stop.Token);
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeMetricClient(MetricIpcProtocol.LocalPipeName);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(CpuUsageId, connect.Token);
            }

            await foreach (var snapshot in client.ReadSnapshotsAsync(cancellationToken))
            {
                if (snapshot.MetricId != CpuUsageId) throw new InvalidDataException("Unexpected metric ID.");
                CounterText.Text = snapshot.State switch
                {
                    MetricState.Fresh when snapshot.Value?.Kind == MetricValueKind.Double =>
                        $"cpu.total.usage: {snapshot.Value.Value.AsDouble().ToString("F1", CultureInfo.InvariantCulture)} %",
                    MetricState.Stale when snapshot.Value?.Kind == MetricValueKind.Double =>
                        $"cpu.total.usage: {snapshot.Value.Value.AsDouble().ToString("F1", CultureInfo.InvariantCulture)} % (stale)",
                    _ => "cpu.total.usage: unavailable"
                };
            }

            if (!_closing) CounterText.Text = "cpu.total.usage: unavailable";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            if (!_closing) CounterText.Text = "cpu.total.usage: unavailable";
        }
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
