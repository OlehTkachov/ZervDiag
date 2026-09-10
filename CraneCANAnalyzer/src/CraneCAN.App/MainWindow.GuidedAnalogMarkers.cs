using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Profiles;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _guidedAnalogMarkersButton;

    [ModuleInitializer]
    internal static void RegisterGuidedAnalogMarkersUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(GuidedAnalogMarkersMainWindowLoaded));
    }

    private static void GuidedAnalogMarkersMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window ||
            window.AnalyzeGuidedButton.Parent is not Panel panel)
            return;

        if (panel.Children.OfType<Button>().Any(button =>
                string.Equals(button.Tag as string, "guided-analog-markers", StringComparison.Ordinal)))
            return;

        var button = new Button
        {
            Content = "Физические точки…",
            Tag = "guided-analog-markers",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip =
                "Зафиксировать raw выбранного Machine Profile signal прямо из Live/Replay buffer " +
                "в известном физическом положении и рассчитать Scale/Offset. Только Rx; CAN Tx отсутствует."
        };
        button.Click += window.GuidedAnalogMarkersButton_Click;

        var calibration = panel.Children.OfType<Button>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                "guided-analog-calibration",
                StringComparison.Ordinal));
        var index = calibration is null
            ? panel.Children.IndexOf(window.AnalyzeGuidedButton)
            : panel.Children.IndexOf(calibration);
        if (index < 0) panel.Children.Add(button);
        else panel.Children.Insert(index, button);
        window._guidedAnalogMarkersButton = button;
    }

    private void GuidedAnalogMarkersButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateProfileFromFields();
            var choices = BuildAnalogCalibrationChoices();
            if (choices.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "В Machine Profile нет сигнала для физической фиксации.\n\n" +
                    "Сначала добавьте raw-кандидат через «Аналоговые сигналы…» или откройте профиль с сигналом.",
                    "Physical Marker Capture",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowAnalogMarkerWindow(choices);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                FormatException(exception),
                "Physical Marker Capture",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowAnalogMarkerWindow(IReadOnlyList<AnalogCalibrationSignalChoice> choices)
    {
        var markers = new ObservableCollection<AnalogMarkerRow>();
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
            Width = 120,
            Margin = new Thickness(4)
        };
        var physicalBox = new TextBox
        {
            Width = 160,
            Margin = new Thickness(4),
            ToolTip = "Реально измеренное физическое значение в указанной Unit, например 12.5."
        };
        var capture = new Button
        {
            Content = "Зафиксировать точку из Live/Replay",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            ToolTip =
                "Берёт несколько последних Rx кадров выбранного сигнала, проверяет стабильность и сохраняет median raw. CAN Tx отсутствует."
        };
        var remove = new Button
        {
            Content = "Удалить точку",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(4),
            IsEnabled = false
        };
        var calculate = new Button
        {
            Content = "Рассчитать",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = false
        };
        var apply = new Button
        {
            Content = "Применить Scale/Offset/Unit",
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

        var instructions = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text =
                "1) Выберите сигнал. 2) Установите механизм в известное физическое положение и удерживайте его. " +
                "3) Введите физическое значение и Unit. 4) Нажмите «Зафиксировать точку».\n" +
                "CraneCAN использует median последних Rx кадров и блокирует точку, если raw продолжает заметно двигаться. " +
                "Для PCAN требуется свежий Live-поток; Replay использует текущий/последний buffer выбранной записи. " +
                "Для проверки линейности рекомендуется минимум 3 точки по рабочему диапазону."
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
            MinHeight = 190,
            Margin = new Thickness(4),
            Text = "Зафиксируйте минимум две разные физические точки."
        };

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = markers,
            MinHeight = 190,
            Margin = new Thickness(4)
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(AnalogMarkerRow.Number)), Width = 42 });
        grid.Columns.Add(new DataGridTextColumn { Header = "raw median", Binding = new Binding(nameof(AnalogMarkerRow.RawText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "physical", Binding = new Binding(nameof(AnalogMarkerRow.PhysicalText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Unit", Binding = new Binding(nameof(AnalogMarkerRow.Unit)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Источник", Binding = new Binding(nameof(AnalogMarkerRow.Source)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(AnalogMarkerRow.IdText)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Samples", Binding = new Binding(nameof(AnalogMarkerRow.SampleCount)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Span raw", Binding = new Binding(nameof(AnalogMarkerRow.SpanText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Drift raw", Binding = new Binding(nameof(AnalogMarkerRow.DriftText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Время кадра", Binding = new Binding(nameof(AnalogMarkerRow.TimestampText)), Width = 190 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "J1939",
            Binding = new Binding(nameof(AnalogMarkerRow.J1939Text)),
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

        AddMarkerFormCell(form, new TextBlock { Text = "Сигнал:", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Grid.SetRow(signalCombo, 0); Grid.SetColumn(signalCombo, 1); Grid.SetColumnSpan(signalCombo, 4); form.Children.Add(signalCombo);
        AddMarkerFormCell(form, new TextBlock { Text = "Physical:", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Grid.SetRow(physicalBox, 1); Grid.SetColumn(physicalBox, 1); form.Children.Add(physicalBox);
        AddMarkerFormCell(form, new TextBlock { Text = "Unit:", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }, 1, 2);
        Grid.SetRow(unitCombo, 1); Grid.SetColumn(unitCombo, 3); form.Children.Add(unitCombo);
        Grid.SetRow(capture, 1); Grid.SetColumn(capture, 4); form.Children.Add(capture);

        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightButtons.Children.Add(remove);
        rightButtons.Children.Add(calculate);
        rightButtons.Children.Add(apply);
        rightButtons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(instructions, 0); layout.Children.Add(instructions);
        Grid.SetRow(form, 1); layout.Children.Add(form);
        Grid.SetRow(sourceStatus, 2); layout.Children.Add(sourceStatus);
        Grid.SetRow(grid, 3); layout.Children.Add(grid);
        Grid.SetRow(preview, 4); layout.Children.Add(preview);
        Grid.SetRow(rightButtons, 5); layout.Children.Add(rightButtons);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — Physical Marker Capture",
            Width = 1280,
            Height = 820,
            MinWidth = 980,
            MinHeight = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        void RefreshSelection()
        {
            markers.Clear();
            apply.IsEnabled = false;
            calculate.IsEnabled = false;
            preview.Text = "Зафиксируйте минимум две разные физические точки.";
            if (signalCombo.SelectedItem is AnalogCalibrationSignalChoice choice)
            {
                unitCombo.Text = choice.Signal.Unit;
                sourceStatus.Text =
                    $"{FormatCalibrationField(choice.Signal)}; signed={choice.Signal.IsSigned}; " +
                    $"Confidence={choice.Signal.Confidence}. Точки привязаны только к этому SignalId и очищаются при смене сигнала.";
            }
        }

        AnalogCalibrationResult CalculateMarkers()
        {
            var unit = unitCombo.Text;
            var points = markers.Select(row => new AnalogCalibrationPoint(
                row.Capture.RawValue,
                row.PhysicalValue,
                $"{row.Source}@{row.Capture.LastSampleTimestamp:O}"))
                .ToArray();
            return AnalogPhysicalCalibration.Fit(points, unit);
        }

        signalCombo.SelectionChanged += (_, _) => RefreshSelection();
        RefreshSelection();

        grid.SelectionChanged += (_, _) => remove.IsEnabled = grid.SelectedItem is AnalogMarkerRow;
        remove.Click += (_, _) =>
        {
            if (grid.SelectedItem is not AnalogMarkerRow row)
                return;
            markers.Remove(row);
            RenumberMarkers(markers);
            calculate.IsEnabled = markers.Count >= 2;
            apply.IsEnabled = false;
            preview.Text = markers.Count >= 2
                ? "Набор точек изменён. Нажмите «Рассчитать»."
                : "Зафиксируйте минимум две разные физические точки.";
        };

        capture.Click += (_, _) =>
        {
            try
            {
                if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice selected)
                    throw new InvalidOperationException("Выберите Machine Profile signal.");
                var current = FindCurrentProfileSignal(selected.Signal.SignalId)
                    ?? throw new InvalidOperationException("Выбранный сигнал больше не найден в текущем Machine Profile.");
                var physical = ParseMarkerPhysicalValue(physicalBox.Text);
                if (_liveReceiver is null || _liveDriver is null)
                    throw new InvalidOperationException(
                        "Live/Replay receiver ещё не создан. Подключите PCAN в LISTEN ONLY или запустите Replay, затем повторите фиксацию.");

                var replay = _liveDriver is ReplayCanDriver;
                if (!replay && (!_liveConnectionReady || !_liveReceiver.IsReceiving))
                    throw new InvalidOperationException(
                        "PCAN Live сейчас не принимает данные. Подключите канал в LISTEN ONLY и повторите фиксацию.");

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
                        captured.StabilityMessage + "\n\nТочка НЕ добавлена в калибровку.",
                        "Raw ещё нестабилен",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    sourceStatus.Text = captured.StabilityMessage;
                    return;
                }

                var unit = string.IsNullOrWhiteSpace(unitCombo.Text) ? "<not set>" : unitCombo.Text.Trim();
                var source = replay ? "REPLAY" : "PCAN LIVE";
                markers.Add(new AnalogMarkerRow(
                    markers.Count + 1,
                    physical,
                    unit,
                    source,
                    captured));
                calculate.IsEnabled = markers.Count >= 2;
                apply.IsEnabled = false;
                preview.Text = markers.Count >= 2
                    ? "Точка добавлена. Нажмите «Рассчитать» после набора нужных положений."
                    : "Точка добавлена. Для Scale/Offset нужна ещё минимум одна физически отличающаяся точка.";
                sourceStatus.Text =
                    $"{source}: raw={FormatCalibrationNumber(captured.RawValue)}, samples={captured.SampleCount}, " +
                    $"ID={captured.LatestObservedCanId:X8}; {captured.StabilityMessage}";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    window,
                    FormatException(exception),
                    "Physical Marker Capture",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        calculate.Click += (_, _) =>
        {
            try
            {
                var result = CalculateMarkers();
                preview.Text = FormatAnalogCalibrationPreview(result);
                apply.IsEnabled = result.CanApply;
            }
            catch (Exception exception)
            {
                apply.IsEnabled = false;
                preview.Text = "Ошибка: " + FormatException(exception);
            }
        };

        apply.Click += (_, _) =>
        {
            try
            {
                if (signalCombo.SelectedItem is not AnalogCalibrationSignalChoice selected)
                    throw new InvalidOperationException("Выберите Machine Profile signal.");
                var result = CalculateMarkers();
                if (!result.CanApply)
                    throw new InvalidOperationException("Качество калибровки POOR. Запись в профиль заблокирована.");
                var current = FindCurrentProfileSignal(selected.Signal.SignalId)
                    ?? throw new InvalidOperationException("Выбранный сигнал больше не найден в текущем Machine Profile.");

                var confirmation =
                    $"Сигнал: {current.Name}\n" +
                    $"Поле: {FormatCalibrationField(current)}\n" +
                    $"Физических точек: {result.Points.Count}\n" +
                    $"Scale={FormatCalibrationNumber(result.Scale)}\n" +
                    $"Offset={FormatCalibrationNumber(result.Offset)}\n" +
                    $"Unit={result.Unit}\n" +
                    $"Quality={result.Quality}; R²={FormatCalibrationNumber(result.RSquared)}; " +
                    $"max error={FormatCalibrationNumber(result.MaximumAbsoluteError)} {result.Unit}.\n\n" +
                    $"Confidence останется {current.Confidence}. Профиль не сохраняется на диск автоматически.\n\nПрименить?";
                if (MessageBox.Show(
                        window,
                        confirmation,
                        "Подтверждение Physical Marker Calibration",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                var updated = AnalogPhysicalCalibration.ApplyToSignal(current, result, DateTimeOffset.UtcNow);
                if (!ReplaceProfileSignal(updated))
                    throw new InvalidOperationException("Не удалось заменить сигнал в Machine Profile.");
                ApplyProfileToFields();
                StatusText.Text =
                    $"Physical Marker Calibration применена к «{updated.Name}»: Scale={FormatCalibrationNumber(updated.Scale)}, " +
                    $"Offset={FormatCalibrationNumber(updated.Offset)}, Unit={updated.Unit}; Confidence={updated.Confidence}. " +
                    "Профиль ещё не сохранён. CAN Tx отсутствует.";
                MessageBox.Show(
                    window,
                    "Scale/Offset/Unit записаны в текущий Machine Profile. Confidence не повышен автоматически.\n" +
                    "Для постоянного хранения сохраните Machine Profile явно.",
                    "Physical Marker Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                window.Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    window,
                    FormatException(exception),
                    "Physical Marker Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static void AddMarkerFormCell(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static void RenumberMarkers(ObservableCollection<AnalogMarkerRow> markers)
    {
        for (var index = 0; index < markers.Count; index++)
            markers[index] = markers[index] with { Number = index + 1 };
    }

    private static double ParseMarkerPhysicalValue(string text)
    {
        var token = (text ?? string.Empty).Trim();
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            return value;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value))
            return value;
        if (token.Contains(',') && !token.Contains('.') &&
            double.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value))
            return value;
        throw new FormatException("Введите конечное физическое значение, например 12.5.");
    }

    private sealed record AnalogMarkerRow(
        int Number,
        double PhysicalValue,
        string Unit,
        string Source,
        AnalogMarkerCaptureResult Capture)
    {
        public string RawText => FormatCalibrationNumber(Capture.RawValue);
        public string PhysicalText => FormatCalibrationNumber(PhysicalValue);
        public string IdText => Capture.LatestObservedCanId.ToString("X8", CultureInfo.InvariantCulture);
        public int SampleCount => Capture.SampleCount;
        public string SpanText => FormatCalibrationNumber(Capture.RobustSpanRaw);
        public string DriftText => FormatCalibrationNumber(Capture.DriftRaw);
        public string TimestampText => Capture.LastSampleTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        public string J1939Text => Capture.J1939Pgn.HasValue
            ? $"PGN 0x{Capture.J1939Pgn.Value:X5}; SA 0x{Capture.J1939SourceAddress.GetValueOrDefault():X2}" +
              (Capture.J1939DestinationAddress.HasValue
                  ? $"; DA 0x{Capture.J1939DestinationAddress.Value:X2}"
                  : string.Empty)
            : "—";
    }
}
