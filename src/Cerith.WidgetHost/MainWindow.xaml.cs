#nullable enable

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
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
    private readonly List<MetricReadout> _overview = [];
    private int _page;
    private bool _details;
    private bool _compact;
    private RectInt32? _normalBounds;
    private bool _overviewPending;
    private ImmutableArray<(int Start, int Count, bool Fits)> _pages = [];
    private sealed record PreparedComposition(MetricComposition Composition, MetricSubscription[] Subscriptions,
        List<(UIElement Element, MetricSubscription Subscription, Action<MetricSubscription> Apply)> Elements,
        List<(MetricReadout Element, MetricSubscription Subscription)> Overview,
        List<(MetricReadout Element, MetricSubscription Subscription)> Compact);

    public MainWindow(DisplayArea? startupDisplay = null)
    {
        _startupDisplay = startupDisplay;
        InitializeComponent();
        OverviewViewport.SizeChanged += (_, _) => ScheduleOverviewPage();
        OverviewNavigation.SizeChanged += (_, _) => ScheduleOverviewPage();
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
        var overview = composition.OverviewRows().Select(row =>
            (new MetricReadout(new MetricPresentationSpec(new MetricId(row.Id), row.Format)),
             subscriptions.Single(item => item.MetricId == new MetricId(row.Id)))).ToList();
        var compact = composition.OverviewRows().Take(2).Select(row =>
            (new MetricReadout(new MetricPresentationSpec(new MetricId(row.Id), row.Format)),
             subscriptions.Single(item => item.MetricId == new MetricId(row.Id)))).ToList();
        return new PreparedComposition(composition, subscriptions, elements, overview, compact);
    }

    private void Install(PreparedComposition prepared)
    {
        if (_closing) return;
        _subscriptions = prepared.Subscriptions;
        _compositionStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        MetricsPanel.Children.Clear();
        OverviewPanel.Children.Clear();
        CompactPanel.Children.Clear();
        _overview.Clear();
        _pages = [];
        _page = 0;
        foreach (var (element, subscription, apply) in prepared.Elements)
        {
            MetricsPanel.Children.Add(element);
            subscription.Updated += apply;
            apply(subscription);
        }
        foreach (var (readout, subscription) in prepared.Overview)
        {
            readout.PresentationChanged += ScheduleOverviewPage;
            readout.SizeChanged += (_, _) => ScheduleOverviewPage();
            subscription.Updated += readout.Apply;
            readout.Apply(subscription);
            _overview.Add(readout);
        }
        foreach (var (readout, subscription) in prepared.Compact)
        {
            CompactPanel.Children.Add(readout);
            subscription.Updated += readout.Apply;
            readout.Apply(subscription);
        }
        ScheduleOverviewPage();
        _active = prepared.Composition;
    }

    private void ScheduleOverviewPage()
    {
        if (_overviewPending || _closing || _details || _compact) return;
        _overviewPending = true;
        if (!DispatcherQueue.TryEnqueue(() => { _overviewPending = false; ShowOverviewPage(); }))
            _overviewPending = false;
    }

    private void ShowOverviewPage()
    {
        if (_closing || _details || _compact || OverviewViewport.ActualWidth <= 0) return;
        double width = OverviewViewport.ActualWidth;
        // The star row already excludes the navigation's actual arranged height.
        double availableHeight = Math.Max(0, OverviewViewport.ActualHeight);
        int anchor = _pages.IsEmpty ? 0 : _pages[Math.Clamp(_page, 0, _pages.Length - 1)].Start;
        var heights = new double[_overview.Count];
        for (int index = 0; index < _overview.Count; index++)
        {
            _overview[index].Measure(new Size(width, double.PositiveInfinity));
            heights[index] = _overview[index].DesiredSize.Height;
        }
        _pages = MetricComposition.OverviewPages(heights, availableHeight);
        if (_pages.IsEmpty) return;
        _page = 0;
        while (_page + 1 < _pages.Length && _pages[_page + 1].Start <= anchor) _page++;
        var (start, count, fits) = _pages[_page];
        int visibleCount = fits ? count : 0;
        bool same = OverviewPanel.Children.Count == visibleCount;
        for (int i = 0; same && i < visibleCount; i++)
            same = ReferenceEquals(OverviewPanel.Children[i], _overview[start + i]);
        if (!same)
        {
            OverviewPanel.Children.Clear();
            for (int i = start; i < start + visibleCount; i++) OverviewPanel.Children.Add(_overview[i]);
        }
        OverviewSizeWarning.Visibility = fits ? Visibility.Collapsed : Visibility.Visible;
        string page = $"Page {_page + 1}/{_pages.Length} · Metrics {start + 1}–{start + count}/{_overview.Count}";
        if (PageText.Text != page) PageText.Text = page;
        PreviousButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = _page + 1 < _pages.Length;
    }

    private void OnPrevious(object sender, RoutedEventArgs args)
    {
        if (_closing || _details || _page == 0) return;
        _page--;
        ScheduleOverviewPage();
    }
    private void OnNext(object sender, RoutedEventArgs args)
    {
        if (_closing || _details || _page + 1 >= _pages.Length) return;
        _page++;
        ScheduleOverviewPage();
    }
    private void OnToggleDetails(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        _details = !_details;
        OverviewView.Visibility = _details ? Visibility.Collapsed : Visibility.Visible;
        DetailsView.Visibility = _details ? Visibility.Visible : Visibility.Collapsed;
        DetailsButtonText.Text = _details ? "Back to overview" : "Composition details";
        if (!_details) ScheduleOverviewPage();
    }

    private void OnToggleCompact(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        if (!_compact)
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            _normalBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
        }
        _compact = !_compact;
        NormalShell.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CompactShell.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        if (!_compact)
        {
            CompactPinButton.IsChecked = false;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = false;
        }
        UpdateMinimumForScale();
        var current = AppWindow.Position;
        var target = _compact
            ? new RectInt32(current.X, current.Y, (int)Math.Ceiling(300d * _scale), (int)Math.Ceiling(250d * _scale))
            : _normalBounds ?? new RectInt32(current.X, current.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        MoveWithinStartupDisplay(target);
        if (_compact) ExpandButton.Focus(FocusState.Programmatic);
        else
        {
            CompactButton.Focus(FocusState.Programmatic);
            ScheduleOverviewPage();
        }
    }

    private void OnTogglePin(object sender, RoutedEventArgs args)
    {
        if (!_closing && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = CompactPinButton.IsChecked == true;
    }

    private void MoveWithinStartupDisplay(RectInt32 bounds)
    {
        if (_startupDisplay is not { } display)
        {
            AppWindow.MoveAndResize(bounds);
            return;
        }
        var work = display.WorkArea;
        var width = Math.Min(bounds.Width, work.Width);
        var height = Math.Min(bounds.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(
            Math.Clamp(bounds.X, work.X, work.X + work.Width - width),
            Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - height), width, height));
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
        ScheduleOverviewPage();
    }

    private void UpdateMinimumForScale()
    {
        _scale = Root.XamlRoot?.RasterizationScale ?? 1d;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)Math.Ceiling((_compact ? 260d : 360d) * _scale), _startupDisplay?.WorkArea.Width ?? int.MaxValue);
            presenter.PreferredMinimumHeight = Math.Min((int)Math.Ceiling((_compact ? 170d : 340d) * _scale), _startupDisplay?.WorkArea.Height ?? int.MaxValue);
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
