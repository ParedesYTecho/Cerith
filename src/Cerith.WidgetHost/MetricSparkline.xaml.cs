#nullable enable

using System.Globalization;
using Cerith.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Cerith.WidgetHost;

public sealed partial class MetricSparkline : UserControl
{
    private readonly MetricRangeSpec _spec;
    private readonly MetricReadout _readout;
    private readonly MetricSparklineSequence _sequence;

    internal MetricSparkline(MetricRangeSpec spec)
    {
        _spec = spec;
        _sequence = new(spec);
        InitializeComponent();
        _readout = new MetricReadout(spec.Presentation);
        ReadoutHost.Content = _readout;
    }

    internal void Apply(MetricSubscription subscription)
    {
        // Consume every observation before any display equality optimization.
        var geometryChanged = _sequence.Apply(subscription.Descriptor, subscription.Snapshot);
        _readout.Apply(subscription);
        var result = MetricRangePresentation.Create(_spec, subscription.Descriptor, subscription.Snapshot);
        var range = MetricRangePresentation.FormatRange(_spec, subscription.Descriptor, result);
        var history = $"{_sequence.Samples.Count} retained Fresh samples, observed span {_sequence.Span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";
        var details = $"{range} · {history}";
        if (RangeText.Text != details) RangeText.Text = details;
        var presentation = MetricPresentation.Create(_spec.Presentation, subscription);
        var name = $"{subscription.Descriptor?.DisplayName ?? _spec.Presentation.MetricId.Value}, sparkline, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}, {details}";
        if (AutomationProperties.GetName(this) != name) AutomationProperties.SetName(this, name);
        Line.Opacity = _sequence.IsStale ? 0.78 : 1;
        Plot.Visibility = _sequence.Samples.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (geometryChanged) Redraw();
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs args) => Redraw();

    private void Redraw()
    {
        var samples = _sequence.Samples;
        var width = Plot.ActualWidth - 2;
        var height = Plot.ActualHeight - 2;
        if (samples.Count < 2 || width <= 0 || height <= 0)
        {
            Line.Visibility = Visibility.Collapsed;
            Line.Points?.Clear();
            return;
        }

        var duration = _sequence.Span.Ticks;
        if (duration <= 0)
        {
            Line.Visibility = Visibility.Collapsed;
            return;
        }

        var points = new PointCollection();
        foreach (var sample in samples)
        {
            var elapsed = (sample.Time - samples[0].Time).Ticks;
            points.Add(new Point(1 + width * (elapsed / (double)duration), 1 + height * (1 - sample.Fraction)));
        }
        Line.Points = points;
        Line.Visibility = Visibility.Visible;
    }
}
