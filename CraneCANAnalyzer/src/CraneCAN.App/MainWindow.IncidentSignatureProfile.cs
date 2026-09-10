using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private void ShowIncidentSignatureProfileBuilder(
        IncidentSignatureCandidate candidate,
        IReadOnlyList<LoadedIncidentPackage> packages,
        Window owner)
    {
        if (candidate.Kind != IncidentTransitionKind.ByteChanged ||
            !candidate.DataIndex.HasValue ||
            candidate.Priority == IncidentSignaturePriority.Info)
        {
            MessageBox.Show(
                "В Machine Profile можно добавить только MEDIUM/HIGH DATA[n]-сигнатуру.",
                "Cross-Incident → Machine Profile",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var suggestedStatus = candidate.Priority == IncidentSignaturePriority.High
            ? SignalKnowledgeState.Probable
            : SignalKnowledgeState.Candidate;

        var defaultName = candidate.ProfileSignals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(defaultName))
        {
            defaultName =
                $"0x{candidate.Id.ToString(candidate.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)} " +
                $"DATA[{candidate.DataIndex.Value}] signature";
        }

        var name = new TextBox
        {
            Text = defaultName,
            MinWidth = 260
        };
        var startByte = new TextBox
        {
            Text = candidate.DataIndex.Value.ToString(CultureInfo.InvariantCulture),
            Width = 80
        };
        var startBit = new TextBox { Text = "0", Width = 80 };
        var bitLength = new TextBox { Text = "8", Width = 80 };
        var byteOrder = new ComboBox
        {
            ItemsSource = Enum.GetValues<SignalByteOrder>(),
            SelectedItem = SignalByteOrder.LittleEndian,
            Width = 150
        };
        var isSigned = new CheckBox
        {
            Content = "Signed",
            VerticalAlignment = VerticalAlignment.Center
        };
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
        form.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var info = new TextBlock
        {
            Text =
                $"Источник: {(candidate.IsExtended ? "Extended" : "Standard")} ID 0x{candidate.Id:X} · " +
                $"DATA[{candidate.DataIndex.Value}] · {candidate.OccurrenceCount}/{candidate.IncidentCount} incident · " +
                $"переход {candidate.ModalBaselineValue} → {candidate.ModalObservedValue} · " +
                $"median {candidate.MedianReactionMilliseconds.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture)} ms · " +
                $"spread {candidate.TimingSpreadMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms.\n" +
                $"Рекомендуемый статус: {suggestedStatus.ToString().ToUpperInvariant()}. " +
                "HIGH может повысить CANDIDATE до PROBABLE, но никогда автоматически до CONFIRMED.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(info, 2);
        Grid.SetRow(info, 0);
        form.Children.Add(info);

        AddSignatureProfileRow(form, 1, "Название:", name);
        AddSignatureProfileRow(form, 2, "StartByte:", startByte);
        AddSignatureProfileRow(form, 3, "StartBit:", startBit);
        AddSignatureProfileRow(form, 4, "BitLength:", bitLength);
        AddSignatureProfileRow(form, 5, "Byte order:", byteOrder);
        AddSignatureProfileRow(form, 6, "Знак:", isSigned);
        AddSignatureProfileRow(form, 7, "Scale:", scale);
        AddSignatureProfileRow(form, 8, "Offset:", offset);
        AddSignatureProfileRow(form, 9, "Unit:", unit);
        AddSignatureProfileRow(form, 10, "Заметки:", notes);

        var add = new Button
        {
            Content = $"Добавить {suggestedStatus.ToString().ToUpperInvariant()}",
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
            Title = "CraneCAN — Cross-Incident → Machine Profile",
            Width = 590,
            Height = 640,
            MinWidth = 520,
            MinHeight = 570,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        cancel.Click += (_, _) => dialog.Close();
        add.Click += (_, _) =>
        {
            try
            {
                var definition = new IncidentSignatureSignalDefinition
                {
                    Name = name.Text,
                    StartByte = ParseSignatureProfileInt(startByte.Text, "StartByte"),
                    StartBit = ParseSignatureProfileInt(startBit.Text, "StartBit"),
                    BitLength = ParseSignatureProfileInt(bitLength.Text, "BitLength"),
                    ByteOrder = byteOrder.SelectedItem is SignalByteOrder selectedOrder
                        ? selectedOrder
                        : SignalByteOrder.LittleEndian,
                    IsSigned = isSigned.IsChecked == true,
                    Scale = ParseSignatureProfileDouble(scale.Text, "Scale"),
                    Offset = ParseSignatureProfileDouble(offset.Text, "Offset"),
                    Unit = unit.Text,
                    Notes = notes.Text
                };

                var update = IncidentSignatureProfileService.AddOrUpdate(
                    _machineProfile,
                    candidate,
                    packages.Select(package => package.Incident.IncidentId).ToArray(),
                    definition);
                _machineProfile = update.Profile;

                StatusText.Text = update.ExistingSignalUpdated
                    ? $"Сигнал «{update.Signal.Name}»: добавлена Cross-Incident evidence; " +
                      $"статус {update.PreviousConfidence.ToString().ToUpperInvariant()} → " +
                      $"{update.ResultingConfidence.ToString().ToUpperInvariant()}. Сохраните Machine Profile."
                    : $"Сигнал «{update.Signal.Name}» добавлен как " +
                      $"{update.ResultingConfidence.ToString().ToUpperInvariant()}. Сохраните Machine Profile.";

                dialog.DialogResult = true;
                dialog.Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Cross-Incident → Machine Profile",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        dialog.ShowDialog();
    }

    private static void AddSignatureProfileRow(
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

    private static int ParseSignatureProfileInt(
        string text,
        string field)
    {
        if (!int.TryParse(
                text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            throw new FormatException(
                $"Поле «{field}» должно содержать целое число.");
        }

        return value;
    }

    private static double ParseSignatureProfileDouble(
        string text,
        string field)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(
                normalized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            throw new FormatException(
                $"Поле «{field}» должно содержать число.");
        }

        return value;
    }
}
