#nullable enable

using System;
using Cerith.Core;
using Microsoft.UI.Xaml;
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
        VisualStateManager.GoToState(this, presentation.State switch
        {
            MetricState.Fresh or MetricState.Unlimited => "Live",
            MetricState.Stale => "Stale",
            MetricState.Error => "Error",
            _ => "Off"
        }, false);
        AutomationProperties.SetName(this,
            $"{label}, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}");
        PresentationChanged?.Invoke();
    }
}
