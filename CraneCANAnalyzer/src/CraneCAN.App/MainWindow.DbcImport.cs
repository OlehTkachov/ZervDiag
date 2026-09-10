using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _dbcImportButton;

    [ModuleInitializer]
    internal static void RegisterDbcImportUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(DbcImportMainWindowLoaded));
    }

    private static void DbcImportMainWindowLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MainWindow window ||
            window.MachineNameTextBox.Parent is not Grid profileGrid)
        {
            return;
        }

        var buttonPanel = profileGrid.Children
            .OfType<WrapPanel>()
            .FirstOrDefault();
        if (buttonPanel is null ||
            buttonPanel.Children
                .OfType<Button>()
                .Any(button => string.Equals(
                    button.Tag as string,
                    "dbc-j1939-import",
                    StringComparison.Ordinal)))
        {
            return;
        }

        var button = new Button
        {
            Content = "Импорт DBC…",
            Tag = "dbc-j1939-import",
            ToolTip =
                "Разобрать DBC, показать 11/29-bit сообщения, PGN/SPN и добавить поддерживаемые сигналы в Machine Profile как документированное evidence. CAN Tx отсутствует."
        };
        button.Click += window.DbcImportButton_Click;
        buttonPanel.Children.Add(button);
        window._dbcImportButton = button;
    }

    private async void DbcImportButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Title = "Открыть CAN database (DBC)",
            Filter = "CAN database (*.dbc)|*.dbc|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (openDialog.ShowDialog(this) != true)
            return;

        DbcDatabase database;
        DbcMachineProfileImportResult importResult;

        try
        {
            if (_dbcImportButton is not null)
                _dbcImportButton.IsEnabled = false;

            SetBusy(true, "Разбор DBC и J1939 PGN/SPN…");
            database = await DbcCodec.LoadAsync(openDialog.FileName);

            UpdateProfileFromFields();
            importResult = DbcMachineProfileImporter.Import(
                _machineProfile,
                database,
                openDialog.FileName,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Ошибка импорта DBC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        finally
        {
            SetBusy(false);
            if (_dbcImportButton is not null)
                _dbcImportButton.IsEnabled = true;
        }

        if (!ShowDbcImportPreview(
                database,
                importResult,
                openDialog.FileName))
        {
            StatusText.Text =
                "DBC разобран без изменения Machine Profile. CAN Tx отсутствует.";
            return;
        }

        _machineProfile = importResult.Profile;
        ApplyProfileToFields();

        StatusText.Text =
            $"DBC импортирован в Machine Profile: новых сигналов {importResult.ImportedSignals}, " +
            $"evidence добавлено {importResult.EvidenceAdded}, conflicts {importResult.ConflictSignals}. " +
            "Сохраните *.craneprofile на диск. CAN Tx отсутствует.";
    }

    private bool ShowDbcImportPreview(
        DbcDatabase database,
        DbcMachineProfileImportResult importResult,
        string sourcePath)
    {
        var rows = database.Messages
            .Select(message => new DbcMessagePreviewRow(message))
            .OrderByDescending(row => row.IsJ1939)
            .ThenBy(row => row.CanId)
            .ToArray();

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text =
                $"Файл: {DbcCodec.PortableFileName(sourcePath)}\n" +
                $"Messages: {database.Messages.Count}; signals: {database.SignalCount}; " +
                $"J1939 messages: {database.J1939MessageCount}.\n" +
                $"Будет добавлено новых signals: {importResult.ImportedSignals}; " +
                $"evidence к существующим: {importResult.EvidenceAdded}; " +
                $"duplicate evidence: {importResult.DuplicateEvidenceSkipped}; " +
                $"multiplexed skipped: {importResult.SkippedMultiplexedSignals}; " +
                $"float/double skipped: {importResult.SkippedFloatingPointSignals}; " +
                $"CAN FD skipped: {importResult.SkippedCanFdSignals}; " +
                $"conflicts: {importResult.ConflictSignals}.\n" +
                "Новые DBC/J1939 сигналы получают PROBABLE, но не CONFIRMED. " +
                "Существующие сигналы не перезаписываются при конфликте engineering semantics. CAN Tx отсутствует."
        };

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
            ItemsSource = rows,
            MinHeight = 260
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Message",
            Binding = new Binding(nameof(DbcMessagePreviewRow.Name)),
            Width = 180
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "CAN ID",
            Binding = new Binding(nameof(DbcMessagePreviewRow.IdText)),
            Width = 115
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Format",
            Binding = new Binding(nameof(DbcMessagePreviewRow.FormatText)),
            Width = 85
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "PGN",
            Binding = new Binding(nameof(DbcMessagePreviewRow.PgnText)),
            Width = 90
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Priority",
            Binding = new Binding(nameof(DbcMessagePreviewRow.PriorityText)),
            Width = 70
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "SA",
            Binding = new Binding(nameof(DbcMessagePreviewRow.SourceAddressText)),
            Width = 65
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "DA",
            Binding = new Binding(nameof(DbcMessagePreviewRow.DestinationAddressText)),
            Width = 65
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "DLC",
            Binding = new Binding(nameof(DbcMessagePreviewRow.DlcText)),
            Width = 55
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Signals",
            Binding = new Binding(nameof(DbcMessagePreviewRow.SignalCountText)),
            Width = 65
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "SPN",
            Binding = new Binding(nameof(DbcMessagePreviewRow.SpnText)),
            Width = new DataGridLength(
                1,
                DataGridLengthUnitType.Star)
        });

        var warningText = importResult.Warnings.Count == 0
            ? "Warnings: нет."
            : "Warnings:\n• " +
              string.Join(
                  "\n• ",
                  importResult.Warnings.Take(20)) +
              (importResult.Warnings.Count > 20
                  ? $"\n… ещё {importResult.Warnings.Count - 20}"
                  : string.Empty);
        var warnings = new TextBox
        {
            Text = warningText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 100,
            MaxHeight = 180,
            Margin = new Thickness(0, 8, 0, 8)
        };

        var importButton = new Button
        {
            Content = "Импортировать в Machine Profile",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = importResult.ChangedSignals > 0
        };
        var cancelButton = new Button
        {
            Content = "Отмена",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(importButton);
        buttons.Children.Add(cancelButton);

        var layout = new Grid
        {
            Margin = new Thickness(12)
        };
        layout.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(
                    1,
                    GridUnitType.Star)
            });
        layout.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(warnings, 2);
        Grid.SetRow(buttons, 3);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(warnings);
        layout.Children.Add(buttons);

        var previewWindow = new Window
        {
            Owner = this,
            Title = "CraneCAN — DBC / J1939 import preview",
            Width = 1080,
            Height = 700,
            MinWidth = 820,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        importButton.Click += (_, _) =>
        {
            previewWindow.DialogResult = true;
        };
        cancelButton.Click += (_, _) =>
        {
            previewWindow.DialogResult = false;
        };

        return previewWindow.ShowDialog() == true;
    }

    private sealed record DbcMessagePreviewRow(
        DbcMessageDefinition Message)
    {
        public string Name => Message.Name;

        public uint CanId => Message.CanId;

        public bool IsJ1939 => Message.IsJ1939;

        public string IdText =>
            "0x" + Message.CanId.ToString(
                Message.IsExtended ? "X8" : "X3",
                CultureInfo.InvariantCulture);

        public string FormatText =>
            Message.IsExtended
                ? "Extended"
                : "Standard";

        public string PgnText =>
            Message.J1939?.PgnHex ?? "—";

        public string PriorityText =>
            Message.J1939 is null
                ? "—"
                : Message.J1939.Priority.ToString(
                    CultureInfo.InvariantCulture);

        public string SourceAddressText =>
            Message.J1939?.SourceAddressHex ?? "—";

        public string DestinationAddressText =>
            Message.J1939?.DestinationAddressHex ?? "—";

        public string DlcText =>
            Message.Dlc.ToString(CultureInfo.InvariantCulture);

        public string SignalCountText =>
            Message.Signals.Count.ToString(
                CultureInfo.InvariantCulture);

        public string SpnText
        {
            get
            {
                var spns = Message.Signals
                    .Where(signal => signal.Spn.HasValue)
                    .Select(signal => signal.Spn!.Value)
                    .Distinct()
                    .OrderBy(value => value)
                    .Take(8)
                    .Select(value =>
                        value.ToString(
                            CultureInfo.InvariantCulture))
                    .ToArray();

                if (spns.Length == 0)
                    return "—";

                var suffix = Message.Signals.Count(signal =>
                    signal.Spn.HasValue) > spns.Length
                    ? ", …"
                    : string.Empty;
                return string.Join(", ", spns) + suffix;
            }
        }
    }
}
