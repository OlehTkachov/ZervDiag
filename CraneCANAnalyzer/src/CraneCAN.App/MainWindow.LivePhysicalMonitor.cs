using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _livePhysicalMonitorButton;
    private Window? _livePhysicalMonitorWindow;

    [ModuleInitializer]
    internal static void RegisterLivePhysicalMonitorUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(LivePhysicalMonitorMainWindowLoaded));
    }

    private static void LivePhysicalMonitorMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window ||
            window.AnalyzeGuidedButton.Parent is not Panel panel)
            return;

        if (panel.Children.OfType<Button>().Any(button =>
                string.Equals(button.Tag as string, "live-physical-monitor", StringComparison.Ordinal)))
            return;

        var button = new Button
        {
            Content = "Физический монитор…",
            Tag = "live-physical-monitor",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip =
                "Пассивный монитор выбранных Machine Profile signals в инженерных единицах: " +
                "physical, raw, min/max, скорость изменения и STALE. Только уже принятые Rx CAN; Tx отсутствует."
        };
        button.Click += window.LivePhysicalMonitorButton_Click;

        var validationButton = panel.Children.OfType<Button>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                "guided-analog-validation",
                StringComparison.Ordinal));
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

        var index = validationButton is not null
            ? panel.Children.IndexOf(validationButton) + 1
            : markerButton is not null
                ? panel.Children.IndexOf(markerButton) + 1
                : calibrationButton is not null
                    ? panel.Children.IndexOf(calibrationButton)
                    : panel.Children.IndexOf(window.AnalyzeGuidedButton);
        if (index < 0 || index > panel.Children.Count) panel.Children.Add(button);
        else panel.Children.Insert(index, button);
        window._livePhysicalMonitorButton = button;
    }

    private void LivePhysicalMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_livePhysicalMonitorWindow is { IsVisible: true } existing)
            {
                existing.Activate();
                return;
            }

            UpdateProfileFromFields();
            var choices = BuildAnalogCalibrationChoices()
                .Where(choice => !string.IsNullOrWhiteSpace(choice.Signal.Unit))
                .ToArray();
            if (choices.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "В Machine Profile нет сигналов с инженерной Unit.\n\n" +
                    "Сначала выполните физическую калибровку или импортируйте проверенное описание сигнала. " +
                    "Монитор не угадывает Scale/Offset/Unit.",
                    "Live Physical Signal Monitor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowLivePhysicalMonitorWindow(choices);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                FormatException(exception),
                "Live Physical Signal Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowLivePhysicalMonitorWindow(IReadOnlyList<AnalogCalibrationSignalChoice> choices)
    {
        var instructions = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Выберите один или несколько сигналов. Значение Physical рассчитывается только из Scale/Offset/Unit текущего Machine Profile. " +
                "Min/Max и скорость относятся к последним 5 секундам данных выбранного sender; STALE означает, что свежего кадра нет более 2 секунд. " +
                "Для J1939 один PGN может иметь меняющийся Source Address, но статистика разных SA не смешивается.\n" +
                "VALIDATED PASS / CALIBRATED в колонке Evidence — подсказка о происхождении физической шкалы, а не доказательство причинности. CAN Tx отсутствует."
        };

        var selector = new ListBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(AnalogCalibrationSignalChoice.DisplayText),
            SelectionMode = SelectionMode.Extended,
            MinHeight = 95,
            MaxHeight = 150,
            Margin = new Thickness(4)
        };

        var selectAll = new Button
        {
            Content = "Выбрать все",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4)
        };
        var clearSelection = new Button
        {
            Content = "Очистить выбор",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4)
        };
        var pause = new CheckBox
        {
            Content = "Пауза отображения",
            Margin = new Thickness(12, 4, 4, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };

        var sourceStatus = new TextBlock
        {
            Margin = new Thickness(4, 4, 4, 8),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        };

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            Margin = new Thickness(4),
            MinHeight = 360
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding(nameof(LivePhysicalMonitorRow.State)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Signal", Binding = new Binding(nameof(LivePhysicalMonitorRow.Signal)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Physical", Binding = new Binding(nameof(LivePhysicalMonitorRow.Physical)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Unit", Binding = new Binding(nameof(LivePhysicalMonitorRow.Unit)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "raw", Binding = new Binding(nameof(LivePhysicalMonitorRow.Raw)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Min 5s", Binding = new Binding(nameof(LivePhysicalMonitorRow.Minimum)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Max 5s", Binding = new Binding(nameof(LivePhysicalMonitorRow.Maximum)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Rate /s", Binding = new Binding(nameof(LivePhysicalMonitorRow.Rate)), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Age", Binding = new Binding(nameof(LivePhysicalMonitorRow.Age)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Samples", Binding = new Binding(nameof(LivePhysicalMonitorRow.Samples)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(LivePhysicalMonitorRow.Id)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "J1939", Binding = new Binding(nameof(LivePhysicalMonitorRow.J1939)), Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Evidence", Binding = new Binding(nameof(LivePhysicalMonitorRow.Evidence)), Width = 135 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Confidence", Binding = new Binding(nameof(LivePhysicalMonitorRow.Confidence)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Detail",
            Binding = new Binding(nameof(LivePhysicalMonitorRow.Detail)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var selectorButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        selectorButtons.Children.Add(selectAll);
        selectorButtons.Children.Add(clearSelection);
        selectorButtons.Children.Add(pause);

        var bottomButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        bottomButtons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(instructions, 0); layout.Children.Add(instructions);
        Grid.SetRow(selector, 1); layout.Children.Add(selector);
        Grid.SetRow(selectorButtons, 2); layout.Children.Add(selectorButtons);
        Grid.SetRow(sourceStatus, 3); layout.Children.Add(sourceStatus);
        Grid.SetRow(grid, 4); layout.Children.Add(grid);
        Grid.SetRow(bottomButtons, 5); layout.Children.Add(bottomButtons);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — Live Physical Signal Monitor",
            Width = 1500,
            Height = 760,
            MinWidth = 1100,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        _livePhysicalMonitorWindow = window;

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        void Refresh()
        {
            if (pause.IsChecked == true)
                return;

            var selected = selector.SelectedItems
                .Cast<AnalogCalibrationSignalChoice>()
                .ToArray();
            if (selected.Length == 0)
            {
                grid.ItemsSource = Array.Empty<LivePhysicalMonitorRow>();
                sourceStatus.Text = "Сигналы не выбраны. CAN Tx отсутствует.";
                return;
            }

            var receiver = _liveReceiver;
            if (receiver is null)
            {
                grid.ItemsSource = selected.Select(choice =>
                    LivePhysicalMonitorRow.NoSource(choice.Signal, PhysicalMonitorEvidence(choice.Signal)))
                    .ToArray();
                sourceStatus.Text =
                    "CAN source не запущен. Подключите PCAN в LISTEN ONLY или запустите Replay. Окно можно оставить открытым; передача CAN отсутствует.";
                return;
            }

            var buffer = receiver.Buffer.Snapshot();
            var replay = _liveDriver is ReplayCanDriver;
            var referenceTime = replay && buffer.Count > 0
                ? buffer.Max(frame => frame.Timestamp)
                : DateTimeOffset.UtcNow;
            var transportRunning = receiver.IsReceiving;
            var status = receiver.DriverStatus;
            sourceStatus.Text = replay
                ? $"REPLAY: {(transportRunning ? "RUNNING" : "STOPPED / последнее значение заморожено")}; buffer={buffer.Count:N0}; " +
                  $"reference={referenceTime:HH:mm:ss.fff}. CAN Tx отсутствует."
                : $"PCAN LIVE: {(transportRunning && _liveConnectionReady ? "RECEIVING" : "STOPPED")}; " +
                  $"LISTEN ONLY={(status.ListenOnlyConfirmed ? "CONFIRMED" : "NOT CONFIRMED")}; buffer={buffer.Count:N0}; " +
                  $"lost={status.LostFrames:N0}; errors={status.ErrorFrames:N0}. CAN Tx отсутствует.";

            var rows = new List<LivePhysicalMonitorRow>(selected.Length);
            foreach (var choice in selected)
            {
                var current = FindCurrentProfileSignal(choice.Signal.SignalId);
                if (current is null)
                {
                    rows.Add(LivePhysicalMonitorRow.Removed(choice.Signal));
                    continue;
                }

                try
                {
                    var reading = LivePhysicalSignalMonitor.Evaluate(
                        current,
                        buffer,
                        referenceTime,
                        new LivePhysicalSignalMonitorOptions
                        {
                            StatisticsWindow = TimeSpan.FromSeconds(5),
                            StaleAfter = TimeSpan.FromSeconds(2),
                            MinimumRateSpan = TimeSpan.FromMilliseconds(200)
                        });
                    rows.Add(LivePhysicalMonitorRow.From(
                        current,
                        reading,
                        PhysicalMonitorEvidence(current),
                        transportRunning,
                        replay));
                }
                catch (Exception exception)
                {
                    rows.Add(LivePhysicalMonitorRow.Error(
                        current,
                        PhysicalMonitorEvidence(current),
                        FormatException(exception)));
                }
            }

            grid.ItemsSource = rows;
        }

        selectAll.Click += (_, _) => selector.SelectAll();
        clearSelection.Click += (_, _) => selector.UnselectAll();
        selector.SelectionChanged += (_, _) => Refresh();
        timer.Tick += (_, _) => Refresh();
        close.Click += (_, _) => window.Close();
        window.Loaded += (_, _) =>
        {
            selector.SelectAll();
            Refresh();
            timer.Start();
        };
        window.Closed += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_livePhysicalMonitorWindow, window))
                _livePhysicalMonitorWindow = null;
        };

        window.Show();
    }

    private static string PhysicalMonitorEvidence(MachineSignal signal)
    {
        if (signal.Evidence.Any(evidence =>
                evidence.Kind == EvidenceKind.PhysicalOutputCheck &&
                string.Equals(evidence.CaptureOrigin, "analog-calibration-validation", StringComparison.OrdinalIgnoreCase)))
            return "VALIDATED PASS";

        if (signal.Evidence.Any(evidence =>
                evidence.Kind == EvidenceKind.PhysicalOutputCheck &&
                string.Equals(evidence.CaptureOrigin, "guided-analog-calibration", StringComparison.OrdinalIgnoreCase)))
            return "CALIBRATED";

        if (signal.Evidence.Any(evidence => evidence.Kind == EvidenceKind.PhysicalOutputCheck))
            return "PHYSICAL EVIDENCE";

        return "UNIT ONLY";
    }

    private sealed record LivePhysicalMonitorRow(
        string State,
        string Signal,
        string Physical,
        string Unit,
        string Raw,
        string Minimum,
        string Maximum,
        string Rate,
        string Age,
        int Samples,
        string Id,
        string J1939,
        string Evidence,
        string Confidence,
        string Detail)
    {
        public static LivePhysicalMonitorRow From(
            MachineSignal signal,
            LivePhysicalSignalReading reading,
            string evidence,
            bool transportRunning,
            bool replay)
        {
            var state = !transportRunning
                ? replay ? "REPLAY STOPPED" : "SOURCE STOPPED"
                : reading.State switch
                {
                    LivePhysicalSignalState.Fresh => "FRESH",
                    LivePhysicalSignalState.Stale => "STALE",
                    _ => "NO DATA"
                };
            var id = reading.LatestObservedCanId.HasValue
                ? reading.LatestObservedCanId.Value.ToString(signal.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)
                : "—";
            var j1939 = reading.J1939Pgn.HasValue
                ? $"PGN 0x{reading.J1939Pgn.Value:X5}; SA 0x{reading.J1939SourceAddress.GetValueOrDefault():X2}" +
                  (reading.J1939DestinationAddress.HasValue
                      ? $"; DA 0x{reading.J1939DestinationAddress.Value:X2}"
                      : string.Empty)
                : "—";
            var detail = reading.StatusMessage;
            if (reading.MatchingFramesWithShortDlc > 0)
                detail += $" short-DLC={reading.MatchingFramesWithShortDlc}.";

            return new LivePhysicalMonitorRow(
                state,
                signal.Name,
                FormatValue(reading.CurrentPhysical),
                signal.Unit,
                FormatValue(reading.CurrentRaw),
                FormatValue(reading.MinimumPhysical),
                FormatValue(reading.MaximumPhysical),
                FormatValue(reading.RatePerSecond),
                reading.Age.HasValue ? $"{reading.Age.Value.TotalMilliseconds:0} ms" : "—",
                reading.SampleCount,
                id,
                j1939,
                evidence,
                signal.Confidence.ToString().ToUpperInvariant(),
                detail);
        }

        public static LivePhysicalMonitorRow NoSource(MachineSignal signal, string evidence) => new(
            "NO SOURCE", signal.Name, "—", signal.Unit, "—", "—", "—", "—", "—", 0, "—", "—",
            evidence, signal.Confidence.ToString().ToUpperInvariant(), "CAN source не запущен.");

        public static LivePhysicalMonitorRow Removed(MachineSignal signal) => new(
            "REMOVED", signal.Name, "—", signal.Unit, "—", "—", "—", "—", "—", 0, "—", "—",
            "—", signal.Confidence.ToString().ToUpperInvariant(), "SignalId больше не найден в текущем Machine Profile.");

        public static LivePhysicalMonitorRow Error(MachineSignal signal, string evidence, string detail) => new(
            "ERROR", signal.Name, "—", signal.Unit, "—", "—", "—", "—", "—", 0, "—", "—",
            evidence, signal.Confidence.ToString().ToUpperInvariant(), detail);

        private static string FormatValue(double? value) =>
            value.HasValue && double.IsFinite(value.Value)
                ? value.Value.ToString("0.#########", CultureInfo.InvariantCulture)
                : "—";
    }
}
