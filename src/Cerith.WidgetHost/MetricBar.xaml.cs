#nullable enable

using Cerith.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Cerith.WidgetHost;

public sealed partial class MetricBar : UserControl
{
    private readonly MetricRangeSpec _spec;
    private readonly MetricReadout _readout;
    private MetricRangeResult? _lastBar;

    internal MetricBar(MetricRangeSpec spec)
    {
        _spec = spec;
        InitializeComponent();
        _readout = new MetricReadout(spec.Presentation);
        ReadoutHost.Content = _readout;
    }

    internal void Apply(MetricSubscription subscription)
    {
        var result = MetricRangePresentation.Create(_spec, subscription.Descriptor, subscription.Snapshot);
        _readout.Apply(subscription);
        if (_lastBar != result)
        {
            _lastBar = result;
            Track.Visibility = result.FillFraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
            FillScale.ScaleX = result.FillFraction ?? 0;
        }
        var range = MetricRangePresentation.FormatRange(_spec, subscription.Descriptor, result);
        if (RangeText.Text != range) RangeText.Text = range;
        var presentation = MetricPresentation.Create(_spec.Presentation, subscription);
        var name = $"{subscription.Descriptor?.DisplayName ?? _spec.Presentation.MetricId.Value}, bar, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}, {range}";
        if (AutomationProperties.GetName(this) != name) AutomationProperties.SetName(this, name);
        Fill.Opacity = subscription.Snapshot?.State == MetricState.Stale ? 0.78 : 1;
    }
}
