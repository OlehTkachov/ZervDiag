using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _projectTraceBindingsButton;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        EnsureProjectButton();
        EnsureProjectTraceBindingsButton();
    }

    private void EnsureProjectTraceBindingsButton()
    {
        if (_projectTraceBindingsButton is not null)
            return;
        if (ClearButton.Parent is not Panel panel)
            return;

        var button = new Button
        {
            Content = "TRC привязки…",
            ToolTip =
                "Сохранить однозначные REFERENCE / ACTION / RETURN -> Trace resource привязки внутри *.canproject.",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 0, 0, 0)
        };
        button.Click += ProjectTraceBindingsButton_Click;

        var projectIndex = _projectButton is null
            ? -1
            : panel.Children.IndexOf(_projectButton);
        var clearIndex = panel.Children.IndexOf(ClearButton);
        var insertIndex = projectIndex >= 0
            ? projectIndex + 1
            : clearIndex >= 0
                ? clearIndex + 1
                : panel.Children.Count;
        panel.Children.Insert(insertIndex, button);
        _projectTraceBindingsButton = button;
    }

    private async void ProjectTraceBindingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_craneProjectPath))
        {
            MessageBox.Show(
                "Сначала создайте или откройте *.canproject и сохраните его на диск.",
                "TRC привязки проекта",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var experiments = _craneProject.Resources
            .Where(resource =>
                resource.Kind == CraneProjectResourceKind.GuidedExperiment)
            .Select(resource => new ProjectBindingExperimentChoice(
                resource,
                string.IsNullOrWhiteSpace(resource.Label)
                    ? resource.RelativePath
                    : $"{resource.Label} · {resource.RelativePath}"))
            .ToArray();
        if (experiments.Length == 0)
        {
            MessageBox.Show(
                "В проекте нет GuidedExperiment resource. Сначала добавьте *.canexperiment в «Проект…».",
                "TRC привязки проекта",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var traces = _craneProject.Resources
            .Where(resource => resource.Kind == CraneProjectResourceKind.Trace)
            .Select(resource => new ProjectBindingTraceChoice(
                resource,
                string.IsNullOrWhiteSpace(resource.Label)
                    ? resource.RelativePath
                    : $"{resource.Label} · {resource.RelativePath}"))
            .ToArray();

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var experimentCombo = new ComboBox
        {
            ItemsSource = experiments,
            DisplayMemberPath = nameof(ProjectBindingExperimentChoice.Text),
            MinWidth = 420,
            Margin = new Thickness(0, 0, 0, 8)
        };
        experimentCombo.SelectedItem = experiments.FirstOrDefault(choice =>
            choice.Resource.ResourceId == _craneProject.ActiveExperimentResourceId)
            ?? experiments[0];

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Повтор",
            Binding = new Binding(nameof(ProjectBindingDependencyRow.RepeatNumber)),
            Width = 70
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Роль",
            Binding = new Binding(nameof(ProjectBindingDependencyRow.RoleText)),
            Width = 100
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Путь в experiment",
            Binding = new Binding(nameof(ProjectBindingDependencyRow.RequestedPath)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Persistent binding",
            Binding = new Binding(nameof(ProjectBindingDependencyRow.BindingText)),
            Width = 260
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Состояние",
            Binding = new Binding(nameof(ProjectBindingDependencyRow.StatusText)),
            Width = 180
        });

        var traceLabel = new TextBlock
        {
            Text = "Trace resource для выбранной зависимости:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var traceCombo = new ComboBox
        {
            ItemsSource = traces,
            DisplayMemberPath = nameof(ProjectBindingTraceChoice.Text),
            MinWidth = 380,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (traces.Length > 0)
            traceCombo.SelectedIndex = 0;

        var bind = ProjectBindingActionButton("Привязать");
        var clear = ProjectBindingActionButton("Очистить привязку");
        var close = ProjectBindingActionButton("Закрыть");
        bind.IsEnabled = traces.Length > 0;
        clear.IsEnabled = false;

        var selector = new WrapPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        selector.Children.Add(traceLabel);
        selector.Children.Add(traceCombo);
        selector.Children.Add(bind);
        selector.Children.Add(clear);
        selector.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(experimentCombo, 1);
        Grid.SetRow(grid, 2);
        Grid.SetRow(selector, 3);
        layout.Children.Add(summary);
        layout.Children.Add(experimentCombo);
        layout.Children.Add(grid);
        layout.Children.Add(selector);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — persistent TRC bindings",
            Width = 1120,
            Height = 620,
            MinWidth = 860,
            MinHeight = 450,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        async Task RefreshAsync()
        {
            if (experimentCombo.SelectedItem is not ProjectBindingExperimentChoice experimentChoice ||
                _craneProjectPath is null)
            {
                grid.ItemsSource = Array.Empty<ProjectBindingDependencyRow>();
                return;
            }

            var experimentPath = CraneProjectCodec.ResolveResourcePath(
                _craneProjectPath,
                experimentChoice.Resource);
            if (!File.Exists(experimentPath))
            {
                grid.ItemsSource = Array.Empty<ProjectBindingDependencyRow>();
                summary.Text = $"Experiment отсутствует: {experimentChoice.Resource.RelativePath}";
                return;
            }

            var experiment = await GuidedJsonCodec.ReadExperimentAsync(experimentPath);
            var rows = BuildProjectBindingRows(
                experimentChoice.Resource,
                experimentPath,
                experiment);
            grid.ItemsSource = rows;

            var issues = ProjectTraceBindingService.ValidateBindings(_craneProject);
            summary.Text =
                $"Проект: {_craneProject.Name}\n" +
                $"Experiment: {experimentChoice.Resource.RelativePath}\n" +
                $"Trace resources: {traces.Length}; persistent bindings: {_craneProject.TraceBindings.Count}. " +
                "Binding хранит только Resource ID; исходный *.canexperiment и *.trc не изменяются." +
                (issues.Count > 0
                    ? "\nПредупреждения manifest: " + string.Join("; ", issues)
                    : string.Empty);
        }

        experimentCombo.SelectionChanged += async (_, _) =>
        {
            grid.SelectedItem = null;
            clear.IsEnabled = false;
            await RefreshAsync();
        };

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not ProjectBindingDependencyRow row)
            {
                clear.IsEnabled = false;
                return;
            }

            var binding = ProjectTraceBindingService.GetBinding(
                _craneProject,
                row.ExperimentResourceId,
                row.RepeatNumber,
                row.Role);
            clear.IsEnabled = binding is not null;
            if (binding is not null)
            {
                traceCombo.SelectedItem = traces.FirstOrDefault(choice =>
                    choice.Resource.ResourceId == binding.TraceResourceId);
            }
        };

        bind.Click += async (_, _) =>
        {
            if (grid.SelectedItem is not ProjectBindingDependencyRow row ||
                traceCombo.SelectedItem is not ProjectBindingTraceChoice traceChoice ||
                _craneProjectPath is null)
                return;

            try
            {
                var tracePath = CraneProjectCodec.ResolveResourcePath(
                    _craneProjectPath,
                    traceChoice.Resource);
                if (!File.Exists(tracePath))
                {
                    throw new FileNotFoundException(
                        "Выбранный Trace resource зарегистрирован в проекте, но файл отсутствует.",
                        tracePath);
                }

                _craneProject = ProjectTraceBindingService.SetBinding(
                    _craneProject,
                    row.ExperimentResourceId,
                    row.RepeatNumber,
                    row.Role,
                    traceChoice.Resource.ResourceId);
                _craneProject = await CraneProjectCodec.SaveAsync(
                    _craneProjectPath,
                    _craneProject);
                await RefreshAsync();
                StatusText.Text =
                    $"Persistent TRC binding сохранён: repeat {row.RepeatNumber} / {ProjectBindingRoleText(row.Role)} -> " +
                    traceChoice.Resource.RelativePath;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "TRC привязка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        clear.Click += async (_, _) =>
        {
            if (grid.SelectedItem is not ProjectBindingDependencyRow row ||
                _craneProjectPath is null)
                return;

            _craneProject = ProjectTraceBindingService.RemoveBinding(
                _craneProject,
                row.ExperimentResourceId,
                row.RepeatNumber,
                row.Role);
            _craneProject = await CraneProjectCodec.SaveAsync(
                _craneProjectPath,
                _craneProject);
            await RefreshAsync();
            clear.IsEnabled = false;
            StatusText.Text =
                $"Persistent TRC binding очищен: repeat {row.RepeatNumber} / {ProjectBindingRoleText(row.Role)}.";
        };

        close.Click += (_, _) => window.Close();

        await RefreshAsync();
        window.ShowDialog();
    }

    private IReadOnlyList<ProjectBindingDependencyRow> BuildProjectBindingRows(
        CraneProjectResource experimentResource,
        string experimentPath,
        GuidedExperiment experiment)
    {
        var dependencies = new List<(int RepeatNumber, ProjectTraceBindingRole Role, string Path)>();
        foreach (var repeat in experiment.Repeats.OrderBy(item => item.RepeatNumber))
        {
            dependencies.Add((
                repeat.RepeatNumber,
                ProjectTraceBindingRole.Reference,
                repeat.ReferenceSource.Path));
            dependencies.Add((
                repeat.RepeatNumber,
                ProjectTraceBindingRole.Action,
                repeat.ActionSource.Path));
            if (repeat.ReturnSource is not null)
            {
                dependencies.Add((
                    repeat.RepeatNumber,
                    ProjectTraceBindingRole.Return,
                    repeat.ReturnSource.Path));
            }
        }

        return dependencies.Select(dependency =>
        {
            var binding = ProjectTraceBindingService.GetBinding(
                _craneProject,
                experimentResource.ResourceId,
                dependency.RepeatNumber,
                dependency.Role);
            if (binding is not null)
            {
                var trace = _craneProject.Resources.FirstOrDefault(resource =>
                    resource.ResourceId == binding.TraceResourceId);
                if (trace is null)
                {
                    return new ProjectBindingDependencyRow(
                        experimentResource.ResourceId,
                        dependency.RepeatNumber,
                        dependency.Role,
                        ProjectBindingRoleText(dependency.Role),
                        dependency.Path,
                        "resource удалён",
                        "BINDING INVALID");
                }

                var boundPath = _craneProjectPath is null
                    ? null
                    : CraneProjectCodec.ResolveResourcePath(_craneProjectPath, trace);
                return new ProjectBindingDependencyRow(
                    experimentResource.ResourceId,
                    dependency.RepeatNumber,
                    dependency.Role,
                    ProjectBindingRoleText(dependency.Role),
                    dependency.Path,
                    trace.RelativePath,
                    boundPath is not null && File.Exists(boundPath)
                        ? "BOUND / OK"
                        : "BOUND / FILE MISSING");
            }

            var automatic = ProjectTraceDependencyResolver.Resolve(
                experimentPath,
                dependency.Path);
            return new ProjectBindingDependencyRow(
                experimentResource.ResourceId,
                dependency.RepeatNumber,
                dependency.Role,
                ProjectBindingRoleText(dependency.Role),
                dependency.Path,
                "—",
                automatic.CanLoad
                    ? $"AUTO / {automatic.Kind}"
                    : automatic.Kind == ProjectTraceResolutionKind.AmbiguousTrace
                        ? "AUTO / НЕОДНОЗНАЧНО"
                        : "AUTO / НЕ НАЙДЕН");
        }).ToArray();
    }

    private static Button ProjectBindingActionButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(3)
    };

    private static string ProjectBindingRoleText(ProjectTraceBindingRole role) => role switch
    {
        ProjectTraceBindingRole.Reference => "REFERENCE",
        ProjectTraceBindingRole.Action => "ACTION",
        ProjectTraceBindingRole.Return => "RETURN",
        _ => role.ToString().ToUpperInvariant()
    };

    private sealed record ProjectBindingExperimentChoice(
        CraneProjectResource Resource,
        string Text);

    private sealed record ProjectBindingTraceChoice(
        CraneProjectResource Resource,
        string Text);

    private sealed record ProjectBindingDependencyRow(
        Guid ExperimentResourceId,
        int RepeatNumber,
        ProjectTraceBindingRole Role,
        string RoleText,
        string RequestedPath,
        string BindingText,
        string StatusText);
}
