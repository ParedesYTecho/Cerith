#nullable enable

using System;
using System.Diagnostics;
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
        new(new MetricId("cpu.total.usage"), "0.0"),
        new(new MetricId("ram.usage"), "0.0"),
        new(new MetricId("game.fps.display"), "0.0"),
        new(new MetricId("ai.codex.quota.primary.remaining"), "0.0")
    ];
    private static readonly MetricBarSpec[] Bars =
    [
        new(Metrics[0], 0, 100),
        new(Metrics[3], 0, 100)
    ];

    private readonly MetricSubscription[] _subscriptions;
    private readonly CancellationTokenSource _stop = new();
    private Task? _readTask;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Cerith";
        _subscriptions = MetricSubscription.ForDistinctMetrics(
            Metrics.Select(spec => spec.MetricId).Concat(Bars.Select(bar => bar.Presentation.MetricId)),
            new MetricConnectionAttempt(), MetricIpcProtocol.LocalPipeName);
        foreach (var spec in Metrics)
        {
            var subscription = _subscriptions.Single(item => item.MetricId == spec.MetricId);
            var readout = new MetricReadout(spec);
            MetricsPanel.Children.Add(readout);
            subscription.Updated += readout.Apply;
            readout.Apply(subscription);
            foreach (var barSpec in Bars.Where(bar => bar.Presentation.MetricId == spec.MetricId))
            {
                var bar = new MetricBar(barSpec);
                MetricsPanel.Children.Add(bar);
                subscription.Updated += bar.Apply;
                bar.Apply(subscription);
            }
        }
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        ResizeForScale();
        if (!_closing)
            _readTask = Task.WhenAll(_subscriptions.Select(subscription => subscription.RunAsync(_stop.Token)));
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
            (int)Math.Ceiling(620d * scale)));
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
            foreach (var subscription in _subscriptions) subscription.DetachConsumers();
            _stop.Dispose();
            AppWindow.Closing -= OnClosing;
            Close();
        }
    }
}
