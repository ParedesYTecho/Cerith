#nullable enable

using System;
using Cerith.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Cerith.WidgetHost;

public sealed partial class MetricReadout : UserControl
{
    private readonly MetricPresentationSpec _spec;
    private MetricPresentationResult? _lastPresentation;
    internal event Action? PresentationChanged;

    internal MetricReadout(MetricPresentationSpec spec)
    {
        _spec = spec;
        InitializeComponent();
    }

    internal void Apply(MetricSubscription subscription)
    {
        var presentation = MetricPresentation.Create(_spec, subscription);
        var label = subscription.Descriptor?.DisplayName ?? _spec.MetricId.Value;
        if (_lastPresentation == presentation && LabelText.Text == label) return;
        _lastPresentation = presentation;
        LabelText.Text = label;
        ValueText.Text = presentation.ValueText;
        StateText.Text = presentation.StateText;
        ConfidenceText.Text = presentation.ConfidenceText;
        StateRail.Opacity = presentation.State switch
        {
            MetricState.Fresh => 1,
            MetricState.Stale => 0.78,
            MetricState.Error => 1,
            MetricState.Unlimited => 0.7,
            _ => 0.35
        };
        AutomationProperties.SetName(this,
            $"{label}, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}");
        PresentationChanged?.Invoke();
    }
}
