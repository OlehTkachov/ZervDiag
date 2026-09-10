using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Profiles;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _guidedAnalogCalibrationButton;

    [ModuleInitializer]
    internal static void RegisterGuidedAnalogCalibrationUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(GuidedAnalogCalibrationMainWindowLoaded));
    }

    private static void GuidedAnalogCalibrationMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window ||
            window.AnalyzeGuidedButton.Parent is not Panel panel)
            return;

        if (panel.Children
            .OfType<Button>()
            .Any(button => string.Equals(
                button.Tag as string,
                "guided-analog-calibration",
                StringComparison.Ordinal)))
            return;

        var button = new Button
        {
            Content = "Калибровка raw…",
            Tag = "guided-analog-calibration",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip =
                "Физическая калибровка Machine Profile: минимум две пары raw=physical, " +
                "линейная аппроксимация scale/offset, ошибка и R². Unit вводит инженер; Confidence автоматически не меняется."
        };
        button.Click += window.GuidedAnalogCalibrationButton_Click;

        var index = panel.Children.IndexOf(window.AnalyzeGuidedButton);
        if (index < 0) panel.Children.Add(button);
        else panel.Children.Insert(index, button);
        window._guidedAnalogCalibrationButton = button;
    }

    private void GuidedAnalogCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateProfileFromFields();
            var choices = BuildAnalogCalibrationChoices();
            if (choices.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "В Machine Profile пока нет сигналов для калибровки.\n\n" +
                    "Сначала добавьте найденный raw-кандидат через «Аналоговые сигналы…» " +
                    "или откройте профиль с определённым сигналом.",
                    "Analog Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowAnalogCalibrationWindow(choices);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                FormatException(exception),
                "Analog Calibration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private IReadOnlyList<AnalogCalibrationSignalChoice> BuildAnalogCalibrationChoices()
    {
        var known = _machineProfile.KnownSignals
            .Select(signal => new AnalogCalibrationSignalChoice(signal, "KNOWN"));
        var experimental = _machineProfile.ExperimentalSignals
            .Select(signal => new AnalogCalibrationSignalChoice(signal, "EXPERIMENTAL"));
        return known.Concat(experimental)
            .OrderBy(choice => choice.Signal.CanId)
            .ThenBy(choice => choice.Signal.IsExtended)
            .ThenBy(choice => choice.Signal.StartByte)
            .ThenBy(choice => choice.Signal.StartBit)
            .ThenBy(choice => choice.Signal.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ShowAnalogCalibrationWindow(IReadOnlyList<AnalogCalibrationSignalChoice> choices)
    {
        var instructions = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                "Введите реально измеренные пары raw = physical. Пример: 1000 = 12.5. " +
                "Десятичная точка или запятая поддерживается. Две точки дают scale/offset, но не проверяют линейность; " +
                "для оценки линейности используйте минимум 3 точки по рабочему диапазону.\n" +
                "Raw — значение поля ДО текущих Scale/Offset. Для signed сигнала вводите signed raw. " +
                "Единица (m, deg, bar и т. п.) задаётся инженером и никогда не угадывается по CAN."
        };

        var signalCombo = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(AnalogCalibrationSignalChoice.DisplayText),
            SelectedIndex = 0,
            Margin = new Thickness(4),
            MinWidth = 720
        };

        var unitCombo = new ComboBox
        {
            IsEditable = true,
            ItemsSource = new[] { "m", "mm", "cm", "deg", "°", "bar", "MPa", "kPa", "%", "A", "V", "rpm" },
            Margin = new Thickness(4),
            Width = 120
        };

        var pointsBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Height = 170,
            Margin = new Thickness(4),
            ToolTip = "Одна точка на строку: raw = physical. Строки, начинающиеся с #, игнорируются."
        };

        var currentText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 2, 4, 8)
        };

        var previewBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12.5,
            MinHeight = 230,
            Margin = new Thickness(4),
            Text = "Введите минимум две точки и нажмите «Рассчитать»."
        };

        var calculate = new Button
        {
            Content = "Рассчитать",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var apply = new Button
        {
            Content = "Применить Scale/Offset/Unit к профилю",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = false
        };
        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var signalLabel = new TextBlock
        {
            Text = "Сигнал:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4)
        };
        Grid.SetRow(signalLabel, 0);
        Grid.SetColumn(signalLabel, 0);
        Grid.SetRow(signalCombo, 0);
        Grid.SetColumn(signalCombo, 1);
        Grid.SetColumnSpan(signalCombo, 3);
        form.Children.Add(signalLabel);
        form.Children.Add(signalCombo);

        var unitLabel = new TextBlock
        {
            Text = "Unit:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4)
        };
        Grid.SetRow(unitLabel, 1);
        Grid.SetColumn(unitLabel, 0);
        Grid.SetRow(unitCombo, 1);
        Grid.SetColumn(unitCombo, 1);
        form.Children.Add(unitLabel);
        form.Children.Add(unitCombo);

        var pointsLabel = new TextBlock
        {
            Text = "Calibration points:",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 8, 4, 4)
        };
        Grid.SetRow(pointsLabel, 2);
        Grid.SetColumn(pointsLabel, 0);
        Grid.SetRow(pointsBox, 2);
        Grid.SetColumn(pointsBox, 1);
        Grid.SetColumnSpan(pointsBox, 3);
        form.Children.Add(pointsLabel);
        form.Children.Add(pointsBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        buttons.Children.Add(calculate);
        buttons.Children.Add(apply);
        buttons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(instructions, 0);
        Grid.SetRow(form, 1);
        Grid.SetRow(currentText, 2);
        Grid.SetRow(previewBox, 3);
        Grid.SetRow(buttons, 4);
        layout.Children.Add(instructions);
        layout.Children.Add(form);
        layout.Children.Add(currentText);
        layout.Children.Add(previewBox);
        layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — Analog Calibration / Physical Correlation",
            Width = 1120,
            Height = 760,
            MinWidth = 900,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        void RefreshSignalDetails()
        {
            if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice choice)
                return;
            var signal = choice.Signal;
            currentText.Text =
                $"Текущее определение: {choice.CollectionName}; {FormatCalibrationField(signal)}; " +
                $"signed={signal.IsSigned}; Scale={FormatCalibrationNumber(signal.Scale)}; " +
                $"Offset={FormatCalibrationNumber(signal.Offset)}; Unit={FormatCalibrationUnit(signal.Unit)}; " +
                $"Confidence={signal.Confidence}. Confidence при калибровке не меняется.";
            unitCombo.Text = signal.Unit;
            apply.IsEnabled = false;
            previewBox.Text = "Введите минимум две точки и нажмите «Рассчитать».";
        }

        AnalogCalibrationResult Calculate()
        {
            var points = AnalogPhysicalCalibration.ParsePoints(pointsBox.Text);
            return AnalogPhysicalCalibration.Fit(points, unitCombo.Text);
        }

        signalCombo.SelectionChanged += (_, _) => RefreshSignalDetails();
        RefreshSignalDetails();

        calculate.Click += (_, _) =>
        {
            try
            {
                var result = Calculate();
                previewBox.Text = FormatAnalogCalibrationPreview(result);
                apply.IsEnabled = result.CanApply;
            }
            catch (Exception exception)
            {
                apply.IsEnabled = false;
                previewBox.Text = "Ошибка: " + FormatException(exception);
            }
        };

        apply.Click += (_, _) =>
        {
            try
            {
                if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice selected)
                    throw new InvalidOperationException("Выберите Machine Profile signal.");

                // Recalculate from the current text so a stale preview can never be applied.
                var result = Calculate();
                if (!result.CanApply)
                    throw new InvalidOperationException(
                        "Качество калибровки POOR. Повторите физические измерения; запись в профиль заблокирована.");

                var current = FindCurrentProfileSignal(selected.Signal.SignalId)
                    ?? throw new InvalidOperationException("Выбранный сигнал больше не найден в текущем Machine Profile.");

                var confirmation =
                    $"Сигнал: {current.Name}\n" +
                    $"Поле: {FormatCalibrationField(current)}\n" +
                    $"Текущие: Scale={FormatCalibrationNumber(current.Scale)}, " +
                    $"Offset={FormatCalibrationNumber(current.Offset)}, Unit={FormatCalibrationUnit(current.Unit)}\n" +
                    $"Новые: Scale={FormatCalibrationNumber(result.Scale)}, " +
                    $"Offset={FormatCalibrationNumber(result.Offset)}, Unit={result.Unit}\n" +
                    $"Точек: {result.Points.Count}; качество: {result.Quality}; " +
                    $"R²={FormatCalibrationNumber(result.RSquared)}; " +
                    $"max error={FormatCalibrationNumber(result.MaximumAbsoluteError)} {result.Unit} " +
                    $"({FormatCalibrationNumber(result.MaximumErrorPercentOfSpan)}% диапазона).\n\n" +
                    $"Confidence останется {current.Confidence}. Изменение не будет сохранено на диск, пока вы явно не сохраните Machine Profile.\n\n" +
                    "Применить эту физическую калибровку?";

                if (MessageBox.Show(
                        window,
                        confirmation,
                        "Подтверждение Analog Calibration",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                var updated = AnalogPhysicalCalibration.ApplyToSignal(
                    current,
                    result,
                    DateTimeOffset.UtcNow);
                if (!ReplaceProfileSignal(updated))
                    throw new InvalidOperationException("Не удалось заменить сигнал в Machine Profile.");

                ApplyProfileToFields();
                StatusText.Text =
                    $"Analog Calibration применена к «{updated.Name}»: " +
                    $"Scale={FormatCalibrationNumber(updated.Scale)}, Offset={FormatCalibrationNumber(updated.Offset)}, " +
                    $"Unit={updated.Unit}; Confidence={updated.Confidence}. Профиль ещё не сохранён. CAN Tx отсутствует.";

                MessageBox.Show(
                    window,
                    "Scale/Offset/Unit записаны в текущий Machine Profile и добавлено PhysicalOutputCheck evidence.\n\n" +
                    $"Confidence сохранён без автоматического повышения: {updated.Confidence}.\n" +
                    "Для постоянного хранения нажмите «Сохранить…» в Machine Profile.",
                    "Analog Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                window.Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    window,
                    FormatException(exception),
                    "Analog Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private MachineSignal? FindCurrentProfileSignal(Guid signalId) =>
        _machineProfile.KnownSignals
            .Concat(_machineProfile.ExperimentalSignals)
            .FirstOrDefault(signal => signal.SignalId == signalId);

    private bool ReplaceProfileSignal(MachineSignal updated)
    {
        var knownIndex = _machineProfile.KnownSignals.FindIndex(signal => signal.SignalId == updated.SignalId);
        if (knownIndex >= 0)
        {
            _machineProfile.KnownSignals[knownIndex] = updated;
            return true;
        }

        var experimentalIndex = _machineProfile.ExperimentalSignals.FindIndex(signal => signal.SignalId == updated.SignalId);
        if (experimentalIndex >= 0)
        {
            _machineProfile.ExperimentalSignals[experimentalIndex] = updated;
            return true;
        }

        return false;
    }

    private static string FormatAnalogCalibrationPreview(AnalogCalibrationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("physical = raw * Scale + Offset");
        builder.AppendLine($"Scale             = {FormatCalibrationNumber(result.Scale)} {result.Unit}/raw");
        builder.AppendLine($"Offset            = {FormatCalibrationNumber(result.Offset)} {result.Unit}");
        builder.AppendLine($"Points            = {result.Points.Count}");
        builder.AppendLine($"Quality           = {result.Quality}");
        builder.AppendLine($"R²                = {FormatCalibrationNumber(result.RSquared)}");
        builder.AppendLine($"RMSE              = {FormatCalibrationNumber(result.RootMeanSquareError)} {result.Unit} ({FormatCalibrationNumber(result.RmsePercentOfSpan)}% span)");
        builder.AppendLine($"Max absolute error= {FormatCalibrationNumber(result.MaximumAbsoluteError)} {result.Unit} ({FormatCalibrationNumber(result.MaximumErrorPercentOfSpan)}% span)");
        builder.AppendLine($"Physical span     = {FormatCalibrationNumber(result.PhysicalSpan)} {result.Unit}");
        builder.AppendLine($"Linearity checked = {(result.LinearityValidated ? "YES" : "NO")}");
        builder.AppendLine();
        builder.AppendLine("raw\tmeasured\tpredicted\terror");
        foreach (var item in result.Residuals)
        {
            builder.Append(FormatCalibrationNumber(item.RawValue)).Append('\t')
                .Append(FormatCalibrationNumber(item.PhysicalValue)).Append('\t')
                .Append(FormatCalibrationNumber(item.PredictedValue)).Append('\t')
                .Append(FormatCalibrationSigned(item.Error)).AppendLine();
        }

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings:");
            foreach (var warning in result.Warnings)
                builder.Append("- ").AppendLine(warning);
        }

        if (!result.CanApply)
        {
            builder.AppendLine();
            builder.AppendLine("APPLY BLOCKED: качество POOR. Повторите измерения или добавьте точки по рабочему диапазону.");
        }
        return builder.ToString();
    }

    private static string FormatCalibrationField(MachineSignal signal)
    {
        var id = signal.CanId.ToString(signal.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture);
        return $"ID {id} {(signal.IsExtended ? "Extended" : "Standard")}, " +
               $"byte {signal.StartByte}, bit {signal.StartBit}, len {signal.BitLength}, {signal.ByteOrder}";
    }

    private static string FormatCalibrationNumber(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string FormatCalibrationSigned(double value) =>
        value.ToString(value >= 0 ? "+0.#########" : "0.#########", CultureInfo.InvariantCulture);

    private static string FormatCalibrationUnit(string unit) =>
        string.IsNullOrWhiteSpace(unit) ? "<empty>" : unit;

    private sealed record AnalogCalibrationSignalChoice(
        MachineSignal Signal,
        string CollectionName)
    {
        public string DisplayText =>
            $"{CollectionName} | {Signal.Name} | {FormatCalibrationField(Signal)} | {Signal.Confidence}";
    }
}
