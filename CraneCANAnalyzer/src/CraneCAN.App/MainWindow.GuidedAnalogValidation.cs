using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Profiles;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _guidedAnalogValidationButton;

    [ModuleInitializer]
    internal static void RegisterGuidedAnalogValidationUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(GuidedAnalogValidationMainWindowLoaded));
    }

    private static void GuidedAnalogValidationMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window ||
            window.AnalyzeGuidedButton.Parent is not Panel panel)
            return;

        if (panel.Children.OfType<Button>().Any(button =>
                string.Equals(button.Tag as string, "guided-analog-validation", StringComparison.Ordinal)))
            return;

        var button = new Button
        {
            Content = "Проверка калибровки…",
            Tag = "guided-analog-validation",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip =
                "Независимая проверка уже рассчитанных Scale/Offset по новым физическим точкам. " +
                "Калибровка не пересчитывается; raw берётся только из принятого Live/Replay CAN."
        };
        button.Click += window.GuidedAnalogValidationButton_Click;

        var markerButton = panel.Children.OfType<Button>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                "guided-analog-markers",
                StringComparison.Ordinal));
        var calibrationButton = panel.Children.OfType<Button>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                "guided-analog-calibration",
                StringComparison.Ordinal));

        var index = markerButton is not null
            ? panel.Children.IndexOf(markerButton) + 1
            : calibrationButton is not null
                ? panel.Children.IndexOf(calibrationButton)
                : panel.Children.IndexOf(window.AnalyzeGuidedButton);
        if (index < 0 || index > panel.Children.Count) panel.Children.Add(button);
        else panel.Children.Insert(index, button);
        window._guidedAnalogValidationButton = button;
    }

    private void GuidedAnalogValidationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateProfileFromFields();
            var choices = BuildAnalogCalibrationChoices()
                .Where(choice => !string.IsNullOrWhiteSpace(choice.Signal.Unit))
                .ToArray();
            if (choices.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "Нет сигнала с заданной физической Unit для независимой проверки.\n\n" +
                    "Сначала выполните «Физические точки…» или «Калибровка raw…», " +
                    "чтобы задать Scale/Offset/Unit в Machine Profile.",
                    "Calibration Validation Run",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowAnalogValidationWindow(choices);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                FormatException(exception),
                "Calibration Validation Run",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowAnalogValidationWindow(IReadOnlyList<AnalogCalibrationSignalChoice> choices)
    {
        var points = new ObservableCollection<AnalogValidationMarkerRow>();
        var signalCombo = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(AnalogCalibrationSignalChoice.DisplayText),
            SelectedIndex = 0,
            Margin = new Thickness(4),
            MinWidth = 720
        };
        var physicalBox = new TextBox
        {
            Width = 170,
            Margin = new Thickness(4),
            ToolTip = "Новое независимо измеренное физическое значение. Не используйте точку исходной калибровки."
        };
        var unitText = new TextBlock
        {
            Margin = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };
        var capture = new Button
        {
            Content = "Зафиксировать независимую точку",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            ToolTip =
                "Берёт median стабильного raw из уже принятого Live/Replay buffer. " +
                "Scale/Offset не применяются при фиксации raw; CAN Tx отсутствует."
        };
        var remove = new Button
        {
            Content = "Удалить точку",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(4),
            IsEnabled = false
        };
        var validate = new Button
        {
            Content = "Проверить калибровку",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = false
        };
        var recordEvidence = new Button
        {
            Content = "Записать PASS evidence",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = false,
            ToolTip =
                "Добавляет PhysicalOutputCheck evidence только для PASS. Scale/Offset/Unit и Confidence не меняются."
        };
        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };

        var instructions = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text =
                "Это контроль ПОСЛЕ калибровки. Используйте минимум 3 новых физических положения, " +
                "которые не участвовали в расчёте Scale/Offset, и распределите их по рабочему диапазону. " +
                "CraneCAN не подгоняет прямую повторно: для каждой точки сравнивается " +
                "CAN prediction = raw × текущий Scale + текущий Offset с реально измеренным physical.\n" +
                "Точка принимается только при стабильном raw. PASS evidence можно записать в профиль, " +
                "но Confidence автоматически не повышается и причинность не утверждается."
        };
        var formulaStatus = new TextBlock
        {
            Margin = new Thickness(4, 2, 4, 8),
            TextWrapping = TextWrapping.Wrap
        };
        var sourceStatus = new TextBlock
        {
            Margin = new Thickness(4, 2, 4, 8),
            TextWrapping = TextWrapping.Wrap
        };
        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12.5,
            MinHeight = 210,
            Margin = new Thickness(4),
            Text = "Зафиксируйте минимум три независимые физические точки."
        };

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = points,
            MinHeight = 210,
            Margin = new Thickness(4)
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(AnalogValidationMarkerRow.Number)), Width = 42 });
        grid.Columns.Add(new DataGridTextColumn { Header = "raw median", Binding = new Binding(nameof(AnalogValidationMarkerRow.RawText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "measured", Binding = new Binding(nameof(AnalogValidationMarkerRow.MeasuredText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "predicted", Binding = new Binding(nameof(AnalogValidationMarkerRow.PredictedText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "error", Binding = new Binding(nameof(AnalogValidationMarkerRow.ErrorText)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Unit", Binding = new Binding(nameof(AnalogValidationMarkerRow.Unit)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Источник", Binding = new Binding(nameof(AnalogValidationMarkerRow.Source)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(AnalogValidationMarkerRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Samples", Binding = new Binding(nameof(AnalogValidationMarkerRow.SampleCount)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Span raw", Binding = new Binding(nameof(AnalogValidationMarkerRow.SpanText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Drift raw", Binding = new Binding(nameof(AnalogValidationMarkerRow.DriftText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Время кадра",
            Binding = new Binding(nameof(AnalogValidationMarkerRow.TimestampText)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddMarkerFormCell(form, new TextBlock
        {
            Text = "Сигнал:",
            Margin = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center
        }, 0, 0);
        Grid.SetRow(signalCombo, 0);
        Grid.SetColumn(signalCombo, 1);
        Grid.SetColumnSpan(signalCombo, 4);
        form.Children.Add(signalCombo);

        AddMarkerFormCell(form, new TextBlock
        {
            Text = "Measured physical:",
            Margin = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center
        }, 1, 0);
        Grid.SetRow(physicalBox, 1);
        Grid.SetColumn(physicalBox, 1);
        form.Children.Add(physicalBox);
        Grid.SetRow(unitText, 1);
        Grid.SetColumn(unitText, 2);
        form.Children.Add(unitText);
        Grid.SetRow(capture, 1);
        Grid.SetColumn(capture, 4);
        form.Children.Add(capture);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(remove);
        buttons.Children.Add(validate);
        buttons.Children.Add(recordEvidence);
        buttons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(instructions, 0); layout.Children.Add(instructions);
        Grid.SetRow(form, 1); layout.Children.Add(form);
        Grid.SetRow(formulaStatus, 2); layout.Children.Add(formulaStatus);
        Grid.SetRow(sourceStatus, 3); layout.Children.Add(sourceStatus);
        Grid.SetRow(grid, 4); layout.Children.Add(grid);
        Grid.SetRow(preview, 5); layout.Children.Add(preview);
        Grid.SetRow(buttons, 6); layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — Calibration Validation Run",
            Width = 1320,
            Height = 850,
            MinWidth = 1020,
            MinHeight = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        void RefreshSelection()
        {
            points.Clear();
            validate.IsEnabled = false;
            recordEvidence.IsEnabled = false;
            preview.Text = "Зафиксируйте минимум три независимые физические точки.";
            sourceStatus.Text = string.Empty;
            if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice choice)
                return;

            unitText.Text = choice.Signal.Unit;
            formulaStatus.Text =
                $"Проверяется без refit: physical = raw × {FormatCalibrationNumber(choice.Signal.Scale)} + " +
                $"{FormatCalibrationNumber(choice.Signal.Offset)} {choice.Signal.Unit}; " +
                $"{FormatCalibrationField(choice.Signal)}; Confidence={choice.Signal.Confidence}.";
        }

        AnalogCalibrationValidationResult EvaluateCurrent()
        {
            if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice selected)
                throw new InvalidOperationException("Выберите Machine Profile signal.");
            var current = FindCurrentProfileSignal(selected.Signal.SignalId)
                ?? throw new InvalidOperationException("Выбранный сигнал больше не найден в текущем Machine Profile.");
            var validationPoints = points.Select(row => new AnalogCalibrationValidationPoint(
                row.Capture.RawValue,
                row.MeasuredPhysicalValue,
                $"validation-{row.Number}",
                row.Source,
                row.Capture.LastSampleTimestamp)).ToArray();
            return AnalogCalibrationValidation.Evaluate(current, validationPoints);
        }

        signalCombo.SelectionChanged += (_, _) => RefreshSelection();
        RefreshSelection();

        grid.SelectionChanged += (_, _) =>
            remove.IsEnabled = grid.SelectedItem is AnalogValidationMarkerRow;
        remove.Click += (_, _) =>
        {
            if (grid.SelectedItem is not AnalogValidationMarkerRow row)
                return;
            points.Remove(row);
            RenumberValidationPoints(points);
            validate.IsEnabled = points.Count >= AnalogCalibrationValidation.MinimumValidationPoints;
            recordEvidence.IsEnabled = false;
            preview.Text = points.Count >= AnalogCalibrationValidation.MinimumValidationPoints
                ? "Набор validation points изменён. Нажмите «Проверить калибровку»."
                : "Зафиксируйте минимум три независимые физические точки.";
        };

        capture.Click += (_, _) =>
        {
            try
            {
                if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice selected)
                    throw new InvalidOperationException("Выберите Machine Profile signal.");
                var current = FindCurrentProfileSignal(selected.Signal.SignalId)
                    ?? throw new InvalidOperationException("Выбранный сигнал больше не найден в текущем Machine Profile.");
                if (string.IsNullOrWhiteSpace(current.Unit))
                    throw new InvalidOperationException("У выбранного сигнала не задана Unit. Сначала выполните калибровку.");

                var measured = ParseMarkerPhysicalValue(physicalBox.Text);
                if (_liveReceiver is null || _liveDriver is null)
                {
                    throw new InvalidOperationException(
                        "Live/Replay receiver ещё не создан. Подключите PCAN в LISTEN ONLY или запустите Replay.");
                }

                var replay = _liveDriver is ReplayCanDriver;
                if (!replay && (!_liveConnectionReady || !_liveReceiver.IsReceiving))
                {
                    throw new InvalidOperationException(
                        "PCAN Live сейчас не принимает данные. Подключите канал в LISTEN ONLY и повторите фиксацию.");
                }

                var buffer = _liveReceiver.Buffer.Snapshot();
                if (buffer.Count == 0)
                    throw new InvalidOperationException("Live/Replay buffer пока пуст. Дождитесь принимаемых кадров.");

                var captured = AnalogPhysicalMarkerCapture.Capture(
                    current,
                    buffer,
                    new AnalogMarkerCaptureOptions
                    {
                        RequireRecentFrame = !replay,
                        ReferenceTime = replay ? null : DateTimeOffset.UtcNow,
                        MaximumFrameAge = TimeSpan.FromSeconds(2),
                        SamplingWindow = TimeSpan.FromMilliseconds(750),
                        MinimumSamples = 5
                    });
                if (!captured.IsStable)
                {
                    MessageBox.Show(
                        window,
                        captured.StabilityMessage + "\n\nValidation point НЕ добавлена.",
                        "Raw ещё нестабилен",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    sourceStatus.Text = captured.StabilityMessage;
                    return;
                }

                var predicted = captured.RawValue * current.Scale + current.Offset;
                if (!double.IsFinite(predicted))
                    throw new OverflowException("CAN prediction вышел за диапазон double.");
                var source = replay ? "REPLAY" : "PCAN LIVE";
                points.Add(new AnalogValidationMarkerRow(
                    points.Count + 1,
                    measured,
                    predicted,
                    current.Unit,
                    source,
                    current.IsExtended,
                    captured));
                validate.IsEnabled = points.Count >= AnalogCalibrationValidation.MinimumValidationPoints;
                recordEvidence.IsEnabled = false;
                preview.Text = validate.IsEnabled
                    ? "Точка добавлена. После набора независимых положений нажмите «Проверить калибровку»."
                    : $"Точка добавлена. Нужно ещё {AnalogCalibrationValidation.MinimumValidationPoints - points.Count} независимых положения.";
                sourceStatus.Text =
                    $"{source}: raw={FormatCalibrationNumber(captured.RawValue)}, " +
                    $"measured={FormatCalibrationNumber(measured)} {current.Unit}, " +
                    $"predicted={FormatCalibrationNumber(predicted)} {current.Unit}, " +
                    $"error={FormatValidationSigned(predicted - measured)} {current.Unit}; {captured.StabilityMessage}";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    window,
                    FormatException(exception),
                    "Calibration Validation Run",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        validate.Click += (_, _) =>
        {
            try
            {
                var result = EvaluateCurrent();
                preview.Text = FormatAnalogValidationPreview(result);
                recordEvidence.IsEnabled = result.CanRecordEvidence;
            }
            catch (Exception exception)
            {
                recordEvidence.IsEnabled = false;
                preview.Text = "Ошибка: " + FormatException(exception);
            }
        };

        recordEvidence.Click += (_, _) =>
        {
            try
            {
                if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice selected)
                    throw new InvalidOperationException("Выберите Machine Profile signal.");
                var current = FindCurrentProfileSignal(selected.Signal.SignalId)
                    ?? throw new InvalidOperationException("Выбранный сигнал больше не найден в текущем Machine Profile.");
                var result = EvaluateCurrent();
                if (!result.CanRecordEvidence)
                {
                    throw new InvalidOperationException(
                        "PASS evidence доступно только после успешной независимой проверки минимум по трём точкам.");
                }

                var confirmation =
                    $"Сигнал: {current.Name}\n" +
                    $"Проверено независимых точек: {result.Points.Count}\n" +
                    $"Текущие Scale/Offset: {FormatCalibrationNumber(current.Scale)} / {FormatCalibrationNumber(current.Offset)}\n" +
                    $"RMSE: {FormatCalibrationNumber(result.RootMeanSquareError)} {result.Unit} " +
                    $"({FormatCalibrationNumber(result.RmsePercentOfSpan)}% span)\n" +
                    $"Max error: {FormatCalibrationNumber(result.MaximumAbsoluteError)} {result.Unit} " +
                    $"({FormatCalibrationNumber(result.MaximumErrorPercentOfSpan)}% span)\n" +
                    $"Результат: {result.Quality}\n\n" +
                    "Scale/Offset/Unit НЕ изменятся. Confidence НЕ изменится. " +
                    "Будет добавлено только PhysicalOutputCheck evidence.\n\nЗаписать PASS evidence?";
                if (MessageBox.Show(
                        window,
                        confirmation,
                        "Подтверждение Calibration Validation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                var updated = AnalogCalibrationValidation.AddEvidence(
                    current,
                    result,
                    DateTimeOffset.UtcNow);
                if (!ReplaceProfileSignal(updated))
                    throw new InvalidOperationException("Не удалось заменить сигнал в Machine Profile.");
                ApplyProfileToFields();
                StatusText.Text =
                    $"Calibration Validation PASS для «{updated.Name}»: RMSE={FormatCalibrationNumber(result.RootMeanSquareError)} {result.Unit}, " +
                    $"max error={FormatCalibrationNumber(result.MaximumAbsoluteError)} {result.Unit}. " +
                    $"Scale/Offset и Confidence={updated.Confidence} не изменены. Профиль ещё не сохранён. CAN Tx отсутствует.";
                MessageBox.Show(
                    window,
                    "PASS validation evidence добавлено в текущий Machine Profile.\n\n" +
                    "Scale/Offset/Unit и Confidence не менялись. Для постоянного хранения сохраните Machine Profile явно.",
                    "Calibration Validation Run",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                window.Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    window,
                    FormatException(exception),
                    "Calibration Validation Run",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string FormatAnalogValidationPreview(AnalogCalibrationValidationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("VALIDATION ONLY — Scale/Offset were NOT refitted");
        builder.AppendLine($"Result            = {result.Quality}");
        builder.AppendLine($"Scale evaluated   = {FormatCalibrationNumber(result.EvaluatedScale)}");
        builder.AppendLine($"Offset evaluated  = {FormatCalibrationNumber(result.EvaluatedOffset)} {result.Unit}");
        builder.AppendLine($"Independent points= {result.Points.Count}");
        builder.AppendLine($"Validation span   = {FormatCalibrationNumber(result.PhysicalSpan)} {result.Unit}");
        builder.AppendLine($"Mean error        = {FormatValidationSigned(result.MeanError)} {result.Unit}");
        builder.AppendLine($"RMSE              = {FormatCalibrationNumber(result.RootMeanSquareError)} {result.Unit} ({FormatCalibrationNumber(result.RmsePercentOfSpan)}% span)");
        builder.AppendLine($"Max absolute error= {FormatCalibrationNumber(result.MaximumAbsoluteError)} {result.Unit} ({FormatCalibrationNumber(result.MaximumErrorPercentOfSpan)}% span)");
        builder.AppendLine();
        builder.AppendLine("raw\tmeasured\tpredicted\terror");
        foreach (var item in result.Residuals)
        {
            builder.Append(FormatCalibrationNumber(item.RawValue)).Append('\t')
                .Append(FormatCalibrationNumber(item.MeasuredPhysicalValue)).Append('\t')
                .Append(FormatCalibrationNumber(item.PredictedPhysicalValue)).Append('\t')
                .Append(FormatValidationSigned(item.Error)).AppendLine();
        }

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Notes / warnings:");
            foreach (var warning in result.Warnings)
                builder.Append("- ").AppendLine(warning);
        }

        if (!result.CanRecordEvidence)
        {
            builder.AppendLine();
            builder.AppendLine("PASS EVIDENCE BLOCKED: результат должен быть PASS по минимум трём независимым точкам.");
        }

        return builder.ToString();
    }

    private static void RenumberValidationPoints(ObservableCollection<AnalogValidationMarkerRow> points)
    {
        for (var index = 0; index < points.Count; index++)
            points[index] = points[index] with { Number = index + 1 };
    }

    private static string FormatValidationSigned(double value) =>
        value.ToString(value >= 0 ? "+0.#########" : "0.#########", CultureInfo.InvariantCulture);

    private sealed record AnalogValidationMarkerRow(
        int Number,
        double MeasuredPhysicalValue,
        double PredictedPhysicalValue,
        string Unit,
        string Source,
        bool IsExtended,
        AnalogMarkerCaptureResult Capture)
    {
        public string RawText => FormatCalibrationNumber(Capture.RawValue);
        public string MeasuredText => FormatCalibrationNumber(MeasuredPhysicalValue);
        public string PredictedText => FormatCalibrationNumber(PredictedPhysicalValue);
        public string ErrorText => FormatValidationSigned(PredictedPhysicalValue - MeasuredPhysicalValue);
        public string IdText => Capture.LatestObservedCanId.ToString(IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture);
        public int SampleCount => Capture.SampleCount;
        public string SpanText => FormatCalibrationNumber(Capture.RobustSpanRaw);
        public string DriftText => FormatCalibrationNumber(Capture.DriftRaw);
        public string TimestampText => Capture.LastSampleTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
    }
}
