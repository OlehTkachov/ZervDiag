using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private CraneProject _craneProject = new();
    private string? _craneProjectPath;
    private Button? _projectButton;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureProjectButton();
    }

    private void EnsureProjectButton()
    {
        if (_projectButton is not null)
            return;

        if (ClearButton.Parent is not Panel panel)
            return;

        var button = new Button
        {
            Content = "Проект…",
            ToolTip =
                "Открыть рабочий *.canproject: профиль, эксперименты, incident, трассы и отчёты одной машины.",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 0, 0, 0)
        };
        button.Click += ProjectButton_Click;

        var clearIndex = panel.Children.IndexOf(ClearButton);
        panel.Children.Insert(
            clearIndex >= 0 ? clearIndex + 1 : panel.Children.Count,
            button);
        _projectButton = button;
    }

    private void ProjectButton_Click(object sender, RoutedEventArgs e) =>
        ShowProjectManager();

    private void ShowProjectManager()
    {
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

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
            Header = "Тип",
            Binding = new Binding(nameof(ProjectResourceRow.KindText)),
            Width = 125
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Роль",
            Binding = new Binding(nameof(ProjectResourceRow.RoleText)),
            Width = 150
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Файл",
            Binding = new Binding(nameof(ProjectResourceRow.PathText)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Состояние",
            Binding = new Binding(nameof(ProjectResourceRow.StatusText)),
            Width = 110
        });

        var create = ProjectActionButton("Новый");
        var open = ProjectActionButton("Открыть…");
        var save = ProjectActionButton("Сохранить…");
        var sync = ProjectActionButton("Добавить текущие");
        var addFiles = ProjectActionButton("Добавить файлы…");
        var openSelected = ProjectActionButton("Открыть выбранное");
        var remove = ProjectActionButton("Убрать из проекта");
        var close = ProjectActionButton("Закрыть");

        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        foreach (var button in new[]
                 {
                     create, open, save, sync, addFiles,
                     openSelected, remove, close
                 })
        {
            buttons.Children.Add(button);
        }

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — рабочий проект",
            Width = 1050,
            Height = 620,
            MinWidth = 820,
            MinHeight = 450,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        void Refresh()
        {
            var missing = _craneProjectPath is null
                ? 0
                : CraneProjectCodec.GetMissingResources(
                    _craneProjectPath,
                    _craneProject).Count;

            summary.Text =
                $"Проект: {_craneProject.Name}\n" +
                $"Машина: {_craneProject.MachineName} · " +
                $"{ProjectDash(_craneProject.Manufacturer)} / {ProjectDash(_craneProject.Model)} · " +
                $"{ProjectDash(_craneProject.CanBusName)} · " +
                $"{FormatProjectBitrate(_craneProject.Bitrate)}\n" +
                $"Файл: {(_craneProjectPath ?? "ещё не сохранён")}\n" +
                $"Resources: {_craneProject.Resources.Count}; отсутствуют: {missing}. " +
                "В .canproject хранятся только относительные ссылки; исходные файлы не копируются и не изменяются.";

            grid.ItemsSource = BuildProjectRows();
        }

        create.Click += (_, _) =>
        {
            _craneProject = CreateProjectFromCurrentFields();
            _craneProjectPath = null;
            Refresh();
            StatusText.Text =
                "Создан новый рабочий проект. Сохраните .canproject в папке материалов машины.";
        };

        open.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Открыть CraneCAN project",
                Filter = "CraneCAN project (*.canproject)|*.canproject|JSON (*.json)|*.json",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(window) != true)
                return;

            try
            {
                var loadedProject = await CraneProjectCodec.LoadAsync(dialog.FileName);
                _craneProject = loadedProject;
                _craneProjectPath = dialog.FileName;
                await ApplyOpenedProjectAsync();
                Refresh();
                StatusText.Text = $"Проект открыт: {dialog.FileName}";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Ошибка открытия проекта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        save.Click += async (_, _) =>
        {
            try
            {
                if (!await EnsureProjectPathAsync(window))
                    return;

                _craneProject = UpdateProjectMetadataFromFields(_craneProject);
                var warnings = SyncCurrentResourcesIntoProject();
                _craneProject = await CraneProjectCodec.SaveAsync(
                    _craneProjectPath!,
                    _craneProject);
                Refresh();
                StatusText.Text = $"Проект сохранён: {_craneProjectPath}";

                if (warnings.Count > 0)
                {
                    MessageBox.Show(
                        string.Join("\n", warnings),
                        "Проект сохранён с предупреждениями",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Ошибка сохранения проекта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        sync.Click += async (_, _) =>
        {
            try
            {
                if (!await EnsureProjectPathAsync(window))
                    return;
                var warnings = SyncCurrentResourcesIntoProject();
                Refresh();
                if (warnings.Count > 0)
                {
                    MessageBox.Show(
                        string.Join("\n", warnings),
                        "Добавление текущих ресурсов",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    StatusText.Text =
                        "Текущий профиль / эксперимент / TRC добавлены в рабочий проект. Сохраните .canproject.";
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Добавление текущих ресурсов",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        addFiles.Click += async (_, _) =>
        {
            try
            {
                if (!await EnsureProjectPathAsync(window))
                    return;

                var dialog = new OpenFileDialog
                {
                    Title = "Добавить существующие файлы в CraneCAN project",
                    Filter =
                        "Материалы CraneCAN (*.craneprofile;*.canexperiment;*.canincident;*.cansnapshot;*.trc;*.md;*.txt;*.pdf;*.docx)|" +
                        "*.craneprofile;*.canexperiment;*.canincident;*.cansnapshot;*.trc;*.md;*.txt;*.pdf;*.docx|" +
                        "Все файлы (*.*)|*.*",
                    Multiselect = true,
                    CheckFileExists = true
                };
                if (dialog.ShowDialog(window) != true)
                    return;

                var rejected = new List<string>();
                foreach (var file in dialog.FileNames)
                {
                    try
                    {
                        _craneProject = CraneProjectCodec.AddResource(
                            _craneProject,
                            _craneProjectPath!,
                            file,
                            label: Path.GetFileName(file)).Project;
                    }
                    catch (Exception exception)
                    {
                        rejected.Add(
                            $"{Path.GetFileName(file)}: {exception.Message}");
                    }
                }

                Refresh();
                StatusText.Text =
                    "Файлы добавлены в список проекта. Сохраните .canproject.";

                if (rejected.Count > 0)
                {
                    MessageBox.Show(
                        "Не добавлены:\n" + string.Join("\n", rejected),
                        "Некоторые файлы вне папки проекта",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Добавление файлов проекта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        remove.Click += (_, _) =>
        {
            if (grid.SelectedItem is not ProjectResourceRow row)
                return;

            _craneProject = CraneProjectCodec.RemoveResource(
                _craneProject,
                row.Resource.ResourceId);
            Refresh();
            StatusText.Text =
                "Resource убран только из .canproject; исходный файл не удалялся.";
        };

        openSelected.Click += async (_, _) =>
        {
            if (grid.SelectedItem is not ProjectResourceRow row ||
                _craneProjectPath is null)
                return;

            try
            {
                await OpenProjectResourceAsync(row.Resource);
                Refresh();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatException(exception),
                    "Открытие resource проекта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        close.Click += (_, _) => window.Close();

        Refresh();
        window.ShowDialog();
    }

    private static Button ProjectActionButton(string text) =>
        new()
        {
            Content = text,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(3)
        };

    private CraneProject CreateProjectFromCurrentFields() =>
        CraneProjectCodec.Create(
            projectName: MachineNameTextBox.Text.Trim(),
            machineName: MachineNameTextBox.Text.Trim(),
            manufacturer: ManufacturerTextBox.Text,
            model: MachineModelTextBox.Text,
            canBusName: CanBusTextBox.Text,
            bitrate: _machineProfile.Bitrate);

    private CraneProject UpdateProjectMetadataFromFields(
        CraneProject project) =>
        project with
        {
            ProgramVersion = "0.7.0",
            Name = string.IsNullOrWhiteSpace(project.Name)
                ? MachineNameTextBox.Text.Trim()
                : project.Name,
            MachineName = MachineNameTextBox.Text.Trim(),
            Manufacturer = ManufacturerTextBox.Text.Trim(),
            Model = MachineModelTextBox.Text.Trim(),
            CanBusName = string.IsNullOrWhiteSpace(CanBusTextBox.Text)
                ? "CAN1"
                : CanBusTextBox.Text.Trim(),
            Bitrate = _machineProfile.Bitrate,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private async Task<bool> EnsureProjectPathAsync(Window owner)
    {
        if (!string.IsNullOrWhiteSpace(_craneProjectPath))
            return true;

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить CraneCAN project",
            Filter = "CraneCAN project (*.canproject)|*.canproject",
            FileName =
                SanitizeFileName(
                    string.IsNullOrWhiteSpace(MachineNameTextBox.Text)
                        ? "CraneCAN_project"
                        : MachineNameTextBox.Text) +
                ".canproject"
        };
        if (dialog.ShowDialog(owner) != true)
            return false;

        _craneProjectPath = dialog.FileName;
        _craneProject = UpdateProjectMetadataFromFields(_craneProject);
        _craneProject = await CraneProjectCodec.SaveAsync(
            _craneProjectPath,
            _craneProject);
        return true;
    }

    private IReadOnlyList<string> SyncCurrentResourcesIntoProject()
    {
        if (_craneProjectPath is null)
            return ["Сначала сохраните .canproject."];

        var warnings = new List<string>();
        AddCurrentResource(
            _machineProfilePath,
            CraneProjectResourceKind.MachineProfile,
            "Текущий Machine Profile",
            setActive: true,
            warnings);
        AddCurrentResource(
            _guidedDocumentPath,
            CraneProjectResourceKind.GuidedExperiment,
            "Текущий Guided Experiment",
            setActive: true,
            warnings);
        AddCurrentResource(
            _loadedTrcPath,
            CraneProjectResourceKind.Trace,
            "Текущий TRC",
            setActive: false,
            warnings);
        return warnings;
    }

    private void AddCurrentResource(
        string? path,
        CraneProjectResourceKind kind,
        string role,
        bool setActive,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            _craneProjectPath is null)
            return;

        try
        {
            _craneProject = CraneProjectCodec.AddResource(
                _craneProject,
                _craneProjectPath,
                path,
                kind,
                Path.GetFileName(path),
                role,
                setActive).Project;
        }
        catch (InvalidOperationException exception)
        {
            warnings.Add(
                $"{Path.GetFileName(path)} не включён: {exception.Message}");
        }
    }

    private IReadOnlyList<ProjectResourceRow> BuildProjectRows()
    {
        if (_craneProjectPath is null)
        {
            return _craneProject.Resources
                .Select(resource => new ProjectResourceRow(
                    resource,
                    resource.Kind.ToString(),
                    ProjectDash(resource.Role),
                    resource.RelativePath,
                    "не сохранён"))
                .ToArray();
        }

        return _craneProject.Resources
            .Select(resource =>
            {
                var full = CraneProjectCodec.ResolveResourcePath(
                    _craneProjectPath,
                    resource);
                return new ProjectResourceRow(
                    resource,
                    resource.Kind.ToString(),
                    ProjectDash(resource.Role),
                    resource.RelativePath,
                    File.Exists(full) ? "OK" : "ОТСУТСТВУЕТ");
            })
            .ToArray();
    }

    private async Task ApplyOpenedProjectAsync()
    {
        if (_craneProjectPath is null)
            return;

        var activeProfile = _craneProject.ActiveProfileResourceId.HasValue
            ? _craneProject.Resources.FirstOrDefault(resource =>
                resource.ResourceId ==
                _craneProject.ActiveProfileResourceId.Value)
            : null;

        if (activeProfile is not null)
        {
            var profilePath = CraneProjectCodec.ResolveResourcePath(
                _craneProjectPath,
                activeProfile);
            if (File.Exists(profilePath))
            {
                _machineProfile =
                    await GuidedJsonCodec.LoadProfileAsync(profilePath);
                _machineProfilePath = profilePath;
                ApplyProfileToFields();
                return;
            }
        }

        MachineNameTextBox.Text = _craneProject.MachineName;
        ManufacturerTextBox.Text = _craneProject.Manufacturer;
        MachineModelTextBox.Text = _craneProject.Model;
        CanBusTextBox.Text = _craneProject.CanBusName;
    }

    private async Task OpenProjectResourceAsync(
        CraneProjectResource resource)
    {
        if (_craneProjectPath is null)
            throw new InvalidOperationException(
                "Сначала сохраните или откройте .canproject.");

        var path = CraneProjectCodec.ResolveResourcePath(
            _craneProjectPath,
            resource);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Resource проекта не найден.",
                path);

        switch (resource.Kind)
        {
            case CraneProjectResourceKind.MachineProfile:
                _machineProfile =
                    await GuidedJsonCodec.LoadProfileAsync(path);
                _machineProfilePath = path;
                ApplyProfileToFields();
                _craneProject = _craneProject with
                {
                    ActiveProfileResourceId = resource.ResourceId
                };
                StatusText.Text = $"Machine Profile открыт из проекта: {path}";
                break;

            case CraneProjectResourceKind.GuidedExperiment:
                await OpenProjectExperimentAsync(path);
                _craneProject = _craneProject with
                {
                    ActiveExperimentResourceId = resource.ResourceId
                };
                break;

            case CraneProjectResourceKind.Incident:
                var package =
                    await PreFaultIncidentCodec.LoadAsync(path);
                StatusText.Text =
                    $"Открыто событие проекта {package.Incident.IncidentId:N}: " +
                    $"{package.Incident.Frames.Count:N0} кадров.";
                ShowIncidentTimeline(package);
                break;

            case CraneProjectResourceKind.Trace:
                await OpenProjectTraceAsync(path);
                break;

            default:
                MessageBox.Show(
                    $"Resource зарегистрирован в проекте:\n{path}\n\n" +
                    "Для этого типа автоматическое открытие пока не выполняется; " +
                    "файл остаётся доступен по указанному относительному пути.",
                    "CraneCAN project",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;
        }
    }

    private async Task OpenProjectExperimentAsync(string path)
    {
        SetBusy(true, "Открытие эксперимента из проекта…");
        try
        {
            var experiment =
                await GuidedJsonCodec.LoadExperimentAsync(path);
            var loadedRuns = new List<GuidedExperimentRun>();
            foreach (var definition in
                     experiment.Repeats.OrderBy(item => item.RepeatNumber))
            {
                loadedRuns.Add(
                    await LoadGuidedRunAsync(definition));
            }

            _guidedRuns.Clear();
            _guidedDocument = experiment;
            _guidedDocumentPath = path;
            _guidedCandidates = [];
            _guidedAnalysisResult = null;
            GuidedCandidatesGrid.ItemsSource =
                Array.Empty<GuidedCandidateRow>();
            _guidedRuns.AddRange(loadedRuns);
            _guidedRepeatDefinitions.Clear();
            _guidedRepeatDefinitions.AddRange(experiment.Repeats);
            GuidedActionNameTextBox.Text = experiment.ActionName;
            CanBusTextBox.Text = experiment.Bus;
            GuidedRepeatsText.Text =
                $"Открыт эксперимент проекта: повторов {_guidedRuns.Count}.";
            GuidedQualityText.Text =
                "Связанные TRC загружены. Нажмите «АНАЛИЗИРОВАТЬ».";
            StatusText.Text = $"Эксперимент открыт из проекта: {path}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OpenProjectTraceAsync(string path)
    {
        SetBusy(true, "Чтение TRC из проекта…");
        try
        {
            var import =
                await PcanTrcCodec.LoadWithDiagnosticsAsync(path);
            _loadedTrcPath = path;
            _loadedFrames = import.Frames;
            BuildFrameRows();
            BuildStatistics();
            SetDefaultWindows();
            LoadedFileText.Text = path;
            StatusText.Text =
                $"Открыт TRC из проекта: {import.Frames.Count:N0} кадров; " +
                $"RTR пропущено: {import.RemoteFramesSkipped:N0}; " +
                $"error/status: {import.ErrorFramesSkipped:N0}; " +
                "передача CAN отсутствует.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string ProjectDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string FormatProjectBitrate(int? bitrate) =>
        bitrate.HasValue
            ? bitrate.GetValueOrDefault().ToString(
                  "N0",
                  CultureInfo.InvariantCulture) +
              " bit/s"
            : "bitrate неизвестен";

    private sealed record ProjectResourceRow(
        CraneProjectResource Resource,
        string KindText,
        string RoleText,
        string PathText,
        string StatusText);
}
