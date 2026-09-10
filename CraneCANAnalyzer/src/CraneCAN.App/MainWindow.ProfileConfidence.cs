using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

namespace CraneCAN.App;

public partial class MainWindow
{
    [ModuleInitializer]
    internal static void RegisterProfileConfidenceUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ProfileConfidenceMainWindowLoaded));
    }

    private static void ProfileConfidenceMainWindowLoaded(
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
                    "profile-confidence-explanation",
                    StringComparison.Ordinal)))
        {
            return;
        }

        var button = new Button
        {
            Content = "Evidence…",
            Tag = "profile-confidence-explanation",
            ToolTip = "Показать, почему у сигналов Machine Profile текущий CANDIDATE / PROBABLE / CONFIRMED и какие evidence отсутствуют."
        };
        button.Click += window.ProfileConfidenceButton_Click;
        buttonPanel.Children.Add(button);
    }

    private void ProfileConfidenceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        UpdateProfileFromFields();

        var rows = _machineProfile.KnownSignals
            .Concat(_machineProfile.ExperimentalSignals)
            .Select(signal => new ProfileConfidenceRow(
                signal,
                MachineSignalConfidenceExplainer.Explain(signal)))
            .OrderBy(row => StatusOrder(row.Signal.Confidence))
            .ThenBy(row => row.Signal.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Signal.CanId)
            .ThenBy(row => row.Signal.IsExtended)
            .ToArray();

        if (rows.Length == 0)
        {
            MessageBox.Show(
                "В текущем Machine Profile ещё нет сигналов. Сначала добавьте CANDIDATE/PROBABLE/CONFIRMED из Guided Diagnostics или Incident Signature.",
                "Machine Profile — confidence/evidence",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var summary = new TextBlock
        {
            Text =
                $"Machine Profile: {_machineProfile.MachineName}. " +
                $"Сигналов: {rows.Length}; " +
                $"CONFIRMED {rows.Count(row => row.Signal.Confidence == SignalKnowledgeState.Confirmed)}, " +
                $"PROBABLE {rows.Count(row => row.Signal.Confidence == SignalKnowledgeState.Probable)}, " +
                $"CANDIDATE {rows.Count(row => row.Signal.Confidence == SignalKnowledgeState.Candidate)}.\n" +
                "Окно только объясняет уже сохранённый статус и evidence. Оно не повышает и не понижает confidence автоматически.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
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
            ItemsSource = rows
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Статус",
            Binding = new Binding(nameof(ProfileConfidenceRow.StatusText)),
            Width = 100
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Сигнал",
            Binding = new Binding(nameof(ProfileConfidenceRow.Name)),
            Width = 210
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "ID",
            Binding = new Binding(nameof(ProfileConfidenceRow.IdText)),
            Width = 105
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Поле",
            Binding = new Binding(nameof(ProfileConfidenceRow.FieldText)),
            Width = 165
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Evidence",
            Binding = new Binding(nameof(ProfileConfidenceRow.EvidenceCountText)),
            Width = 80
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Cross-Incident",
            Binding = new Binding(nameof(ProfileConfidenceRow.CrossIncidentText)),
            Width = 105
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Независимое",
            Binding = new Binding(nameof(ProfileConfidenceRow.IndependentText)),
            Width = 105
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Явное подтвержд.",
            Binding = new Binding(nameof(ProfileConfidenceRow.ConfirmationText)),
            Width = 125
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Предупр.",
            Binding = new Binding(nameof(ProfileConfidenceRow.CautionCountText)),
            Width = 80
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Источник",
            Binding = new Binding(nameof(ProfileConfidenceRow.Source)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var details = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            MinHeight = 260,
            Margin = new Thickness(0, 8, 0, 8),
            Text = "Выберите сигнал."
        };

        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(320) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(details, 2);
        Grid.SetRow(close, 3);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(details);
        layout.Children.Add(close);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — Machine Profile Confidence / Evidence",
            Width = 1260,
            Height = 800,
            MinWidth = 980,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is ProfileConfidenceRow row)
            {
                details.Text = BuildProfileConfidenceDetails(
                    row.Signal,
                    row.Explanation);
            }
        };
        close.Click += (_, _) => window.Close();

        grid.SelectedIndex = 0;
        window.ShowDialog();
    }

    private static string BuildProfileConfidenceDetails(
        MachineSignal signal,
        MachineSignalConfidenceExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"СИГНАЛ: {signal.Name}");
        builder.AppendLine(
            $"STATUS: {signal.Confidence.ToString().ToUpperInvariant()}    " +
            $"ID: 0x{signal.CanId.ToString(signal.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)} " +
            $"{(signal.IsExtended ? "Extended" : "Standard")}");
        builder.AppendLine(
            $"FIELD: DATA[{signal.StartByte}] bit {signal.StartBit}, length {signal.BitLength}, " +
            $"{signal.ByteOrder}, {(signal.IsSigned ? "signed" : "unsigned")}, " +
            $"scale {signal.Scale.ToString("G", CultureInfo.InvariantCulture)}, " +
            $"offset {signal.Offset.ToString("G", CultureInfo.InvariantCulture)} " +
            $"{signal.Unit}");
        builder.AppendLine($"SOURCE: {Dash(signal.Source)}");
        builder.AppendLine();

        builder.AppendLine("ПОЧЕМУ СЕЙЧАС ТАКОЙ СТАТУС");
        builder.AppendLine(explanation.StatusBasis);
        builder.AppendLine();

        builder.AppendLine("СВОДКА EVIDENCE");
        builder.AppendLine(
            $"Всего: {explanation.EvidenceCount}; " +
            $"Cross-Incident: {(explanation.HasCrossIncidentEvidence ? "да" : "нет")}; " +
            $"независимое: {(explanation.HasIndependentEvidence ? "да" : "нет")}; " +
            $"UserConfirmation: {(explanation.HasExplicitConfirmation ? "да" : "нет")}; " +
            $"Replay-only: {(explanation.ReplayOnly ? "да" : "нет")}.");
        if (explanation.EvidenceGroups.Count == 0)
        {
            builder.AppendLine("• evidence отсутствуют");
        }
        else
        {
            foreach (var group in explanation.EvidenceGroups)
            {
                builder.AppendLine($"• {group.Title}: {group.Count}");
                builder.AppendLine($"  {group.Interpretation}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("ИСХОДНЫЕ EVIDENCE");
        if (signal.Evidence.Count == 0)
        {
            builder.AppendLine("• нет");
        }
        else
        {
            foreach (var evidence in signal.Evidence
                         .OrderBy(item => item.RecordedAt)
                         .ThenBy(item => item.Kind))
            {
                builder.AppendLine(
                    $"• {evidence.Kind} | " +
                    $"{evidence.RecordedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)} | " +
                    $"origin={Dash(evidence.CaptureOrigin)}");
                if (!string.IsNullOrWhiteSpace(evidence.SourceReference))
                    builder.AppendLine($"  ref: {evidence.SourceReference}");
                if (!string.IsNullOrWhiteSpace(evidence.Description))
                    builder.AppendLine($"  {evidence.Description.Trim()}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("ОГРАНИЧЕНИЯ / ПРЕДУПРЕЖДЕНИЯ");
        if (explanation.Cautions.Count == 0)
        {
            builder.AppendLine("• специальных предупреждений не сформировано");
        }
        else
        {
            foreach (var caution in explanation.Cautions)
                builder.AppendLine($"• {caution}");
        }
        builder.AppendLine();

        builder.AppendLine("СЛЕДУЮЩИЕ ШАГИ");
        foreach (var step in explanation.NextSteps)
            builder.AppendLine($"• {step}");
        builder.AppendLine();
        builder.AppendLine(
            "Важно: CraneCAN показывает происхождение и силу evidence, но не делает автоматический вывод о физической причинности и не меняет confidence в этом окне.");

        return builder.ToString();
    }

    private static int StatusOrder(SignalKnowledgeState status) => status switch
    {
        SignalKnowledgeState.Confirmed => 0,
        SignalKnowledgeState.Probable => 1,
        SignalKnowledgeState.Candidate => 2,
        SignalKnowledgeState.Unknown => 3,
        SignalKnowledgeState.Rejected => 4,
        _ => 5
    };

    private static string Dash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private sealed record ProfileConfidenceRow(
        MachineSignal Signal,
        MachineSignalConfidenceExplanation Explanation)
    {
        public string StatusText => Signal.Confidence.ToString().ToUpperInvariant();
        public string Name => Signal.Name;
        public string IdText =>
            "0x" + Signal.CanId.ToString(
                Signal.IsExtended ? "X8" : "X3",
                CultureInfo.InvariantCulture);
        public string FieldText =>
            $"DATA[{Signal.StartByte}] b{Signal.StartBit} len {Signal.BitLength}";
        public string EvidenceCountText =>
            Explanation.EvidenceCount.ToString(CultureInfo.InvariantCulture);
        public string CrossIncidentText =>
            Explanation.HasCrossIncidentEvidence ? "да" : "нет";
        public string IndependentText =>
            Explanation.HasIndependentEvidence ? "да" : "нет";
        public string ConfirmationText =>
            Explanation.HasExplicitConfirmation ? "да" : "нет";
        public string CautionCountText =>
            Explanation.Cautions.Count.ToString(CultureInfo.InvariantCulture);
        public string Source => Dash(Signal.Source);
    }
}
