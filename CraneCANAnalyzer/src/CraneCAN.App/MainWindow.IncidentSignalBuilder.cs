using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Profiles;

namespace CraneCAN.App;

public partial class MainWindow
{
    private void ShowIncidentSignalBuilder(
        IncidentEventChainStep step,
        DateTimeOffset markerTime,
        Window owner)
    {
        if (step.Kind != IncidentTransitionKind.ByteChanged || !step.DataIndex.HasValue)
        {
            MessageBox.Show(
                "Signal Builder доступен только для шага изменения DATA[n].",
                "Signal Builder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var name = new TextBox
        {
            Text = $"{(step.IsExtended ? step.Id.ToString("X8") : step.Id.ToString("X3"))} DATA[{step.DataIndex.Value}]",
            MinWidth = 260
        };
        var startByte = new TextBox { Text = step.DataIndex.Value.ToString(CultureInfo.InvariantCulture), Width = 80 };
        var startBit = new TextBox { Text = "0", Width = 80 };
        var bitLength = new TextBox { Text = "8", Width = 80 };
        var byteOrder = new ComboBox
        {
            ItemsSource = Enum.GetValues<SignalByteOrder>(),
            SelectedItem = SignalByteOrder.LittleEndian,
            Width = 150
        };
        var isSigned = new CheckBox { Content = "Signed", VerticalAlignment = VerticalAlignment.Center };
        var scale = new TextBox { Text = "1", Width = 100 };
        var offset = new TextBox { Text = "0", Width = 100 };
        var unit = new TextBox { Width = 120 };
        var notes = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var info = new TextBlock
        {
            Text =
                $"Источник: {(step.IsExtended ? "Extended" : "Standard")} ID 0x{step.Id:X} · DATA[{step.DataIndex.Value}] · " +
                $"{step.BaselineValue} → {step.ObservedValue} · {step.ReactionMilliseconds:+0.###;-0.###;0} ms.\n" +
                "Новый сигнал добавляется только как CANDIDATE. CONFIRMED требует отдельного подтверждения evidence.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(info, 2);
        Grid.SetRow(info, 0);
        form.Children.Add(info);

        AddSignalBuilderRow(form, 1, "Название:", name);
        AddSignalBuilderRow(form, 2, "StartByte:", startByte);
        AddSignalBuilderRow(form, 3, "StartBit:", startBit);
        AddSignalBuilderRow(form, 4, "BitLength:", bitLength);
        AddSignalBuilderRow(form, 5, "Byte order:", byteOrder);
        AddSignalBuilderRow(form, 6, "Знак:", isSigned);
        AddSignalBuilderRow(form, 7, "Scale:", scale);
        AddSignalBuilderRow(form, 8, "Offset:", offset);
        AddSignalBuilderRow(form, 9, "Unit:", unit);
        AddSignalBuilderRow(form, 10, "Заметки:", notes);

        var add = new Button
        {
            Content = "Добавить CANDIDATE",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var cancel = new Button
        {
            Content = "Отмена",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        buttons.Children.Add(add);
        buttons.Children.Add(cancel);

        var layout = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(form);

        var dialog = new Window
        {
            Owner = owner,
            Title = "CraneCAN — Signal Builder",
            Width = 560,
            Height = 610,
            MinWidth = 500,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        cancel.Click += (_, _) => dialog.Close();
        add.Click += (_, _) =>
        {
            try
            {
                var definition = new IncidentSignalDefinition
                {
                    Name = name.Text,
                    StartByte = ParseSignalBuilderInt(startByte.Text, "StartByte"),
                    StartBit = ParseSignalBuilderInt(startBit.Text, "StartBit"),
                    BitLength = ParseSignalBuilderInt(bitLength.Text, "BitLength"),
                    ByteOrder = byteOrder.SelectedItem is SignalByteOrder selectedOrder
                        ? selectedOrder
                        : SignalByteOrder.LittleEndian,
                    IsSigned = isSigned.IsChecked == true,
                    Scale = ParseSignalBuilderDouble(scale.Text, "Scale"),
                    Offset = ParseSignalBuilderDouble(offset.Text, "Offset"),
                    Unit = unit.Text,
                    Notes = notes.Text
                };

                var update = IncidentSignalProfileService.AddOrUpdate(
                    _machineProfile,
                    step,
                    markerTime,
                    definition);
                _machineProfile = update.Profile;

                StatusText.Text = update.ExistingSignalUpdated
                    ? $"Существующий сигнал «{update.Signal.Name}» дополнен incident evidence. Сохраните Machine Profile."
                    : $"Сигнал «{update.Signal.Name}» добавлен как CANDIDATE. Сохраните Machine Profile.";

                dialog.DialogResult = true;
                dialog.Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Signal Builder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        dialog.ShowDialog();
    }

    private static void AddSignalBuilderRow(
        Grid form,
        int row,
        string label,
        UIElement control)
    {
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (control is FrameworkElement element)
            element.Margin = new Thickness(0, 4, 0, 4);

        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        form.Children.Add(text);
        form.Children.Add(control);
    }

    private static int ParseSignalBuilderInt(string text, string field)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Поле «{field}» должно содержать целое число.");
        return value;
    }

    private static double ParseSignalBuilderDouble(string text, string field)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(
                normalized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
            throw new FormatException($"Поле «{field}» должно содержать число.");
        return value;
    }
}
