#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cerith.WidgetHost;

public sealed partial class CompositionDialog : ContentDialog
{
    private readonly List<CompositionRow> _rows;
    private int _selected = -1;
    private bool _updating;
    internal MetricComposition? Result { get; private set; }

    internal CompositionDialog(MetricComposition current)
    {
        InitializeComponent();
        _rows = current.Rows.ToList();
        PrimaryButtonClick += OnSave;
        Refresh(0);
    }

    private void Refresh(int index)
    {
        _updating = true;
        RowsList.ItemsSource = _rows.Select((row, i) => $"{i + 1}. {row.Kind} · {row.Id}").ToArray();
        RowsList.SelectedIndex = index;
        _selected = index;
        var row = _rows[index];
        KindField.SelectedIndex = (int)row.Kind;
        FormatField.SelectedIndex = Array.IndexOf(new[] { "0", "0.0", "0.00", "0.000" }, row.Format);
        IdField.Text = row.Id;
        MinimumField.Text = row.Minimum?.ToString("R", CultureInfo.InvariantCulture) ?? "0";
        MaximumField.Text = row.Maximum?.ToString("R", CultureInfo.InvariantCulture) ?? "100";
        RangeFields.Visibility = row.Kind == CompositionKind.Text ? Visibility.Collapsed : Visibility.Visible;
        ValidationMessage.Text = string.Empty;
        _updating = false;
    }

    private bool Capture()
    {
        if (_selected < 0) return true;
        try
        {
            var kind = (CompositionKind)KindField.SelectedIndex;
            var format = (FormatField.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            double? min = kind == CompositionKind.Text ? null : double.Parse(MinimumField.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            double? max = kind == CompositionKind.Text ? null : double.Parse(MaximumField.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            var row = new CompositionRow(kind, IdField.Text, format, min, max);
            row.Validate();
            _rows[_selected] = row;
            ValidationMessage.Text = string.Empty;
            return true;
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidDataException)
        {
            ValidationMessage.Text = "Check MetricId, numeric format and finite increasing range.";
            return false;
        }
    }

    private void OnSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || RowsList.SelectedIndex < 0 || RowsList.SelectedIndex == _selected) return;
        int index = RowsList.SelectedIndex;
        if (!Capture()) { _updating = true; RowsList.SelectedIndex = _selected; _updating = false; return; }
        Refresh(index);
    }
    private void OnKindChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updating && KindField.SelectedIndex >= 0)
            RangeFields.Visibility = KindField.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnAdd(object sender, RoutedEventArgs args)
    {
        if (!Capture()) return;
        if (_rows.Count == 16) { ValidationMessage.Text = "Maximum 16 rows."; return; }
        _rows.Add(new(CompositionKind.Text, "cpu.total.usage", "0.0"));
        Refresh(_rows.Count - 1);
    }
    private void OnRemove(object sender, RoutedEventArgs args)
    {
        if (_rows.Count == 1) { ValidationMessage.Text = "At least one row is required."; return; }
        _rows.RemoveAt(_selected);
        Refresh(Math.Min(_selected, _rows.Count - 1));
    }
    private void Move(int offset)
    {
        if (!Capture()) return;
        int next = _selected + offset;
        if (next < 0 || next >= _rows.Count) return;
        (_rows[next], _rows[_selected]) = (_rows[_selected], _rows[next]);
        Refresh(next);
    }
    private void OnUp(object sender, RoutedEventArgs args) => Move(-1);
    private void OnDown(object sender, RoutedEventArgs args) => Move(1);
    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Capture()) { args.Cancel = true; return; }
        try { Result = new MetricComposition(_rows); }
        catch (InvalidDataException) { ValidationMessage.Text = "Invalid composition."; args.Cancel = true; }
    }
}
