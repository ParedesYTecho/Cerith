#nullable enable

using System;
using Cerith.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Cerith.WidgetHost;

public sealed partial class MetricRing : UserControl
{
    private readonly MetricRangeSpec _spec;
    private readonly MetricReadout _readout;
    private MetricRangeResult? _lastRange;

    internal MetricRing(MetricRangeSpec spec)
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
        if (_lastRange != result)
        {
            _lastRange = result;
            Ring.Visibility = result.FillFraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
            Fill.Visibility = result.FillFraction is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
            FullFill.Visibility = result.FillFraction == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (result.FillFraction is > 0 and < 1)
            {
                var fraction = result.FillFraction.Value;
                var angle = fraction * Math.Tau;
                Arc.Point = new Point(16 + 14.5 * Math.Sin(angle), 16 - 14.5 * Math.Cos(angle));
                Arc.IsLargeArc = fraction > 0.5;
            }
        }
        var range = MetricRangePresentation.FormatRange(_spec, subscription.Descriptor, result);
        if (RangeText.Text != range) RangeText.Text = range;
        var presentation = MetricPresentation.Create(_spec.Presentation, subscription);
        var name = $"{subscription.Descriptor?.DisplayName ?? _spec.Presentation.MetricId.Value}, ring, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}, {range}";
        if (AutomationProperties.GetName(this) != name) AutomationProperties.SetName(this, name);
        Fill.Opacity = FullFill.Opacity = subscription.Snapshot?.State == MetricState.Stale ? 0.78 : 1;
    }
}
