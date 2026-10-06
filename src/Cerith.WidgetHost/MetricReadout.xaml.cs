#nullable enable

using Cerith.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Cerith.WidgetHost;

public sealed partial class MetricReadout : UserControl
{
    private readonly MetricPresentationSpec _spec;
    private MetricPresentationResult? _lastPresentation;

    internal MetricReadout(MetricPresentationSpec spec)
    {
        _spec = spec;
        InitializeComponent();
        LabelText.Text = spec.Label;
        ShowConnecting();
    }

    internal void Apply(MetricSnapshot snapshot)
    {
        var presentation = MetricPresentation.Create(_spec, snapshot);
        if (_lastPresentation == presentation) return;
        _lastPresentation = presentation;
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
            $"{_spec.Label}, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}");
    }

    internal void ShowConnecting()
    {
        _lastPresentation = null;
        ValueText.Text = "—";
        StateText.Text = "Connecting";
        ConfidenceText.Text = "No snapshot";
        StateRail.Opacity = 0.35;
        AutomationProperties.SetName(this, $"{_spec.Label}, connecting");
    }

    internal void ShowUnavailable()
    {
        _lastPresentation = null;
        ValueText.Text = "—";
        StateText.Text = "Unavailable";
        ConfidenceText.Text = "No snapshot";
        StateRail.Opacity = 0.35;
        AutomationProperties.SetName(this, $"{_spec.Label}, unavailable, no snapshot");
    }
}
