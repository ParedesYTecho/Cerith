#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Cerith.WidgetHost;

public sealed partial class MainWindow : Window
{
    private static readonly MetricPresentationSpec[] Metrics =
    [
        new(new MetricId("cpu.total.usage"), "CPU total", "0.0", "%"),
        new(new MetricId("game.fps.display"), "Displayed FPS", "0.0", "FPS"),
        new(new MetricId("ai.codex.quota.primary.remaining"), "Codex primary quota", "0.0", "%")
    ];

    private readonly MetricReadout[] _readouts;
    private readonly CancellationTokenSource _stop = new();
    // ponytail: one local availability attempt; split only if a second service endpoint is added.
    private readonly object _connectionAttemptLock = new();
    private TaskCompletionSource<bool>? _connectionAttempt;
    private bool _serviceAvailable;
    private Task? _readTask;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Cerith";
        _readouts = Metrics.Select(spec => new MetricReadout(spec)).ToArray();
        foreach (var readout in _readouts) MetricsPanel.Children.Add(readout);
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        ResizeForScale();
        if (!_closing)
            _readTask = Task.WhenAll(Enumerable.Range(0, Metrics.Length).Select(index => ReadAsync(index, _stop.Token)));
    }

    private void ResizeForScale()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1d;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(360d * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(340d * scale);
        }
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(420d * scale),
            (int)Math.Ceiling(340d * scale)));
    }

    private async Task ReadAsync(int index, CancellationToken cancellationToken)
    {
        var spec = Metrics[index];
        var readout = _readouts[index];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var client = new NamedPipeMetricClient(MetricIpcProtocol.LocalPipeName);
                await ConnectCoordinatedAsync(client, spec.MetricId, cancellationToken);

                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await foreach (var snapshot in client.ReadSnapshotsAsync(receiveTimeout.Token))
                {
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    if (snapshot.MetricId != spec.MetricId) throw new InvalidDataException("Unexpected metric ID.");
                    readout.Apply(snapshot);
                }
                MarkConnectionUnavailable();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                MarkConnectionUnavailable();
                Debug.WriteLine($"Metric subscription interrupted for {spec.MetricId}; reconnecting.");
            }

            if (!_closing) readout.ShowUnavailable();
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectCoordinatedAsync(NamedPipeMetricClient client, MetricId metricId, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? attempt;
        var ownsAttempt = false;
        lock (_connectionAttemptLock)
        {
            if (_serviceAvailable)
            {
                attempt = null;
            }
            else
            {
                attempt = _connectionAttempt;
                if (attempt is null)
                {
                    attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _connectionAttempt = attempt;
                    ownsAttempt = true;
                }
            }
        }

        if (attempt is null)
        {
            await ConnectWithTimeoutAsync(client, metricId, cancellationToken);
            MarkConnectionAvailable();
            return;
        }

        if (!ownsAttempt)
        {
            if (!await attempt.Task.WaitAsync(cancellationToken))
                throw new IOException("TelemetryService connection attempt failed.");

            await ConnectWithTimeoutAsync(client, metricId, cancellationToken);
            MarkConnectionAvailable();
            return;
        }

        try
        {
            await ConnectWithTimeoutAsync(client, metricId, cancellationToken);
            MarkConnectionAvailable();
        }
        catch
        {
            lock (_connectionAttemptLock)
            {
                if (ReferenceEquals(_connectionAttempt, attempt))
                {
                    _connectionAttempt = null;
                    _serviceAvailable = false;
                }
            }
            attempt.TrySetResult(false);
            throw;
        }
    }

    private static async Task ConnectWithTimeoutAsync(NamedPipeMetricClient client, MetricId metricId, CancellationToken cancellationToken)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(metricId, connect.Token);
    }

    private void MarkConnectionAvailable()
    {
        TaskCompletionSource<bool>? attempt;
        lock (_connectionAttemptLock)
        {
            _serviceAvailable = true;
            attempt = _connectionAttempt;
            _connectionAttempt = null;
        }
        attempt?.TrySetResult(true);
    }

    private void MarkConnectionUnavailable()
    {
        lock (_connectionAttemptLock)
            _serviceAvailable = false;
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
