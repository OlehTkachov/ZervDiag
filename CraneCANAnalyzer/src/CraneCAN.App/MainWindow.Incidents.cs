using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Live;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private PreFaultIncident? _retainedIncident;
    private IncidentSource? _retainedIncidentSource;
    private Guid? _savedIncidentId;

    private IncidentSource CurrentIncidentSource() => new(
        _liveDriver?.Id ?? "unknown",
        (LiveChannelCombo.SelectedItem as CraneCAN.Core.Drivers.CanChannelDescriptor)?.Id ?? "",
        IsReplaySource ? null : LiveBitrateCombo.SelectedItem as int?,
        IsReplaySource ? _liveReplayPath : null, _liveReceiver?.RawCapturePath,
        _liveReceiver?.DriverStatus.LostFrames ?? 0, _liveReceiver?.DriverStatus.ErrorFrames ?? 0,
        _liveReceiver?.DriverStatus.ListenOnlyConfirmed ?? false);

    private void RetainIncident()
    {
        if (_liveReceiver?.Incidents.LastIncident is not { } incident) return;
        if (_retainedIncident?.IncidentId == incident.IncidentId) return;
        _retainedIncident = incident;
        _retainedIncidentSource = CurrentIncidentSource();
    }

    private void UpdateIncidentDisplay()
    {
        RetainIncident();
        var active = _liveReceiver?.Incidents.IsCollecting == true;
        var receiving = _liveReceiver?.IsReceiving == true;
        var hasFrame = Interlocked.Read(ref _liveLastFrameUtcTicks) > 0;
        IncidentMarkButton.IsEnabled = receiving && hasFrame;
        IncidentSaveButton.IsEnabled = _retainedIncident is not null;
        IncidentStatusText.Text = active ? "Событие отмечено. Записываю 5 секунд после отметки…" :
            _retainedIncident is { } incident
                ? $"{(incident.Complete ? "Запись полная" : "Запись неполная / качество ограничено")}: {incident.Frames.Count} кадров. " +
                  (_savedIncidentId == incident.IncidentId ? "Сохранено." : "Нажмите «Сохранить запись события». ") +
                  IncidentQualityText(incident)
                : !receiving ? "Приём остановлен. Для отметки события запустите приём CAN или новый Replay."
                : !hasFrame ? "Ожидаю первый CAN-кадр для отметки события."
                : "Перед отметкой накопите 10 секунд CAN. Отметка привязана к последнему принятому кадру.";
        if (receiving && IsReplaySource && hasFrame && _liveReplayCoverage?.Start is { } start)
        {
            var now = new DateTimeOffset(Interlocked.Read(ref _liveLastFrameUtcTicks), TimeSpan.Zero);
            var end = start + _liveReplayCoverage.Available;
            // Guided Replay is stopped when its POST stage completes, even for a longer TRC.
            if (_liveSession is { State: not LiveExperimentState.Aborted, Boundaries: { } boundaries } && boundaries.PostActionEnd < end)
                end = boundaries.PostActionEnd;
            var remaining = Math.Max(0, (end - now).TotalSeconds);
            var elapsed = Math.Max(0, (now - start).TotalSeconds);
            IncidentStatusText.Text += $"\nReplay: прошло {elapsed:0.0} с; до конца приёма {remaining:0.0} с.";
            if (!active)
                IncidentStatusText.Text += remaining < 5
                    ? " Для новой отметки осталось меньше 5 с: окно после события будет неполным."
                    : elapsed < 10
                        ? " Предыстория пока короче 10 с."
                        : "По времени доступно окно −10 / +5 с; качество кадров проверяется отдельно.";
        }
    }

    private static string IncidentQualityText(PreFaultIncident incident) => string.Join(" ", incident.QualityCodes.Select(code => code switch
    {
        "SHORT_PREHISTORY" => "Предыстория короче 10 секунд.",
        "PREHISTORY_FRAME_LIMIT" or "FRAME_LIMIT" => "Достигнут лимит памяти записи.",
        "POST_INTERRUPTED" => "Поток остановлен до конца окна после события.",
        "REPLAY_ENDED" => "Исходный TRC закончился до конца окна после события.",
        "CAPTURE_UNCERTAIN" => "Есть ошибки или потери регистрации.",
        "OUT_OF_ORDER" => "Нарушен порядок timestamps.",
        _ => code
    }));

    private void IncidentMarkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_liveReceiver?.IsReceiving != true) return;
            RetainIncident();
            if (!_liveReceiver.Incidents.IsCollecting && _retainedIncident is { } previous && _savedIncidentId != previous.IncidentId)
            {
                MessageBox.Show("Сначала сохраните предыдущую запись события.", "Запись события");
                return;
            }
            _liveReceiver.Incidents.Mark(string.IsNullOrWhiteSpace(LiveActionCombo.Text) ? "Отметка оператора" : LiveActionCombo.Text);
            UpdateIncidentDisplay();
        }
        catch (Exception exception) { MessageBox.Show(FormatException(exception), "Запись события"); }
    }

    private async void IncidentSaveButton_Click(object sender, RoutedEventArgs e)
    {
        RetainIncident();
        if (_retainedIncident is not { } incident || _retainedIncidentSource is not { } source) return;
        var dialog = new OpenFolderDialog { Title = "Папка для записи события (TRC и метаданные)" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            IncidentSaveButton.IsEnabled = false;
            var path = await PreFaultIncidentCodec.SaveAsync(dialog.FolderName, incident, source);
            _savedIncidentId = incident.IncidentId;
            StatusText.Text = $"Событие сохранено: {path}. Его можно открыть кнопкой «Открыть .canincident…».";
        }
        catch (Exception exception) { MessageBox.Show(FormatException(exception), "Сохранение события"); }
        finally { UpdateIncidentDisplay(); }
    }

    private async void IncidentOpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть сохранённое событие CraneCAN",
            Filter = "CraneCAN incident (*.canincident)|*.canincident|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        LoadedIncidentPackage? package = null;
        SetBusy(true, "Проверка .canincident и связанного capture.trc…");
        try
        {
            package = await PreFaultIncidentCodec.LoadAsync(dialog.FileName);
            StatusText.Text = $"Открыто событие {package.Incident.IncidentId:N}: {package.Incident.Frames.Count:N0} кадров.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Открытие события",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Сохранённое событие не открыто.";
        }
        finally
        {
            SetBusy(false);
        }

        if (package is not null) ShowIncidentTimeline(package);
    }

    private void ShowIncidentTimeline(LoadedIncidentPackage package)
    {
        var incident = package.Incident;
        var quality = IncidentQualityText(incident);
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Incident: {incident.IncidentId:N}\n" +
                $"{(incident.Complete ? "Запись полная" : "Запись неполная / качество ограничено")} · " +
                $"{incident.Frames.Count:N0} кадров · источник: {package.Source.DriverId} / {package.Source.ChannelId}\n" +
                $"Качество: {(string.IsNullOrWhiteSpace(quality) ? "ограничений регистрации не выявлено." : quality)}\n" +
                package.Interpretation
        };

        var timeline = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            ItemsSource = BuildIncidentTimeline(incident)
        };
        timeline.Columns.Add(new DataGridTextColumn { Header = "От отметки", Binding = new System.Windows.Data.Binding(nameof(IncidentTimelineRow.RelativeText)), Width = 95 });
        timeline.Columns.Add(new DataGridTextColumn { Header = "Время", Binding = new System.Windows.Data.Binding(nameof(IncidentTimelineRow.TimestampText)), Width = 185 });
        timeline.Columns.Add(new DataGridTextColumn { Header = "Событие", Binding = new System.Windows.Data.Binding(nameof(IncidentTimelineRow.Kind)), Width = 150 });
        timeline.Columns.Add(new DataGridTextColumn { Header = "Детали", Binding = new System.Windows.Data.Binding(nameof(IncidentTimelineRow.Details)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var analyze = new Button
        {
            Content = "Анализ изменений у отметки",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var compareIncident = new Button
        {
            Content = "Сравнить с другим incident…",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            ToolTip = "Текущий incident = ЭТАЛОН / GOOD / BEFORE; выбранный второй = СРАВНЕНИЕ / FAULT / AFTER."
        };        var openCapture = new Button
        {
            Content = "Открыть capture.trc в анализаторе",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(analyze);
        buttons.Children.Add(compareIncident);
        buttons.Children.Add(openCapture);
        buttons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(timeline, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(summary);
        layout.Children.Add(timeline);
        layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — сохранённое событие",
            Width = 980,
            Height = 600,
            MinWidth = 760,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        close.Click += (_, _) => window.Close();
        analyze.Click += async (_, _) =>
        {
            analyze.IsEnabled = false;
            try { await AnalyzeIncidentTransitionAsync(package, window); }
            finally { analyze.IsEnabled = true; }
        };
        compareIncident.Click += async (_, _) =>
        {
            compareIncident.IsEnabled = false;
            try { await CompareIncidentWithAnotherAsync(package, window); }
            finally { compareIncident.IsEnabled = true; }
        };        openCapture.Click += async (_, _) =>
        {
            openCapture.IsEnabled = false;
            if (await OpenIncidentCaptureAsync(package)) window.Close();
            else openCapture.IsEnabled = true;
        };

        window.ShowDialog();
    }

    private static IReadOnlyList<IncidentTimelineRow> BuildIncidentTimeline(PreFaultIncident incident)
    {
        var markerOrigin = incident.Markers.OrderBy(marker => marker.Timestamp).First().Timestamp;
        var rows = new List<(DateTimeOffset Timestamp, int Order, string Kind, string Details)>
        {
            (incident.WindowStart, 0, "Начало окна", "Левая граница сохранённой предыстории.")
        };

        if (incident.Frames.Count > 0)
        {
            rows.Add((incident.Frames[0].Timestamp, 1, "Первый CAN-кадр", FrameSummary(incident.Frames[0])));
            rows.Add((incident.Frames[^1].Timestamp, 3, "Последний CAN-кадр", FrameSummary(incident.Frames[^1])));
        }

        foreach (var marker in incident.Markers)
            rows.Add((marker.Timestamp, 2, "ОТМЕТКА", marker.Label));

        rows.Add((incident.WindowEnd, 4, "Конец окна", "Правая граница окна после события."));
        if (incident.ObservedUntil != incident.WindowEnd)
            rows.Add((incident.ObservedUntil, 5, "Наблюдалось до", "Последний timestamp, на котором завершена оценка полноты."));

        return rows
            .OrderBy(row => row.Timestamp)
            .ThenBy(row => row.Order)
            .Select(row => new IncidentTimelineRow(
                RelativeTime(row.Timestamp, markerOrigin),
                row.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                row.Kind,
                row.Details))
            .ToArray();
    }

    private static string RelativeTime(DateTimeOffset timestamp, DateTimeOffset origin)
    {
        var seconds = (timestamp - origin).TotalSeconds;
        return seconds >= 0 ? $"+{seconds:0.000} s" : $"{seconds:0.000} s";
    }

    private static string FrameSummary(CraneCAN.Core.Models.CanFrame frame)
    {
        var id = frame.IsExtended ? $"0x{frame.Id:X8} EXT" : $"0x{frame.Id:X3} STD";
        var data = string.Join(" ", frame.Data.Select(value => value.ToString("X2")));
        return $"{id} · DLC {frame.Dlc} · {data}";
    }

    private async Task<bool> OpenIncidentCaptureAsync(LoadedIncidentPackage package)
    {
        SetBusy(true, "Открытие capture.trc из incident-пакета…");
        try
        {
            var import = await PcanTrcCodec.LoadWithDiagnosticsAsync(package.RawTracePath);
            _loadedTrcPath = package.RawTracePath;
            _loadedFrames = import.Frames;
            BuildFrameRows();
            BuildStatistics();
            SetDefaultWindows();
            LoadedFileText.Text = package.RawTracePath;
            var framesTab = MainTabs.Items.OfType<TabItem>()
                .FirstOrDefault(tab => string.Equals(tab.Header?.ToString(), "Generic CAN — кадры", StringComparison.Ordinal));
            if (framesTab is not null) MainTabs.SelectedItem = framesTab;
            StatusText.Text =
                $"Открыт capture.trc события {package.Incident.IncidentId:N}: {import.Frames.Count:N0} кадров. " +
                "Передача CAN отсутствует.";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Открытие capture.trc",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private sealed record IncidentTimelineRow(
        string RelativeText,
        string TimestampText,
        string Kind,
        string Details);
}
