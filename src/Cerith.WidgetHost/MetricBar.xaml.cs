#nullable enable

using System.Globalization;
using Cerith.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Cerith.WidgetHost;

public sealed partial class MetricBar : UserControl
{
    private readonly MetricBarSpec _spec;
    private readonly MetricReadout _readout;
    private MetricBarResult? _lastBar;

    internal MetricBar(MetricBarSpec spec)
    {
        _spec = spec;
        InitializeComponent();
        _readout = new MetricReadout(spec.Presentation);
        ReadoutHost.Content = _readout;
    }

    internal void Apply(MetricSubscription subscription)
    {
        var result = MetricBarPresentation.Create(_spec, subscription.Descriptor, subscription.Snapshot);
        _readout.Apply(subscription);
        if (_lastBar != result)
        {
            _lastBar = result;
            Track.Visibility = result.FillFraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
            FillScale.ScaleX = result.FillFraction ?? 0;
        }
        var range = $"Range {_spec.Minimum.ToString(CultureInfo.InvariantCulture)}–{_spec.Maximum.ToString(CultureInfo.InvariantCulture)}";
        if (subscription.Descriptor?.Unit is { } unit) range += $" {unit}";
        if (result.RangeState.Length > 0) range += $" · {result.RangeState}";
        if (RangeText.Text != range) RangeText.Text = range;
        var presentation = MetricPresentation.Create(_spec.Presentation, subscription);
        var name = $"{subscription.Descriptor?.DisplayName ?? _spec.Presentation.MetricId.Value}, bar, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}, {range}";
        if (AutomationProperties.GetName(this) != name) AutomationProperties.SetName(this, name);
        Fill.Opacity = subscription.Snapshot?.State == MetricState.Stale ? 0.78 : 1;
    }
}
