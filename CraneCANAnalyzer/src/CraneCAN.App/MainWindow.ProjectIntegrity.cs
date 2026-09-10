using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _projectIntegrityButton;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureProjectIntegrityButton();
        if (_projectIntegrityButton is null)
            Loaded += (_, _) => EnsureProjectIntegrityButton();
    }

    private void EnsureProjectIntegrityButton()
    {
        if (_projectIntegrityButton is not null)
            return;
        if (ClearButton.Parent is not Panel panel)
            return;

        var button = new Button
        {
            Content = "Целостность…",
            ToolTip =
                "Проверить *.canproject, resources, persistent TRC bindings и зависимости Guided Experiments.",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 0, 0, 0)
        };
        button.Click += ProjectIntegrityButton_Click;

        var bindingIndex = _projectTraceBindingsButton is null
            ? -1
            : panel.Children.IndexOf(_projectTraceBindingsButton);
        var projectIndex = _projectButton is null
            ? -1
            : panel.Children.IndexOf(_projectButton);
        var clearIndex = panel.Children.IndexOf(ClearButton);
        var insertIndex = bindingIndex >= 0
            ? bindingIndex + 1
            : projectIndex >= 0
                ? projectIndex + 1
                : clearIndex >= 0
                    ? clearIndex + 1
                    : panel.Children.Count;
        panel.Children.Insert(insertIndex, button);
        _projectIntegrityButton = button;
    }

    private async void ProjectIntegrityButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_craneProjectPath))
        {
            MessageBox.Show(
                "Сначала создайте или откройте *.canproject и сохраните его на диск.",
                "Целостность проекта",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true, "Проверка целостности CraneCAN project…");
            var report = await ProjectIntegrityAnalyzer.AnalyzeAsync(
                _craneProjectPath,
                _craneProject);
            ShowProjectIntegrityReport(report);
            StatusText.Text = report.IsHealthy
                ? "Project Integrity: ошибок и предупреждений нет."
                : $"Project Integrity: ошибок {report.ErrorCount}, предупреждений {report.WarningCount}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Проверка целостности проекта",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowProjectIntegrityReport(ProjectIntegrityReport report)
    {
        var summary = new TextBlock
        {
            Text =
                $"Проект: {_craneProject.Name}\n" +
                $"Ошибки: {report.ErrorCount}; предупреждения: {report.WarningCount}; " +
                $"информация: {report.InformationCount}.\n" +
                (report.IsHealthy
                    ? "Состояние: OK."
                    : report.ErrorCount > 0
                        ? "Состояние: ERROR — проект требует исправления перед полевой работой/переносом."
                        : "Состояние: WARNING — проект работает, но переносимость или полнота не гарантирована."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var rows = report.Issues
            .OrderBy(issue => issue.Severity switch
            {
                ProjectIntegritySeverity.Error => 0,
                ProjectIntegritySeverity.Warning => 1,
                _ => 2
            })
            .ThenBy(issue => issue.Subject, StringComparer.OrdinalIgnoreCase)
            .Select(issue => new ProjectIntegrityRow(
                IntegritySeverityText(issue.Severity),
                issue.Code.ToString(),
                issue.Subject,
                issue.Message))
            .ToArray();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            ItemsSource = rows
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Уровень",
            Binding = new Binding(nameof(ProjectIntegrityRow.Severity)),
            Width = 105
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Код",
            Binding = new Binding(nameof(ProjectIntegrityRow.Code)),
            Width = 190
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Resource / dependency",
            Binding = new Binding(nameof(ProjectIntegrityRow.Subject)),
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Диагностика",
            Binding = new Binding(nameof(ProjectIntegrityRow.Message)),
            Width = new DataGridLength(1.4, DataGridLengthUnitType.Star)
        });

        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
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
            Title = "CraneCAN — Project Integrity",
            Width = 1180,
            Height = 650,
            MinWidth = 880,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string IntegritySeverityText(ProjectIntegritySeverity severity) =>
        severity switch
        {
            ProjectIntegritySeverity.Error => "ERROR",
            ProjectIntegritySeverity.Warning => "WARNING",
            _ => "INFO"
        };

    private sealed record ProjectIntegrityRow(
        string Severity,
        string Code,
        string Subject,
        string Message);
}
