#nullable enable

using System.IO;
using Cerith.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Cerith.WidgetHost;

public sealed partial class MetricReadout : UserControl
{
    private readonly MetricPresentationSpec _spec;
    private MetricDescriptor? _descriptor;
    private MetricPresentationResult? _lastPresentation;

    internal MetricReadout(MetricPresentationSpec spec)
    {
        _spec = spec;
        InitializeComponent();
        LabelText.Text = spec.MetricId.Value;
        ShowConnecting();
    }

    internal void BindDescriptor(MetricDescriptor? descriptor)
    {
        if (descriptor is not null && descriptor.Id != _spec.MetricId)
            throw new InvalidDataException("Descriptor MetricId does not match readout binding.");
        _descriptor = descriptor;
        LabelText.Text = descriptor?.DisplayName ?? _spec.MetricId.Value;
        AutomationProperties.SetName(this, $"{LabelText.Text}, {StateText.Text}, {ConfidenceText.Text}");
    }

    internal void Apply(MetricSnapshot snapshot)
    {
        var presentation = MetricPresentation.Create(_spec, _descriptor, snapshot);
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
            $"{LabelText.Text}, {presentation.ValueText}, {presentation.StateText}, {presentation.ConfidenceText}");
    }

    internal void ShowConnecting()
    {
        _lastPresentation = null;
        ValueText.Text = "—";
        StateText.Text = "Connecting";
        ConfidenceText.Text = "No snapshot";
        StateRail.Opacity = 0.35;
        AutomationProperties.SetName(this, $"{LabelText.Text}, connecting");
    }

    internal void ShowWaiting()
    {
        _lastPresentation = null;
        ValueText.Text = "—";
        StateText.Text = "Waiting";
        ConfidenceText.Text = "No snapshot";
        StateRail.Opacity = 0.35;
        AutomationProperties.SetName(this, $"{LabelText.Text}, waiting, no snapshot");
    }

    internal void ShowUnavailable()
    {
        _lastPresentation = null;
        ValueText.Text = "—";
        StateText.Text = "Unavailable";
        ConfidenceText.Text = "No snapshot";
        StateRail.Opacity = 0.35;
        AutomationProperties.SetName(this, $"{LabelText.Text}, unavailable, no snapshot");
    }
}
