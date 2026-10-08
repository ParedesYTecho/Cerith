#nullable enable

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Cerith.WidgetHost;

public sealed partial class MainWindow : Window
{
    private readonly MetricCompositionStore _store = new(MetricCompositionStore.DefaultPath);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _compositionStop;
    private MetricSubscription[] _subscriptions = [];
    private Task _readTask = Task.CompletedTask;
    private Task _operation = Task.CompletedTask;
    private MetricComposition _active = MetricComposition.Default;
    private CompositionLoad _baseline = new(CompositionLoadState.Missing, null, null);
    private bool _busy;
    private CompositionDialog? _dialog;
    private bool _closing;
    private double _scale;
    private readonly DisplayArea? _startupDisplay;
    private sealed record PreparedComposition(MetricComposition Composition, MetricSubscription[] Subscriptions,
        List<(UIElement Element, MetricSubscription Subscription, Action<MetricSubscription> Apply)> Elements);

    public MainWindow(DisplayArea? startupDisplay = null)
    {
        _startupDisplay = startupDisplay;
        InitializeComponent();
        Title = "Cerith";
        Install(Prepare(_active));
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
    }

    private static PreparedComposition Prepare(MetricComposition composition)
    {
        var subscriptions = MetricSubscription.ForDistinctMetrics(composition.Rows.Select(row => new MetricId(row.Id)),
            new MetricConnectionAttempt(), MetricIpcProtocol.LocalPipeName);
        var elements = new List<(UIElement Element, MetricSubscription Subscription, Action<MetricSubscription> Apply)>();
        foreach (var row in composition.Rows)
        {
            var spec = new MetricPresentationSpec(new MetricId(row.Id), row.Format);
            var subscription = subscriptions.Single(item => item.MetricId == spec.MetricId);
            UIElement element;
            Action<MetricSubscription> apply;
            if (row.Kind == CompositionKind.Text)
            {
                var readout = new MetricReadout(spec);
                element = readout;
                apply = readout.Apply;
            }
            else
            {
                var range = new MetricRangeSpec(spec, row.Minimum!.Value, row.Maximum!.Value);
                if (row.Kind == CompositionKind.Bar)
                {
                    var bar = new MetricBar(range);
                    element = bar;
                    apply = bar.Apply;
                }
                else if (row.Kind == CompositionKind.Ring)
                {
                    var ring = new MetricRing(range);
                    element = ring;
                    apply = ring.Apply;
                }
                else if (row.Kind == CompositionKind.Sparkline)
                {
                    var sparkline = new MetricSparkline(range);
                    element = sparkline;
                    apply = sparkline.Apply;
                }
                else
                    throw new InvalidOperationException("Unknown composition renderer.");
            }
            elements.Add((element, subscription, apply));
        }
        return new PreparedComposition(composition, subscriptions, elements);
    }

    private void Install(PreparedComposition prepared)
    {
        if (_closing) return;
        _subscriptions = prepared.Subscriptions;
        _compositionStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        MetricsPanel.Children.Clear();
        foreach (var (element, subscription, apply) in prepared.Elements)
        {
            MetricsPanel.Children.Add(element);
            subscription.Updated += apply;
            apply(subscription);
        }
        _active = prepared.Composition;
    }

    private void StartSubscriptions()
    {
        if (!_closing && _compositionStop is { } stop)
            _readTask = Task.WhenAll(_subscriptions.Select(s => s.RunAsync(stop.Token)));
    }

    private async Task StopSubscriptions()
    {
        var stop = _compositionStop;
        _compositionStop = null;
        stop?.Cancel();
        try { await _readTask; }
        catch (Exception error) { Debug.WriteLine(error); }
        foreach (var subscription in _subscriptions) subscription.DetachConsumers();
        stop?.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        if (_closing) return;
        UpdateMinimumForScale();
        if (Root.XamlRoot is { } xamlRoot)
            xamlRoot.Changed += OnXamlRootChanged;
        AppWindow.Resize(new SizeInt32(
            Math.Min((int)Math.Ceiling(420d * _scale), _startupDisplay?.WorkArea.Width ?? int.MaxValue),
            Math.Min((int)Math.Ceiling(620d * _scale), _startupDisplay?.WorkArea.Height ?? int.MaxValue)));
        _operation = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _busy = true;
        try
        {
            var load = await _store.LoadAsync(_lifetime.Token);
            if (_closing) return;
            _baseline = load;
            if (load.State == CompositionLoadState.Valid)
            {
                var prepared = Prepare(load.Composition!);
                await StopSubscriptions();
                if (_closing) return;
                Install(prepared);
            }
            else if (load.State != CompositionLoadState.Missing)
                Warn("Existing layout cannot be loaded. Its file is preserved; saving disabled.");
            StartSubscriptions();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            if (!_closing)
            {
                if (_baseline.State == CompositionLoadState.Valid)
                    Warn("Saved layout could not be applied. Its file is preserved; active metrics may be incomplete.");
                else
                {
                    _baseline = new(CompositionLoadState.Unreadable, null, null);
                    Warn("Layout load failed; saving disabled.");
                }
                StartSubscriptions();
            }
        }
        finally { _busy = false; ConfigureButton.IsEnabled = !_closing && _baseline.State is (CompositionLoadState.Missing or CompositionLoadState.Valid); }
    }

    private void Warn(string warning)
    {
        StorageWarning.Text = warning;
        StorageWarning.Visibility = Visibility.Visible;
    }

    private void OnConfigure(object sender, RoutedEventArgs args)
    {
        if (_closing || _busy || _baseline.State is not (CompositionLoadState.Missing or CompositionLoadState.Valid)) return;
        _operation = ConfigureAsync();
    }

    private async Task ConfigureAsync()
    {
        _busy = true;
        ConfigureButton.IsEnabled = false;
        try
        {
            var dialog = new CompositionDialog(_active) { XamlRoot = Root.XamlRoot };
            _dialog = dialog;
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); }
            finally { _dialog = null; }
            if (_closing || result != ContentDialogResult.Primary || dialog.Result is not { } draft) return;
            _ = draft.Serialize();
            var prepared = Prepare(draft);
            try { await _store.SaveAsync(draft, _baseline, _lifetime.Token); }
            catch (OperationCanceledException) when (_closing) { return; }
            catch (Exception error)
            {
                Debug.WriteLine(error);
                if (!_closing) Warn("Save failed; active metrics unchanged. Check layout storage before retrying.");
                return;
            }
            if (_closing) return;
            _baseline = new(CompositionLoadState.Valid, draft, System.Text.Encoding.UTF8.GetBytes(draft.Serialize()));
            try
            {
                await StopSubscriptions();
                if (_closing) return;
                Install(prepared);
                StartSubscriptions();
                StorageWarning.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) { Debug.WriteLine(error); Warn("Saved; application failed. Restart to load the saved layout."); }
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            if (!_closing) Warn("Configuration could not be opened or validated.");
        }
        finally { _busy = false; ConfigureButton.IsEnabled = !_closing && _baseline.State is (CompositionLoadState.Missing or CompositionLoadState.Valid); }
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
            presenter.PreferredMinimumWidth = Math.Min((int)Math.Ceiling(360d * _scale), _startupDisplay?.WorkArea.Width ?? int.MaxValue);
            presenter.PreferredMinimumHeight = Math.Min((int)Math.Ceiling(340d * _scale), _startupDisplay?.WorkArea.Height ?? int.MaxValue);
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        Root.Loaded -= OnLoaded;
        if (Root.XamlRoot is { } xamlRoot)
            xamlRoot.Changed -= OnXamlRootChanged;
        _lifetime.Cancel();
        _dialog?.Hide();
        try
        {
            await _operation;
        }
        catch (Exception error) { Debug.WriteLine(error); }
        finally
        {
            await StopSubscriptions();
            _lifetime.Dispose();
            AppWindow.Closing -= OnClosing;
            Close();
        }
    }
}
