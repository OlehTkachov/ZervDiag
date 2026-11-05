using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CraneCAN.Driver.Onk160.Capture;
using Microsoft.Win32;

namespace CraneCAN.App.Controls;

public partial class Onk160CaptureView : UserControl
{
    private readonly Onk160ContinuousCapture _capture = new();
    private readonly ObservableCollection<Onk160CaptureMarker> _markers = [];
    private readonly DispatcherTimer _timer;
    private Onk160OpenedSession? _openedSession;

    public Onk160CaptureView()
    {
        InitializeComponent();
        MarkersGrid.ItemsSource = _markers;
        _capture.Faulted += Capture_Faulted;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => RefreshDisplay();
        _timer.Start();

        Loaded += async (_, _) =>
        {
            _timer.Start();
            await RefreshPortsAsync();
        };
    }

    public async Task StopOnWindowCloseAsync()
    {
        _timer.Stop();
        try
        {
            await _capture.StopRecordingAsync();
        }
        finally
        {
            await _capture.DisconnectAsync();
        }
    }

    private async void RefreshPortsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshPortsAsync();

    private async Task RefreshPortsAsync()
    {
        try
        {
            var previous = ComPortCombo.Text;
            var ports = Onk160ContinuousCapture.DiscoverPorts();
            ComPortCombo.ItemsSource = ports;
            if (ports.Contains(previous, StringComparer.OrdinalIgnoreCase))
                ComPortCombo.Text = previous;
            else if (ports.Count > 0 && string.IsNullOrWhiteSpace(previous))
                ComPortCombo.SelectedIndex = 0;
            StatusText.Text = ports.Count == 0
                ? "COM-порты не найдены автоматически. Можно ввести COMx вручную."
                : $"Найдено COM-портов: {ports.Count}. Параметры приёма: 38400 8E1.";
        }
        catch (Exception exception)
        {
            StatusText.Text = "Ошибка поиска COM-портов: " + exception.Message;
        }
        RefreshDisplay();
        await Task.CompletedTask;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var portName = ComPortCombo.Text.Trim();
            await _capture.ConnectAsync(portName);
            StatusText.Text = $"Подключено к {portName}, 38400 8E1. Чтение постоянно активно; вне записи входящие байты отбрасываются.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Подключение ОНК-160",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Подключение не выполнено.";
        }
        RefreshDisplay();
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _capture.DisconnectAsync();
            StatusText.Text = "COM-порт отключён.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Отключение ОНК-160",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshDisplay();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Начать непрерывную запись ОНК-160",
            Filter = "ONK-160 RAW (*.raw)|*.raw",
            AddExtension = true,
            DefaultExt = ".raw",
            FileName = $"ONK160_{DateTime.Now:yyyyMMdd_HHmmss}.raw"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            await _capture.StartRecordingAsync(dialog.FileName);
            _openedSession = null;
            _markers.Clear();
            SessionPathText.Text = dialog.FileName;
            StatusText.Text = "RECORDING. Исходные байты пишутся в один RAW-файл; метки не прерывают поток.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Старт записи ОНК-160",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Запись не начата.";
        }
        RefreshDisplay();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _capture.StopRecordingAsync();
            StatusText.Text = _capture.LastError is null
                ? "Запись остановлена. RAW и метки сохранены."
                : "Запись остановлена с ошибкой: " + _capture.LastError.Message;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Остановка записи ОНК-160",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "При завершении записи произошла ошибка.";
        }
        RefreshDisplay();
    }

    private async void OpenSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture.HasOpenCapture)
        {
            MessageBox.Show("Сначала остановите текущую запись.", "Открытие сессии",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Открыть ONK-160 Session",
            Filter = "ONK-160 session (*.session.json)|*.session.json|JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var session = await Onk160SessionStore.LoadAsync(dialog.FileName);
            _openedSession = session;
            _markers.Clear();
            foreach (var marker in session.Markers)
                _markers.Add(marker);
            var duration = session.Manifest.StopTimestampUtc.HasValue
                ? session.Manifest.StopTimestampUtc.Value - session.Manifest.StartTimestampUtc
                : DateTimeOffset.UtcNow - session.Manifest.StartTimestampUtc;
            ElapsedText.Text = FormatDuration(duration);
            BytesText.Text = session.Manifest.TotalBytesWritten.ToString("N0");
            MarkerCountText.Text = session.Markers.Count.ToString("N0");
            SessionPathText.Text = session.RawPath;
            StatusText.Text = session.Manifest.IsComplete
                ? $"Сессия открыта: {Path.GetFileName(session.RawPath)}. RAW не загружался в RAM; доступны {session.Markers.Count:N0} меток."
                : $"Открыта незавершённая/восстановленная сессия: {Path.GetFileName(session.RawPath)}. Проверяйте фактический размер RAW.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Открытие ONK-160 Session",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Не удалось открыть сессию.";
        }
        RefreshDisplay();
    }

    private void GenericMarkButton_Click(object sender, RoutedEventArgs e) =>
        AddMarker("MARK");

    private void QuickMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string label })
            AddMarker(label);
    }

    private void OtherButton_Click(object sender, RoutedEventArgs e)
    {
        // Timestamp is fixed before the annotation dialog is shown.
        var marker = AddMarker("OTHER");
        if (marker is null)
            return;

        var details = ShowMarkerDetailsDialog(marker.Label, marker.Comment);
        if (details is null)
            return;

        marker.Label = details.Value.Label;
        marker.Comment = details.Value.Comment;
        MarkersGrid.Items.Refresh();
        _ = PersistMarkerEditsAsync();
    }

    private Onk160CaptureMarker? AddMarker(string label)
    {
        try
        {
            // The capture service takes UTC and monotonic timestamps before returning to the UI.
            var marker = _capture.AddMarker(label);
            _markers.Add(marker);
            MarkersGrid.ScrollIntoView(marker);
            RefreshDisplay();
            return marker;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            return null;
        }
    }

    private static (string Label, string Comment)? ShowMarkerDetailsDialog(string label, string comment)
    {
        var dialog = new Window
        {
            Title = "Описание метки ОНК-160",
            Width = 440,
            Height = 250,
            MinWidth = 440,
            MinHeight = 250,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Метка:" });
        var labelBox = new TextBox { Text = label, Margin = new Thickness(0, 3, 0, 8) };
        panel.Children.Add(labelBox);
        panel.Children.Add(new TextBlock { Text = "Комментарий:" });
        var commentBox = new TextBox
        {
            Text = comment,
            Margin = new Thickness(0, 3, 0, 10),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 75,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        panel.Children.Add(commentBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Сохранить", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (Application.Current?.MainWindow is { } owner)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true
            ? (labelBox.Text.Trim(), commentBox.Text.Trim())
            : null;
    }

    private void MarkersGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(async () => await PersistMarkerEditsAsync()));

    private async Task PersistMarkerEditsAsync()
    {
        try
        {
            if (_openedSession is not null)
            {
                await Onk160SessionStore.SaveMarkersAsync(
                    _openedSession.MarkersPath,
                    _openedSession.Manifest.SessionId,
                    _markers.ToArray());
            }
            else if (_capture.CurrentManifest is not null)
            {
                await _capture.SaveMarkerEditsAsync(_markers.ToArray());
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "Не удалось сохранить комментарии к меткам: " + exception.Message;
        }
    }

    private void Capture_Faulted(Exception exception)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusText.Text = "ОШИБКА CAPTURE: " + exception.Message +
                              ". Нажмите STOP для финального закрытия файлов.";
            RefreshDisplay();
        }));
    }

    private void RefreshDisplay()
    {
        var connected = _capture.IsConnected;
        var recording = _capture.IsRecording;
        var hasOpenCapture = _capture.HasOpenCapture;
        ConnectButton.IsEnabled = !connected && !hasOpenCapture;
        RefreshPortsButton.IsEnabled = !connected;
        ComPortCombo.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected && !hasOpenCapture;
        StartButton.IsEnabled = connected && !hasOpenCapture;
        StopButton.IsEnabled = hasOpenCapture;
        OpenSessionButton.IsEnabled = !hasOpenCapture;
        GenericMarkButton.IsEnabled = recording;
        PressureOffButton.IsEnabled = recording;
        PressureOnButton.IsEnabled = recording;
        KosOffButton.IsEnabled = recording;
        KosOnButton.IsEnabled = recording;
        HookLimitButton.IsEnabled = recording;
        OtherButton.IsEnabled = recording;

        ConnectionStateText.Text = connected ? "CONNECTED" : "DISCONNECTED";
        RecordingIndicatorText.Text = recording ? "● RECORDING" : hasOpenCapture ? "● ERROR / STOP" : "● IDLE";
        RecordingIndicatorText.Foreground = recording
            ? System.Windows.Media.Brushes.DarkGreen
            : hasOpenCapture
                ? System.Windows.Media.Brushes.DarkRed
                : System.Windows.Media.Brushes.Gray;

        if (_capture.HasOpenCapture)
        {
            ElapsedText.Text = FormatDuration(_capture.GetElapsed());
            BytesText.Text = _capture.BytesWritten.ToString("N0");
            MarkerCountText.Text = _markers.Count.ToString("N0");
        }
        else if (_openedSession is not null)
        {
            var end = _openedSession.Manifest.StopTimestampUtc ?? DateTimeOffset.UtcNow;
            ElapsedText.Text = FormatDuration(end - _openedSession.Manifest.StartTimestampUtc);
            BytesText.Text = _openedSession.Manifest.TotalBytesWritten.ToString("N0");
            MarkerCountText.Text = _markers.Count.ToString("N0");
        }
        else if (_capture.CurrentManifest is { } manifest)
        {
            ElapsedText.Text = FormatDuration(_capture.GetElapsed());
            BytesText.Text = manifest.TotalBytesWritten.ToString("N0");
            MarkerCountText.Text = _markers.Count.ToString("N0");
        }
        else
        {
            ElapsedText.Text = "00:00:00.000";
            BytesText.Text = "0";
            MarkerCountText.Text = _markers.Count.ToString("N0");
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        var totalHours = (int)duration.TotalHours;
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";
    }
}
