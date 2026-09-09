using System.IO;
using System.Threading;
using System.Windows;
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
            StatusText.Text = $"Событие сохранено: {path}. Для просмотра откройте capture.trc из этой папки.";
        }
        catch (Exception exception) { MessageBox.Show(FormatException(exception), "Сохранение события"); }
        finally { UpdateIncidentDisplay(); }
    }
}
