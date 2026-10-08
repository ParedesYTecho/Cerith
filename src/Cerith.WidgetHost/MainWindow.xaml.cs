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
    private static readonly MetricRangeSpec[] Bars =
    [
        new(Metrics[0], 0, 100),
        new(Metrics[3], 0, 100)
    ];
    private static readonly MetricRangeSpec[] Rings = [new(Metrics[0], 0, 100)];

    private readonly MetricSubscription[] _subscriptions;
    private readonly CancellationTokenSource _stop = new();
    private Task? _readTask;
    private bool _closing;
    private double _scale;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Cerith";
        _subscriptions = MetricSubscription.ForDistinctMetrics(
            Metrics.Select(spec => spec.MetricId)
                .Concat(Bars.Select(bar => bar.Presentation.MetricId))
                .Concat(Rings.Select(ring => ring.Presentation.MetricId)),
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
            foreach (var ringSpec in Rings.Where(ring => ring.Presentation.MetricId == spec.MetricId))
            {
                var ring = new MetricRing(ringSpec);
                MetricsPanel.Children.Add(ring);
                subscription.Updated += ring.Apply;
                ring.Apply(subscription);
            }
        }
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        UpdateMinimumForScale();
        if (Root.XamlRoot is { } xamlRoot)
            xamlRoot.Changed += OnXamlRootChanged;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(420d * _scale),
            (int)Math.Ceiling(620d * _scale)));
        if (!_closing)
            _readTask = Task.WhenAll(_subscriptions.Select(subscription => subscription.RunAsync(_stop.Token)));
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!_closing && sender.RasterizationScale != _scale)
            UpdateMinimumForScale();
    }

    private void UpdateMinimumForScale()
    {
        _scale = Root.XamlRoot?.RasterizationScale ?? 1d;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(360d * _scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(340d * _scale);
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        if (Root.XamlRoot is { } xamlRoot)
            xamlRoot.Changed -= OnXamlRootChanged;
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
