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
    private async Task CompareIncidentWithAnotherAsync(
        LoadedIncidentPackage baselinePackage,
        Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите второй incident — СРАВНЕНИЕ / FAULT / AFTER",
            Filter = "CraneCAN incident (*.canincident)|*.canincident|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(owner) != true) return;

        if (string.Equals(
                Path.GetFullPath(dialog.FileName),
                Path.GetFullPath(baselinePackage.MetadataPath),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Выбран тот же самый .canincident. Для сравнения нужны две разные записи.",
                "Сравнение incident",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Сравнение двух incident по marker-aligned цепочкам…");
        try
        {
            var currentPackage = await PreFaultIncidentCodec.LoadAsync(dialog.FileName);

            var baselineTransition = await Task.Run(() =>
                IncidentTransitionAnalyzer.Analyze(baselinePackage.Incident));
            var currentTransition = await Task.Run(() =>
                IncidentTransitionAnalyzer.Analyze(currentPackage.Incident));

            var baselineChain = IncidentEventChainAnalyzer.Build(
                baselineTransition,
                _machineProfile);
            var currentChain = IncidentEventChainAnalyzer.Build(
                currentTransition,
                _machineProfile);

            var result = IncidentEventChainComparer.Compare(
                baselineChain,
                currentChain);

            var warnings = result.Warnings.ToList();
            AddIncidentSourceWarnings(baselinePackage, currentPackage, warnings);

            StatusText.Text =
                $"Incident compare: HIGH {result.HighCount}, MEDIUM {result.MediumCount}, INFO {result.InfoCount}.";
            ShowIncidentComparisonWindow(
                baselinePackage,
                currentPackage,
                result with { Warnings = warnings },
                owner);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Ошибка сравнения incident",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void AddIncidentSourceWarnings(
        LoadedIncidentPackage baseline,
        LoadedIncidentPackage current,
        ICollection<string> warnings)
    {
        if (!string.Equals(
                baseline.Source.ChannelId,
                current.Source.ChannelId,
                StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"CAN channel различается: {baseline.Source.ChannelId} / {current.Source.ChannelId}.");
        }

        if (baseline.Source.Bitrate.HasValue &&
            current.Source.Bitrate.HasValue &&
            baseline.Source.Bitrate != current.Source.Bitrate)
        {
            warnings.Add(
                $"Bitrate metadata различается: {baseline.Source.Bitrate:N0} / {current.Source.Bitrate:N0}.");
        }

        if (!string.Equals(
                baseline.CaptureOrigin,
                current.CaptureOrigin,
                StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"Capture origin различается: {baseline.CaptureOrigin} / {current.CaptureOrigin}.");
        }

        if (!baseline.Incident.Complete || !current.Incident.Complete)
        {
            warnings.Add(
                "Хотя бы один incident имеет ограниченное качество/неполное окно; различия интерпретируйте осторожно.");
        }
    }

    private void ShowIncidentComparisonWindow(
        LoadedIncidentPackage baselinePackage,
        LoadedIncidentPackage currentPackage,
        IncidentEventChainComparisonResult result,
        Window owner)
    {
        var warnings = result.Warnings.Count == 0
            ? "Предупреждения качества: нет."
            : "Предупреждения качества:\n• " + string.Join("\n• ", result.Warnings);

        var earliest = result.Earliest is null
            ? "Различий по заданным критериям не найдено."
            : $"Первое наблюдаемое различие: {FormatIncidentComparisonEvidenceTime(result.Earliest)}.";

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"ЭТАЛОН / GOOD / BEFORE: {Path.GetFileName(baselinePackage.MetadataPath)} · " +
                $"{baselinePackage.Incident.IncidentId:N} · шагов {result.BaselineStepCount:N0}\n" +
                $"СРАВНЕНИЕ / FAULT / AFTER: {Path.GetFileName(currentPackage.MetadataPath)} · " +
                $"{currentPackage.Incident.IncidentId:N} · шагов {result.CurrentStepCount:N0}\n" +
                $"Различия: HIGH {result.HighCount}; MEDIUM {result.MediumCount}; INFO {result.InfoCount}. " +
                $"Порог timing = {result.TimingThresholdMilliseconds:0.###} мс. {earliest}\n" +
                warnings + "\n" +
                "Обе записи выровнены каждая по своему marker = 0.000 s. " +
                "Результат показывает различие наблюдаемых CAN-цепочек и не доказывает причинность."
        };

        var rows = result.Differences
            .Select(item => new IncidentComparisonRow(item))
            .ToArray();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            ItemsSource = rows
        };

        grid.Columns.Add(new DataGridTextColumn { Header = "Приоритет", Binding = new Binding(nameof(IncidentComparisonRow.PriorityText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Различие", Binding = new Binding(nameof(IncidentComparisonRow.DifferenceText)), Width = 165 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(IncidentComparisonRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Формат", Binding = new Binding(nameof(IncidentComparisonRow.FormatText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Место", Binding = new Binding(nameof(IncidentComparisonRow.LocationText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Событие", Binding = new Binding(nameof(IncidentComparisonRow.StepKindText)), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Profile signal", Binding = new Binding(nameof(IncidentComparisonRow.ProfileSignalsText)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "GOOD время", Binding = new Binding(nameof(IncidentComparisonRow.BaselineTimeText)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "FAULT время", Binding = new Binding(nameof(IncidentComparisonRow.CurrentTimeText)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Δt", Binding = new Binding(nameof(IncidentComparisonRow.DeltaText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "GOOD", Binding = new Binding(nameof(IncidentComparisonRow.BaselineValue)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "FAULT", Binding = new Binding(nameof(IncidentComparisonRow.CurrentValue)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наблюдение",
            Binding = new Binding(nameof(IncidentComparisonRow.Description)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var saveReport = new Button
        {
            Content = "Сохранить отчёт…",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            ToolTip = "Сохранить переносимый Markdown-отчёт без абсолютных локальных путей."
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
            Title = "CraneCAN — GOOD/FAULT Incident Chain Comparison",
            Width = 1450,
            Height = 720,
            MinWidth = 1020,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        saveReport.Click += async (_, _) =>
        {
            var saveDialog = new SaveFileDialog
            {
                Title = "Сохранить отчёт GOOD/FAULT incident",
                Filter = "Markdown (*.md)|*.md|Текст (*.txt)|*.txt",
                DefaultExt = ".md",
                AddExtension = true,
                FileName = $"CraneCAN_incident_compare_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            };
            if (saveDialog.ShowDialog(window) != true) return;

            saveReport.IsEnabled = false;
            try
            {
                await IncidentComparisonReportCodec.SaveAsync(
                    saveDialog.FileName,
                    baselinePackage,
                    currentPackage,
                    result,
                    _machineProfile);
                StatusText.Text = $"Отчёт сравнения incident сохранён: {saveDialog.FileName}";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Сохранение отчёта incident",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                saveReport.IsEnabled = true;
            }
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string FormatIncidentComparisonEvidenceTime(
        IncidentEventChainDifference difference)
    {
        var seconds = difference.EvidenceMilliseconds / 1000.0;
        return seconds >= 0 ? $"+{seconds:0.000} s" : $"{seconds:0.000} s";
    }

    private sealed record IncidentComparisonRow(
        IncidentEventChainDifference Difference)
    {
        public string PriorityText => Difference.Priority switch
        {
            IncidentEventChainDifferencePriority.High => "HIGH",
            IncidentEventChainDifferencePriority.Medium => "MEDIUM",
            _ => "INFO"
        };

        public string DifferenceText => Difference.DifferenceKind switch
        {
            IncidentEventChainDifferenceKind.StepOnlyInBaseline => "только GOOD",
            IncidentEventChainDifferenceKind.StepOnlyInCurrent => "только FAULT",
            IncidentEventChainDifferenceKind.TransitionChanged => "переход изменился",
            IncidentEventChainDifferenceKind.TimingChanged => "тайминг изменился",
            IncidentEventChainDifferenceKind.BreakpointStateChanged => "признак разрыва",
            _ => Difference.DifferenceKind.ToString()
        };

        public string IdText => Difference.Id.ToString(
            Difference.IsExtended ? "X8" : "X3",
            CultureInfo.InvariantCulture);
        public string FormatText => Difference.IsExtended ? "Extended" : "Standard";
        public string LocationText => Difference.DataIndex.HasValue
            ? $"DATA[{Difference.DataIndex.Value}]"
            : "ID/DLC";

        public string StepKindText => Difference.StepKind switch
        {
            IncidentTransitionKind.IdAppeared => "ID появился",
            IncidentTransitionKind.PeriodicIdStopped => "ID прекратился",
            IncidentTransitionKind.DlcChanged => "DLC изменился",
            IncidentTransitionKind.ByteChanged => "байт изменился",
            _ => Difference.StepKind.ToString()
        };

        public string ProfileSignalsText => Difference.ProfileSignals.Count == 0
            ? "—"
            : string.Join("; ", Difference.ProfileSignals);
        public string BaselineTimeText => FormatNullableTime(Difference.BaselineReactionMilliseconds);
        public string CurrentTimeText => FormatNullableTime(Difference.CurrentReactionMilliseconds);
        public string DeltaText => Difference.TimingDeltaMilliseconds.HasValue
            ? $"{Difference.TimingDeltaMilliseconds.Value:+0.###;-0.###;0} мс"
            : "—";
        public string BaselineValue => Difference.BaselineValue;
        public string CurrentValue => Difference.CurrentValue;
        public string Description => Difference.Description;

        private static string FormatNullableTime(double? milliseconds)
        {
            if (!milliseconds.HasValue) return "—";
            var seconds = milliseconds.Value / 1000.0;
            return seconds >= 0 ? $"+{seconds:0.000} s" : $"{seconds:0.000} s";
        }
    }
}
