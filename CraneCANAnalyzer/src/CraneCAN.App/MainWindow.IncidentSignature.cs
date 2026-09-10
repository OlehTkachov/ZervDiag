using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async Task AnalyzeIncidentSignatureAsync(
        LoadedIncidentPackage anchorPackage,
        Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите минимум два дополнительных incident для анализа повторяемости",
            Filter = "CraneCAN incident (*.canincident)|*.canincident|Все файлы (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(owner) != true) return;

        var anchorPath = Path.GetFullPath(anchorPackage.MetadataPath);
        var selectedPaths = dialog.FileNames
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(
                path,
                anchorPath,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selectedPaths.Length < 2)
        {
            MessageBox.Show(
                "Для устойчивой сигнатуры используйте текущий incident и минимум два других incident (всего 3+).",
                "Повторяемость incident",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Incident signature: анализ повторяемости по 3+ записям…");
        try
        {
            var packages = new List<LoadedIncidentPackage> { anchorPackage };
            foreach (var path in selectedPaths)
                packages.Add(await PreFaultIncidentCodec.LoadAsync(path));

            var duplicateIncident = packages
                .GroupBy(package => package.Incident.IncidentId)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateIncident is not null)
            {
                throw new InvalidOperationException(
                    $"Один и тот же Incident ID выбран повторно: {duplicateIncident.Key:N}. " +
                    "Копии одной записи нельзя считать независимыми повторениями.");
            }

            var chains = await Task.Run(() =>
                packages
                    .Select(package =>
                    {
                        var transition =
                            IncidentTransitionAnalyzer.Analyze(package.Incident);
                        return IncidentEventChainAnalyzer.Build(
                            transition,
                            _machineProfile);
                    })
                    .ToArray());

            var result = IncidentSignatureAnalyzer.Analyze(chains);
            var warnings = result.Warnings.ToList();
            AddIncidentSignatureSourceWarnings(packages, warnings);
            result = result with
            {
                Warnings = warnings
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };

            StatusText.Text =
                $"Incident signature: {packages.Count} записей; " +
                $"HIGH {result.HighCount}, MEDIUM {result.MediumCount}, INFO {result.InfoCount}.";
            ShowIncidentSignatureWindow(packages, result, owner);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Ошибка анализа повторяемости incident",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void AddIncidentSignatureSourceWarnings(
        IReadOnlyList<LoadedIncidentPackage> packages,
        ICollection<string> warnings)
    {
        var channels = packages
            .Select(package => package.Source.ChannelId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (channels.Length > 1)
        {
            warnings.Add(
                "CAN channel metadata различаются: " +
                string.Join(" / ", channels) +
                ". Подтвердите, что записи относятся к одной физической шине.");
        }

        var knownBitrates = packages
            .Where(package => package.Source.Bitrate.HasValue)
            .Select(package => package.Source.Bitrate.GetValueOrDefault())
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        if (knownBitrates.Length > 1)
        {
            warnings.Add(
                "Bitrate metadata различаются: " +
                string.Join(
                    " / ",
                    knownBitrates.Select(value =>
                        value.ToString("N0", CultureInfo.InvariantCulture))) +
                ". Такие записи нельзя считать однородной серией без проверки шины.");
        }
        else if (packages.Any(package => !package.Source.Bitrate.HasValue) &&
                 packages.Any(package => package.Source.Bitrate.HasValue))
        {
            warnings.Add(
                "Bitrate известен не для всех incident: однородность физической шины подтверждена не полностью.");
        }

        var origins = packages
            .Select(package => package.CaptureOrigin)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (origins.Length > 1)
        {
            warnings.Add(
                "В серии смешаны разные capture origin: " +
                string.Join(" / ", origins) +
                ". Replay и live допустимо сравнивать только при известном происхождении данных.");
        }

        var drivers = packages
            .Select(package => package.Source.DriverId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (drivers.Length > 1)
        {
            warnings.Add(
                "Driver metadata различаются: " +
                string.Join(" / ", drivers) + ".");
        }

        var incompleteCount = packages.Count(package => !package.Incident.Complete);
        if (incompleteCount > 0)
        {
            warnings.Add(
                $"Неполных/ограниченных incident: {incompleteCount}/{packages.Count}. " +
                "Repeatability может быть занижена из-за отсутствующих кадров.");
        }

        var markerLabels = packages
            .Select(package => package.Incident.Markers
                .OrderBy(marker => marker.Timestamp)
                .First()
                .Label?.Trim() ?? string.Empty)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (markerLabels.Length > 1)
        {
            warnings.Add(
                "Подписи marker различаются: " +
                string.Join(" / ", markerLabels) +
                ". Убедитесь, что сравниваются повторения одного физического события.");
        }
    }

    private void ShowIncidentSignatureWindow(
        IReadOnlyList<LoadedIncidentPackage> packages,
        IncidentSignatureAnalysisResult result,
        Window owner)
    {
        var warnings = result.Warnings.Count == 0
            ? "Предупреждения качества: нет."
            : "Предупреждения качества:\n• " +
              string.Join("\n• ", result.Warnings);

        var earliestHigh = result.EarliestHigh is null
            ? "HIGH-сигнатура не сформирована."
            : $"Самый ранний HIGH: {FormatIncidentSignatureTime(result.EarliestHigh.MedianReactionMilliseconds)} · " +
              $"{FormatIncidentSignatureId(result.EarliestHigh)} · {FormatIncidentSignatureLocation(result.EarliestHigh)}.";

        var fileNames = packages
            .Select(package => Path.GetFileName(package.MetadataPath))
            .ToArray();

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Incident signature: {result.IncidentCount} независимых записей.\n" +
                $"Файлы: {string.Join("; ", fileNames)}\n" +
                $"Кандидаты: HIGH {result.HighCount}; MEDIUM {result.MediumCount}; INFO {result.InfoCount}. {earliestHigh}\n" +
                warnings + "\n" +
                "HIGH означает повторяющийся наблюдаемый CAN-шаг: 3+ incident, 100% присутствие, " +
                "одинаковый переход, разброс времени ≤250 мс и отсутствие INFO-only повторений. " +
                "Это не доказательство физической причины отказа."
        };

        var rows = result.Candidates
            .Select(candidate => new IncidentSignatureRow(candidate))
            .ToArray();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = rows
        };

        grid.Columns.Add(new DataGridTextColumn { Header = "Приоритет", Binding = new Binding(nameof(IncidentSignatureRow.PriorityText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(IncidentSignatureRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Формат", Binding = new Binding(nameof(IncidentSignatureRow.FormatText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Место", Binding = new Binding(nameof(IncidentSignatureRow.LocationText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Событие", Binding = new Binding(nameof(IncidentSignatureRow.KindText)), Width = 135 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Profile signal", Binding = new Binding(nameof(IncidentSignatureRow.ProfileSignalsText)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Повтор", Binding = new Binding(nameof(IncidentSignatureRow.RepeatabilityText)), Width = 115 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Переход", Binding = new Binding(nameof(IncidentSignatureRow.TransitionAgreementText)), Width = 115 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Median t", Binding = new Binding(nameof(IncidentSignatureRow.MedianTimeText)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Разброс t", Binding = new Binding(nameof(IncidentSignatureRow.SpreadText)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Разрыв", Binding = new Binding(nameof(IncidentSignatureRow.BreakpointText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Модальный переход", Binding = new Binding(nameof(IncidentSignatureRow.ModalTransitionText)), Width = 190 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наблюдение",
            Binding = new Binding(nameof(IncidentSignatureRow.Description)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var saveReport = new Button
        {
            Content = "Сохранить отчёт…",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            ToolTip = "Сохранить переносимый Markdown-отчёт без абсолютных путей к incident/capture."
        };
        var addToProfile = new Button
        {
            Content = "Добавить в Machine Profile…",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = false,
            ToolTip = "Доступно для MEDIUM/HIGH DATA[n]. HIGH добавляется/повышается до PROBABLE, но не CONFIRMED."
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
        buttons.Children.Add(saveReport);
        buttons.Children.Add(addToProfile);
        buttons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = owner,
            Title = "CraneCAN — Cross-Incident Repeatability / Signature",
            Width = 1450,
            Height = 720,
            MinWidth = 1040,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        grid.SelectionChanged += (_, _) =>
        {
            addToProfile.IsEnabled =
                grid.SelectedItem is IncidentSignatureRow row &&
                row.Candidate.Kind == IncidentTransitionKind.ByteChanged &&
                row.Candidate.DataIndex.HasValue &&
                row.Candidate.Priority != IncidentSignaturePriority.Info;
        };
        saveReport.Click += async (_, _) =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить Cross-Incident Signature report",
                Filter = "Markdown (*.md)|*.md|Текст (*.txt)|*.txt",
                FileName = $"CraneCAN_incident_signature_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            };
            if (dialog.ShowDialog(window) != true)
                return;

            try
            {
                await IncidentSignatureReportCodec.SaveAsync(
                    dialog.FileName,
                    packages,
                    result,
                    _machineProfile);
                StatusText.Text =
                    $"Cross-Incident Signature report сохранён: {dialog.FileName}";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Ошибка сохранения Signature report",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };
        addToProfile.Click += (_, _) =>
        {
            if (grid.SelectedItem is IncidentSignatureRow row)
                ShowIncidentSignatureProfileBuilder(row.Candidate, packages, window);
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string FormatIncidentSignatureTime(double milliseconds)
    {
        var seconds = milliseconds / 1000.0;
        return seconds >= 0 ? $"+{seconds:0.000} s" : $"{seconds:0.000} s";
    }

    private static string FormatIncidentSignatureId(
        IncidentSignatureCandidate candidate) =>
        candidate.Id.ToString(
            candidate.IsExtended ? "X8" : "X3",
            CultureInfo.InvariantCulture);

    private static string FormatIncidentSignatureLocation(
        IncidentSignatureCandidate candidate) =>
        candidate.DataIndex.HasValue
            ? $"DATA[{candidate.DataIndex.Value}]"
            : "ID/DLC";

    private sealed record IncidentSignatureRow(
        IncidentSignatureCandidate Candidate)
    {
        public string PriorityText => Candidate.Priority switch
        {
            IncidentSignaturePriority.High => "HIGH",
            IncidentSignaturePriority.Medium => "MEDIUM",
            _ => "INFO"
        };

        public string IdText => FormatIncidentSignatureId(Candidate);
        public string FormatText => Candidate.IsExtended ? "Extended" : "Standard";
        public string LocationText => FormatIncidentSignatureLocation(Candidate);

        public string KindText => Candidate.Kind switch
        {
            IncidentTransitionKind.IdAppeared => "ID появился",
            IncidentTransitionKind.PeriodicIdStopped => "ID прекратился",
            IncidentTransitionKind.DlcChanged => "DLC изменился",
            IncidentTransitionKind.ByteChanged => "байт изменился",
            _ => Candidate.Kind.ToString()
        };

        public string ProfileSignalsText => Candidate.ProfileSignals.Count == 0
            ? "—"
            : string.Join("; ", Candidate.ProfileSignals);

        public string RepeatabilityText =>
            $"{Candidate.OccurrenceCount}/{Candidate.IncidentCount} ({Candidate.RepeatabilityPercent:0.#}%)";

        public string TransitionAgreementText =>
            $"{Candidate.TransitionAgreementCount}/{Candidate.OccurrenceCount} ({Candidate.TransitionAgreementPercent:0.#}%)";

        public string MedianTimeText =>
            FormatIncidentSignatureTime(Candidate.MedianReactionMilliseconds);

        public string SpreadText =>
            $"{Candidate.TimingSpreadMilliseconds:0.###} мс";

        public string BreakpointText =>
            $"{Candidate.BreakpointCount}/{Candidate.OccurrenceCount}";

        public string ModalTransitionText =>
            $"{Candidate.ModalBaselineValue} → {Candidate.ModalObservedValue}";

        public string Description => Candidate.Description;
    }
}
