using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async void ConfigurationSnapshotCompareButton_Click(object sender, RoutedEventArgs e)
    {
        var baselineDialog = CreateSnapshotOpenDialog(
            "Выберите эталонный snapshot — GOOD / BEFORE");
        if (baselineDialog.ShowDialog(this) != true) return;

        var currentDialog = CreateSnapshotOpenDialog(
            "Выберите сравниваемый snapshot — FAULT / AFTER");
        if (currentDialog.ShowDialog(this) != true) return;

        if (string.Equals(
                baselineDialog.FileName,
                currentDialog.FileName,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Выбран один и тот же файл. Для сравнения нужны два snapshot.",
                "Сравнение снимков CAN",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Сравнение сохранённых CAN snapshot…");
        try
        {
            var baseline = await ConfigurationSnapshotCodec.LoadAsync(baselineDialog.FileName);
            var current = await ConfigurationSnapshotCodec.LoadAsync(currentDialog.FileName);
            var result = await Task.Run(() => ConfigurationSnapshotComparer.Compare(baseline, current));

            StatusText.Text =
                $"Snapshot сравнение: HIGH {result.HighCount}, MEDIUM {result.MediumCount}, INFO {result.InfoCount}.";
            ShowConfigurationSnapshotComparison(
                baselineDialog.FileName,
                currentDialog.FileName,
                baseline,
                current,
                result);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Ошибка сравнения снимков CAN",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static OpenFileDialog CreateSnapshotOpenDialog(string title) => new()
    {
        Title = title,
        Filter = "CraneCAN observed snapshot (*.cansnapshot)|*.cansnapshot|JSON (*.json)|*.json|Все файлы (*.*)|*.*",
        Multiselect = false
    };

    private void ShowConfigurationSnapshotComparison(
        string baselinePath,
        string currentPath,
        ObservedConfigurationSnapshot baseline,
        ObservedConfigurationSnapshot current,
        ConfigurationSnapshotComparisonResult result)
    {
        var warnings = result.Warnings.Count == 0
            ? "Предупреждения качества: нет."
            : "Предупреждения качества:\n• " + string.Join("\n• ", result.Warnings);

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"ЭТАЛОН: {System.IO.Path.GetFileName(baselinePath)} · {SnapshotIdentity(baseline)} · ID {result.BaselineIdCount:N0}\n" +
                $"СРАВНЕНИЕ: {System.IO.Path.GetFileName(currentPath)} · {SnapshotIdentity(current)} · ID {result.CurrentIdCount:N0}\n" +
                $"Изменения: HIGH {result.HighCount:N0}; MEDIUM {result.MediumCount:N0}; INFO {result.InfoCount:N0}.\n" +
                warnings + "\n" +
                "Результат показывает наблюдаемое различие двух записей. Он не доказывает изменение внутренней конфигурации ECU."
        };

        var rows = result.Differences
            .Select(item => new ConfigurationSnapshotDifferenceRow(item))
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

        grid.Columns.Add(new DataGridTextColumn { Header = "Приоритет", Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.PriorityText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Формат", Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.FormatText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Тип", Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.KindText)), Width = 175 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Сигнал", Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.SignalName)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Эталон", Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.BaselineValue)), Width = 190 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Сравнение", Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.CurrentValue)), Width = 190 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наблюдение",
            Binding = new Binding(nameof(ConfigurationSnapshotDifferenceRow.Description)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(close, 2);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(close);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — сравнение CAN snapshot",
            Width = 1250,
            Height = 660,
            MinWidth = 920,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string SnapshotIdentity(ObservedConfigurationSnapshot snapshot)
    {
        var machine = string.IsNullOrWhiteSpace(snapshot.MachineName)
            ? "машина не указана"
            : snapshot.MachineName;
        var source = string.IsNullOrWhiteSpace(snapshot.SourceFileName)
            ? "источник не указан"
            : snapshot.SourceFileName;
        return $"{machine} · {source}";
    }

    private sealed record ConfigurationSnapshotDifferenceRow(ConfigurationSnapshotDifference Difference)
    {
        public string PriorityText => Difference.Priority switch
        {
            ConfigurationSnapshotDifferencePriority.High => "HIGH",
            ConfigurationSnapshotDifferencePriority.Medium => "MEDIUM",
            _ => "INFO"
        };

        public string IdText => Difference.Id.HasValue
            ? Difference.Id.Value.ToString(
                Difference.IsExtended == true ? "X8" : "X3",
                CultureInfo.InvariantCulture)
            : "—";

        public string FormatText => Difference.Id.HasValue
            ? Difference.IsExtended == true ? "Extended" : "Standard"
            : "—";

        public string KindText => Difference.Kind switch
        {
            ConfigurationSnapshotDifferenceKind.IdAppeared => "ID появился",
            ConfigurationSnapshotDifferenceKind.IdDisappeared => "ID исчез",
            ConfigurationSnapshotDifferenceKind.ModalDlcChanged => "modal DLC",
            ConfigurationSnapshotDifferenceKind.ObservedDlcSetChanged => "набор DLC",
            ConfigurationSnapshotDifferenceKind.ModalDataChanged => "modal DATA",
            ConfigurationSnapshotDifferenceKind.PeriodChanged => "период",
            ConfigurationSnapshotDifferenceKind.ProfileSignalVisibilityChanged => "видимость сигнала",
            _ => Difference.Kind.ToString()
        };

        public string SignalName => string.IsNullOrWhiteSpace(Difference.SignalName)
            ? "—"
            : Difference.SignalName;

        public string BaselineValue => Difference.BaselineValue;
        public string CurrentValue => Difference.CurrentValue;
        public string Description => Difference.Description;
    }
}
