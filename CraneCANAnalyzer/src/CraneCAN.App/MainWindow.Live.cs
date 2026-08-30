using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;
using CraneCAN.Driver.PcanBasic;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _liveUiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<GuidedExperimentRun> _liveRuns = [];
    private readonly List<GuidedExperimentRepeat> _liveRepeatDefinitions = [];
    private readonly List<LiveCaptureMetadata> _liveCaptureMetadata = [];
    private ICanDriver? _liveDriver;
    private LiveCanReceiver? _liveReceiver;
    private LiveExperimentSession? _liveSession;
    private GuidedAnalysisResult? _liveAnalysis;
    private string? _liveReplayPath;
    private string? _liveActionGroup;
    private long _liveLastFrameUtcTicks;
    private long _liveStandardFrames;
    private long _liveExtendedFrames;
    private bool _liveConnectionReady;
    private bool _liveCompleting;
    private bool _liveClosing;

    private bool IsReplaySource => LiveSourceCombo.SelectedIndex == 0;

    private void InitializeLiveGuided()
    {
        LiveSourceCombo.ItemsSource = new[]
        {
            "TRC Replay — без машины",
            "PEAK PCAN-USB — реальная CAN"
        };
        LiveSourceCombo.SelectedIndex = 0;
        LiveBitrateCombo.ItemsSource = new[] { 125_000, 250_000, 500_000, 1_000_000 };
        LiveBitrateCombo.SelectedItem = 250_000;
        LiveActionCombo.ItemsSource = new[]
        {
            "IGNITION_OFF", "IGNITION_ON", "ENGINE_OFF", "ENGINE_IDLE", "JOYSTICK_NEUTRAL",
            "BOOM_UP", "BOOM_DOWN", "TELESCOPE_OUT", "TELESCOPE_IN", "SLEW_LEFT", "SLEW_RIGHT",
            "WINCH_UP", "WINCH_DOWN", "BUTTON_PRESS", "CUSTOM_ACTION"
        };
        LiveActionCombo.Text = "TELESCOPE_OUT";
        LiveCandidatesGrid.ItemsSource = Array.Empty<GuidedCandidateRow>();
        _liveUiTimer.Tick += LiveUiTimer_Tick;
        _liveUiTimer.Start();
        UpdateLiveControls();
    }

    private void LiveSourceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LiveReplayFileButton is null) return;
        LiveReplayFileButton.IsEnabled = IsReplaySource;
        LiveBitrateCombo.IsEnabled = !IsReplaySource;
        LiveChannelCombo.ItemsSource = Array.Empty<CanChannelDescriptor>();
        LiveConnectionText.Text = "DISCONNECTED";
        LiveInstructionText.Text = IsReplaySource
            ? "Выберите сохранённый PCAN-View TRC для безопасной проверки полного цикла."
            : "Найдите PCAN-USB. Канал будет открыт только при подтверждённом LISTEN ONLY.";
    }

    private void LiveReplayFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateTrcOpenDialog();
        if (dialog.ShowDialog(this) != true) return;
        _liveReplayPath = dialog.FileName;
        LiveChannelCombo.ItemsSource = new[]
        {
            new CanChannelDescriptor("replay-trc", $"Replay: {Path.GetFileName(dialog.FileName)}")
        };
        LiveChannelCombo.SelectedIndex = 0;
        LiveInstructionText.Text = $"Replay готов к проверке: {Path.GetFileName(dialog.FileName)}";
        StatusText.Text = "Выбран Replay TRC. Передача CAN отсутствует.";
    }

    private async void LiveDiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveConnectionReady) return;
        try
        {
            SetLiveBusy(true, "Поиск Live CAN каналов…");
            IReadOnlyList<CanChannelDescriptor> channels;
            if (IsReplaySource)
            {
                if (string.IsNullOrWhiteSpace(_liveReplayPath))
                    throw new InvalidOperationException("Сначала выберите TRC для Replay.");
                await using var replay = new ReplayCanDriver(_liveReplayPath, ReplayTimingMode.Accelerated, 20);
                channels = await replay.DiscoverChannelsAsync();
            }
            else
            {
                await using var pcan = new PcanBasicCanDriver();
                channels = await pcan.DiscoverChannelsAsync();
            }

            LiveChannelCombo.ItemsSource = channels;
            LiveChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;
            LiveInstructionText.Text = channels.Count > 0
                ? $"Обнаружено каналов: {channels.Count}. Проверьте bitrate и подключитесь."
                : "Каналы не обнаружены. Проверьте адаптер и официальный драйвер PEAK.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Поиск Live CAN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetLiveBusy(false); }
    }

    private async void LiveConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveConnectionReady)
        {
            await DisconnectLiveAsync(invalidateActiveExperiment: true);
            return;
        }

        try
        {
            SetLiveBusy(true, "Подключение Live CAN…");
            if (LiveChannelCombo.SelectedItem is not CanChannelDescriptor channel)
                throw new InvalidOperationException("Сначала выберите обнаруженный канал.");

            if (IsReplaySource)
            {
                if (string.IsNullOrWhiteSpace(_liveReplayPath))
                    throw new InvalidOperationException("Не выбран исходный TRC для Replay.");
                _liveConnectionReady = true;
                LiveConnectionText.Text = "REPLAY READY";
                LiveInstructionText.Text = "Replay подключён. Нажмите START GUIDED EXPERIMENT.";
            }
            else
            {
                var bitrate = LiveBitrateCombo.SelectedItem is int selected ? selected : 250_000;
                _liveDriver = new PcanBasicCanDriver();
                _liveReceiver = CreateLiveReceiver(_liveDriver);
                var captureStart = DateTimeOffset.UtcNow;
                var rawPath = CreateLiveCapturePath("pcan", captureStart);
                await _liveReceiver.StartAsync(
                    new CanChannelSettings(channel.Id, bitrate, ListenOnly: true, IncludeErrorFrames: true),
                    rawPath, captureStart);
                var status = _liveReceiver.DriverStatus;
                if (!status.ListenOnlyConfirmed)
                    throw new InvalidOperationException("PCAN подключён, но аппаратный LISTEN ONLY не подтверждён.");
                _liveConnectionReady = true;
                LiveConnectionText.Text = "CONNECTED";
                LiveInstructionText.Text = "PCAN принимает CAN в LISTEN ONLY. Можно запускать эксперимент.";
            }

            StatusText.Text = "Live CAN готов. LISTEN ONLY подтверждён; функции CAN Tx отсутствуют.";
        }
        catch (Exception exception)
        {
            await DisposeLiveTransportAsync(invalidateActiveExperiment: true);
            MessageBox.Show(FormatException(exception), "Подключение Live CAN",
                MessageBoxButton.OK, MessageBoxImage.Error);
            LiveConnectionText.Text = "DISCONNECTED";
        }
        finally
        {
            SetLiveBusy(false);
            UpdateLiveControls();
        }
    }

    private async void LiveStartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_liveConnectionReady) throw new InvalidOperationException("Live CAN не подключён.");
            if (_liveSession?.State is not null and not (LiveExperimentState.Completed or LiveExperimentState.Aborted))
                throw new InvalidOperationException("Предыдущий Live-эксперимент ещё не завершён.");

            var configuration = CreateLiveConfiguration();
            if (_liveRuns.Count > 0 && !string.Equals(_liveActionGroup, configuration.ActionName, StringComparison.Ordinal))
            {
                var answer = MessageBox.Show(
                    "Название действия изменилось. Начать новую группу повторов и очистить прежние Live-кандидаты?",
                    "Новая группа экспериментов", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                ResetLiveExperimentGroup();
            }
            _liveActionGroup ??= configuration.ActionName;

            DateTimeOffset start;
            if (IsReplaySource)
            {
                if (string.IsNullOrWhiteSpace(_liveReplayPath))
                    throw new InvalidOperationException("Не выбран TRC для Replay.");
                await DisposeLiveTransportAsync(invalidateActiveExperiment: false);
                _liveDriver = new ReplayCanDriver(_liveReplayPath, ReplayTimingMode.Accelerated, 20);
                _liveReceiver = CreateLiveReceiver(_liveDriver);
                var frames = await PcanTrcCodec.LoadAsync(_liveReplayPath);
                start = frames[0].Timestamp;
            }
            else
            {
                if (_liveReceiver is null || !_liveReceiver.IsReceiving)
                    throw new InvalidOperationException("Поток PCAN не принимает данные. Подключитесь повторно.");
                start = DateTimeOffset.UtcNow;
            }

            _liveStandardFrames = 0;
            _liveExtendedFrames = 0;
            _liveLastFrameUtcTicks = 0;
            _liveSession = new LiveExperimentSession(configuration);
            _liveSession.Start(start);
            _liveReceiver!.AttachSession(_liveSession);

            if (IsReplaySource)
            {
                var rawPath = CreateLiveCapturePath("replay", start);
                await _liveReceiver.StartAsync(new CanChannelSettings("replay-trc", 250_000, ListenOnly: true),
                    rawPath, start);
            }

            LiveCandidatesGrid.ItemsSource = Array.Empty<GuidedCandidateRow>();
            LiveQualityText.Text = "REFERENCE записывается. Ничего не трогайте.";
            LiveStartButton.IsEnabled = false;
            LiveAbortButton.IsEnabled = true;
            UpdateLiveDisplay();
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Запуск Live-эксперимента",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { UpdateLiveControls(); }
    }

    private void LiveAbortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveSession is null) return;
        _liveSession.Abort(DateTimeOffset.UtcNow);
        _liveReceiver?.AttachSession(null);
        LiveQualityText.Text = "ABORTED: опыт остановлен оператором; кандидаты не сформированы.";
        LiveInstructionText.Text = "Эксперимент остановлен. Верните органы управления в безопасное положение.";
        UpdateLiveControls();
    }

    private async void LiveSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveRuns.Count == 0 && _liveCaptureMetadata.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Эксперимент CraneCAN (*.canexperiment)|*.canexperiment",
            FileName = SanitizeFileName(_liveActionGroup ?? "Live_Guided") + ".canexperiment"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var experiment = CreateLiveExperimentDocument();
            await GuidedJsonCodec.SaveExperimentAsync(dialog.FileName, experiment);
            StatusText.Text = $"Live-сеанс сохранён: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Сохранение Live-сеанса",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LiveUiTimer_Tick(object? sender, EventArgs e)
    {
        if (_liveSession is not null && _liveSession.State is not
            (LiveExperimentState.Idle or LiveExperimentState.Completed or LiveExperimentState.Aborted or LiveExperimentState.Analyzing))
        {
            var timestamp = IsReplaySource && Interlocked.Read(ref _liveLastFrameUtcTicks) > 0
                ? new DateTimeOffset(Interlocked.Read(ref _liveLastFrameUtcTicks), TimeSpan.Zero)
                : DateTimeOffset.UtcNow;
            try { _liveSession.AdvanceTo(timestamp); }
            catch (ArgumentOutOfRangeException) { }
        }
        UpdateLiveDisplay();
        if (_liveSession?.State == LiveExperimentState.Analyzing && !_liveCompleting)
            _ = CompleteLiveExperimentAsync();
    }

    private LiveCanReceiver CreateLiveReceiver(ICanDriver driver)
    {
        var receiver = new LiveCanReceiver(driver, new LiveCanBuffer(TimeSpan.FromSeconds(120)));
        receiver.FrameReceived += frame =>
        {
            Interlocked.Exchange(ref _liveLastFrameUtcTicks, frame.Timestamp.UtcDateTime.Ticks);
            if (frame.IsExtended) Interlocked.Increment(ref _liveExtendedFrames);
            else Interlocked.Increment(ref _liveStandardFrames);
        };
        receiver.ReceiverFaulted += message => Dispatcher.BeginInvoke(() =>
        {
            _liveConnectionReady = false;
            LiveConnectionText.Text = "CONNECTION LOST";
            LiveQualityText.Text = "INVALID: " + message;
            LiveInstructionText.Text = "STOP. Проверьте PCAN и CAN-шину; результат этого опыта недействителен.";
            UpdateLiveControls();
        });
        return receiver;
    }

    private async Task CompleteLiveExperimentAsync()
    {
        if (_liveSession is null || _liveReceiver is null || _liveCompleting) return;
        _liveCompleting = true;
        try
        {
            LiveInstructionText.Text = "АНАЛИЗ… Сравниваю REFERENCE / ACTION / POST.";
            var session = _liveSession;
            var rawPath = _liveReceiver.RawCapturePath ?? string.Empty;
            var captureStart = _liveReceiver.RawCaptureStart ?? session.Boundaries!.BaselineStart;
            var driverStatus = _liveReceiver.DriverStatus;
            await _liveReceiver.FlushCaptureAsync();
            var result = await Task.Run(session.Analyze);
            _liveReceiver.AttachSession(null);

            if (IsReplaySource) await _liveReceiver.StopAsync(invalidateActiveExperiment: false);

            var boundaries = result.Boundaries;
            var captured = result.CapturedFrames;
            var metadata = new LiveCaptureMetadata
            {
                SessionId = result.SessionId,
                RepeatNumber = result.Configuration.RepeatNumber,
                DriverId = _liveDriver?.Id ?? string.Empty,
                ChannelId = (LiveChannelCombo.SelectedItem as CanChannelDescriptor)?.Id ?? string.Empty,
                Bitrate = LiveBitrateCombo.SelectedItem is int bitrate ? bitrate : 250_000,
                ListenOnlyConfirmed = driverStatus.ListenOnlyConfirmed,
                RawCapturePath = rawPath,
                CaptureStart = captureStart,
                BaselineStart = boundaries.BaselineStart,
                BaselineEnd = boundaries.BaselineEnd,
                ActionStart = boundaries.ActionStart,
                ActionEnd = boundaries.ActionEnd,
                PostActionEnd = boundaries.PostActionEnd,
                OperatorInstruction = result.Configuration.OperatorInstruction,
                Outcome = result.Outcome.ToString().ToUpperInvariant(),
                ReceivedFrames = driverStatus.ReceivedFrames,
                StandardFrames = captured.LongCount(frame => !frame.IsExtended),
                ExtendedFrames = captured.LongCount(frame => frame.IsExtended),
                LostFrames = driverStatus.LostFrames,
                ErrorFrames = driverStatus.ErrorFrames,
                QualityWarnings = result.Warnings.Select(warning => warning.Message)
                    .Concat(result.Analysis.Quality.Issues
                        .Where(issue => issue.Severity != ExperimentQualitySeverity.Information)
                        .Select(issue => GuidedDiagnosticsText.Quality(issue))).Distinct().ToList()
            };
            _liveCaptureMetadata.Add(metadata);

            if (result.Outcome == LiveExperimentOutcome.Valid)
            {
                var definition = await SaveLiveExperimentSegmentAsync(result, rawPath);
                _liveRuns.Add(result.Run);
                _liveRepeatDefinitions.Add(definition);
                _liveAnalysis = await Task.Run(() =>
                    GuidedDiagnosticsAnalyzer.Analyze(result.Configuration.ActionName, _liveRuns));
                LiveCandidatesGrid.ItemsSource = _liveAnalysis.Candidates
                    .Select((candidate, index) => new GuidedCandidateRow(index + 1, candidate)).ToArray();
                LiveQualityText.Text = $"VALID · повторов {_liveRuns.Count} · кандидатов {_liveAnalysis.Candidates.Count}. " +
                                       FormatQuality(_liveAnalysis.Quality);
                LiveInstructionText.Text = "ГОТОВО. Результат — проверяемые кандидаты, а не подтверждённые функции ECU.";
            }
            else
            {
                LiveCandidatesGrid.ItemsSource = Array.Empty<GuidedCandidateRow>();
                LiveQualityText.Text = $"{result.Outcome.ToString().ToUpperInvariant()}: неполные данные; кандидаты подавлены.";
                LiveInstructionText.Text = "Опыт недействителен. Устраните причину и повторите.";
            }

            LiveRepeatTextBox.Text = (result.Configuration.RepeatNumber + 1).ToString(CultureInfo.InvariantCulture);
            LiveSaveButton.IsEnabled = true;
            StatusText.Text = $"Live Guided: {result.Outcome}; сырых кадров {driverStatus.ReceivedFrames:N0}; " +
                              $"потеряно {driverStatus.LostFrames:N0}.";
        }
        catch (Exception exception)
        {
            LiveQualityText.Text = "INVALID: анализ не завершён.";
            MessageBox.Show(FormatException(exception), "Live Guided Analysis",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _liveCompleting = false;
            UpdateLiveControls();
        }
    }

    private async Task<GuidedExperimentRepeat> SaveLiveExperimentSegmentAsync(
        LiveExperimentResult result, string rawPath)
    {
        if (result.CapturedFrames.Count == 0)
            throw new InvalidOperationException("Нельзя сохранить пустой Live-сегмент.");
        var directory = string.IsNullOrWhiteSpace(rawPath)
            ? Path.GetDirectoryName(CreateLiveCapturePath("segment", result.Boundaries.BaselineStart))!
            : Path.GetDirectoryName(rawPath)!;
        var path = Path.Combine(directory,
            $"experiment_{result.Configuration.RepeatNumber:D2}_{result.SessionId:N}.trc");
        var origin = result.CapturedFrames.Min(frame => frame.Timestamp);
        await using (var writer = await LiveTrcWriter.CreateAsync(path, origin))
        {
            foreach (var frame in result.CapturedFrames) await writer.AppendAsync(frame);
        }

        var boundaries = result.Boundaries;
        double Offset(DateTimeOffset value) => Math.Max(0, (value - origin).TotalMilliseconds);
        var referenceWindow = new TraceWindow(0, Offset(boundaries.BaselineEnd));
        var actionWindow = new TraceWindow(Offset(boundaries.ActionStart), Offset(boundaries.ActionEnd));
        var returnWindow = new TraceWindow(Offset(boundaries.ActionEnd), Offset(boundaries.PostActionEnd));
        return new GuidedExperimentRepeat
        {
            RepeatNumber = result.Configuration.RepeatNumber,
            ReferenceSource = new ExperimentTraceSource
            {
                Path = path, Bus = result.Configuration.Bus, Window = referenceWindow
            },
            ActionSource = new ExperimentTraceSource
            {
                Path = path, Bus = result.Configuration.Bus, Window = actionWindow
            },
            ReturnSource = new ExperimentTraceSource
            {
                Path = path, Bus = result.Configuration.Bus, Window = returnWindow
            },
            ActionApproximateTimeMilliseconds = Offset(boundaries.ActionStart),
            EventSearchToleranceMilliseconds = result.Configuration.EventSearchTolerance.TotalMilliseconds
        };
    }

    private LiveExperimentConfiguration CreateLiveConfiguration()
    {
        var action = LiveActionCombo.Text.Trim();
        var instruction = LiveInstructionTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(action)) throw new InvalidOperationException("Введите название действия.");
        if (string.IsNullOrWhiteSpace(instruction)) throw new InvalidOperationException("Введите инструкцию оператору.");
        if (!int.TryParse(LiveRepeatTextBox.Text, out var repeat) || repeat <= 0)
            throw new FormatException("Номер повтора должен быть целым положительным числом.");
        return new LiveExperimentConfiguration
        {
            ActionName = action,
            OperatorInstruction = instruction,
            Bus = string.IsNullOrWhiteSpace(CanBusTextBox.Text) ? "CAN1" : CanBusTextBox.Text.Trim(),
            RepeatNumber = repeat,
            BaselineDuration = TimeSpan.FromSeconds(ParseLiveSeconds(LiveBaselineSecondsTextBox.Text, "REFERENCE")),
            ActionLeadInDuration = TimeSpan.FromSeconds(ParseLiveSeconds(LiveLeadInSecondsTextBox.Text, "ожидание", allowZero: true)),
            ActionDuration = TimeSpan.FromSeconds(ParseLiveSeconds(LiveActionSecondsTextBox.Text, "ACTION")),
            PostActionDuration = TimeSpan.FromSeconds(ParseLiveSeconds(LivePostSecondsTextBox.Text, "POST"))
        };
    }

    private GuidedExperiment CreateLiveExperimentDocument() => new()
    {
        MachineProfileId = _machineProfile.ProfileId,
        Name = _liveActionGroup ?? "Live Guided",
        ActionName = _liveActionGroup ?? "CUSTOM_ACTION",
        Description = "Live Guided Diagnostics — receive-only",
        Bus = string.IsNullOrWhiteSpace(CanBusTextBox.Text) ? "CAN1" : CanBusTextBox.Text.Trim(),
        AnalysisVersion = "0.7.0",
        Repeats = _liveRepeatDefinitions.ToList(),
        LiveCaptures = _liveCaptureMetadata.ToList(),
        Candidates = (_liveAnalysis?.Candidates ?? [])
            .Select(candidate => new GuidedCandidateSnapshot
            {
                StableKey = candidate.StableKey,
                Id = candidate.Id,
                IsExtended = candidate.IsExtended,
                DataIndex = candidate.DataIndex,
                BitIndex = candidate.BitIndex,
                ReferenceValue = candidate.ReferenceValue,
                ActionValue = candidate.ActionValue,
                Score = candidate.Score,
                RepeatabilityCount = candidate.RepeatabilityCount,
                RepeatCount = candidate.RepeatCount,
                Status = candidate.Status
            }).ToList()
    };

    private void UpdateLiveDisplay()
    {
        var state = _liveSession?.State ?? LiveExperimentState.Idle;
        LiveStateText.Text = state.ToString().ToUpperInvariant();
        LiveInstructionText.Text = state switch
        {
            LiveExperimentState.Baseline => "НЕ ТРОГАЙТЕ ОРГАНЫ УПРАВЛЕНИЯ. Записываю REFERENCE.",
            LiveExperimentState.WaitingForAction => "ПРИГОТОВЬТЕСЬ. Действие начнётся после обратного отсчёта.",
            LiveExperimentState.Action => _liveSession!.Configuration.OperatorInstruction,
            LiveExperimentState.PostAction => "STOP. Верните орган управления в нейтраль и ничего не трогайте.",
            LiveExperimentState.Analyzing => "АНАЛИЗ…",
            LiveExperimentState.Aborted => "ABORTED. Результат опыта недействителен.",
            _ when !_liveConnectionReady => "Подключите PCAN или выберите Replay TRC.",
            _ => LiveInstructionText.Text
        };

        var remaining = GetLiveStageRemaining(state);
        LiveCountdownText.Text = remaining.HasValue ? $"{Math.Max(0, remaining.Value.TotalSeconds):00.0} s" : "—";
        var status = _liveReceiver?.DriverStatus;
        LiveCountersText.Text = $"Frames: {status?.ReceivedFrames ?? 0:N0}    " +
                                $"Standard: {Interlocked.Read(ref _liveStandardFrames):N0}    " +
                                $"Extended: {Interlocked.Read(ref _liveExtendedFrames):N0}    " +
                                $"Lost: {status?.LostFrames ?? 0:N0}    Errors: {status?.ErrorFrames ?? 0:N0}";
        LiveListenOnlyText.Text = status is { ListenOnlyConfirmed: false } && !IsReplaySource
            ? "LISTEN ONLY NOT CONFIRMED"
            : "LISTEN ONLY";
    }

    private TimeSpan? GetLiveStageRemaining(LiveExperimentState state)
    {
        var boundaries = _liveSession?.Boundaries;
        if (boundaries is null) return null;
        var nowTicks = IsReplaySource ? Interlocked.Read(ref _liveLastFrameUtcTicks) : DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        if (nowTicks <= 0) nowTicks = boundaries.BaselineStart.UtcDateTime.Ticks;
        var now = new DateTimeOffset(nowTicks, TimeSpan.Zero);
        var end = state switch
        {
            LiveExperimentState.Baseline => boundaries.BaselineEnd,
            LiveExperimentState.WaitingForAction => boundaries.ActionStart,
            LiveExperimentState.Action => boundaries.ActionEnd,
            LiveExperimentState.PostAction => boundaries.PostActionEnd,
            _ => (DateTimeOffset?)null
        };
        return end - now;
    }

    private async Task DisconnectLiveAsync(bool invalidateActiveExperiment)
    {
        await DisposeLiveTransportAsync(invalidateActiveExperiment);
        _liveConnectionReady = false;
        LiveConnectionText.Text = "DISCONNECTED";
        LiveInstructionText.Text = "Live CAN отключён.";
        StatusText.Text = "Live CAN отключён. Передача CAN отсутствует.";
        UpdateLiveControls();
    }

    private async Task DisposeLiveTransportAsync(bool invalidateActiveExperiment)
    {
        var receiver = _liveReceiver;
        var driver = _liveDriver;
        _liveReceiver = null;
        _liveDriver = null;
        if (receiver is not null)
        {
            try { await receiver.StopAsync(invalidateActiveExperiment); }
            finally { await receiver.DisposeAsync(); }
        }
        else if (driver is not null) await driver.DisposeAsync();
    }

    private void ResetLiveExperimentGroup()
    {
        _liveRuns.Clear();
        _liveRepeatDefinitions.Clear();
        _liveCaptureMetadata.Clear();
        _liveAnalysis = null;
        _liveActionGroup = null;
        LiveCandidatesGrid.ItemsSource = Array.Empty<GuidedCandidateRow>();
        LiveSaveButton.IsEnabled = false;
        LiveRepeatTextBox.Text = "1";
    }

    private void UpdateLiveControls()
    {
        if (LiveStartButton is null) return;
        var active = _liveSession?.State is LiveExperimentState.Baseline or LiveExperimentState.WaitingForAction or
            LiveExperimentState.Action or LiveExperimentState.PostAction or LiveExperimentState.Analyzing;
        LiveStartButton.IsEnabled = _liveConnectionReady && !active && !_liveCompleting;
        LiveAbortButton.IsEnabled = active && _liveSession?.State != LiveExperimentState.Analyzing;
        LiveConnectButton.Content = _liveConnectionReady ? "ОТКЛЮЧИТЬ" : "ПОДКЛЮЧИТЬ";
        LiveSourceCombo.IsEnabled = !_liveConnectionReady && !active;
        LiveReplayFileButton.IsEnabled = IsReplaySource && !_liveConnectionReady && !active;
        LiveDiscoverButton.IsEnabled = !_liveConnectionReady && !active;
        LiveChannelCombo.IsEnabled = !_liveConnectionReady && !active;
        LiveBitrateCombo.IsEnabled = !IsReplaySource && !_liveConnectionReady && !active;
    }

    private void SetLiveBusy(bool busy, string? status = null)
    {
        LiveConnectButton.IsEnabled = !busy;
        LiveDiscoverButton.IsEnabled = !busy;
        LiveReplayFileButton.IsEnabled = !busy && IsReplaySource;
        if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
    }

    private static double ParseLiveSeconds(string text, string name, bool allowZero = false)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            (!allowZero && value <= 0) || (allowZero && value < 0) || value > 3600)
            throw new FormatException($"Длительность «{name}» должна быть числом секунд от {(allowZero ? 0 : 0.1)} до 3600.");
        return value;
    }

    private static string CreateLiveCapturePath(string source, DateTimeOffset timestamp)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CraneCAN", "LiveCaptures");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"CraneCAN_{source}_{timestamp:yyyyMMdd_HHmmss_fff}.trc");
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_liveClosing) return;
        e.Cancel = true;
        _liveClosing = true;
        _liveUiTimer.Stop();
        try
        {
            if (_liveSession?.State is LiveExperimentState.Baseline or LiveExperimentState.WaitingForAction or
                LiveExperimentState.Action or LiveExperimentState.PostAction)
                _liveSession.Abort(DateTimeOffset.UtcNow, "Программа закрыта во время эксперимента.");
            await DisposeLiveTransportAsync(invalidateActiveExperiment: true);
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
