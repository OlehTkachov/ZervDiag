using System.IO;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _projectPackageImportButton;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) =>
        {
            EnsureProjectPackageButton();
            EnsureProjectPackageVerifyButton();
            EnsureProjectPackageImportButton();
        };
    }

    private void EnsureProjectPackageImportButton()
    {
        if (_projectPackageImportButton is not null)
            return;
        if (ClearButton.Parent is not Panel panel)
            return;

        var button = new Button
        {
            Content = "Импорт ZIP…",
            ToolTip =
                "Проверить переносимый CraneCAN ZIP package, внутренний SHA-256 manifest, безопасно распаковать в новую папку и открыть *.canproject.",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 0, 0, 0)
        };
        button.Click += ProjectPackageImportButton_Click;

        var verifyIndex = _projectPackageVerifyButton is null
            ? -1
            : panel.Children.IndexOf(_projectPackageVerifyButton);
        var exportIndex = _projectPackageButton is null
            ? -1
            : panel.Children.IndexOf(_projectPackageButton);
        var integrityIndex = _projectIntegrityButton is null
            ? -1
            : panel.Children.IndexOf(_projectIntegrityButton);
        var insertIndex = verifyIndex >= 0
            ? verifyIndex + 1
            : exportIndex >= 0
                ? exportIndex + 1
                : integrityIndex >= 0
                    ? integrityIndex + 1
                    : panel.Children.Count;

        panel.Children.Insert(insertIndex, button);
        _projectPackageImportButton = button;
    }

    private async void ProjectPackageImportButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var packageDialog = new OpenFileDialog
        {
            Title = "Открыть переносимый CraneCAN ZIP package",
            Filter = "CraneCAN ZIP package (*.zip)|*.zip|Все файлы (*.*)|*.*",
            DefaultExt = ".zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (packageDialog.ShowDialog(this) != true)
            return;

        var folderDialog = new OpenFolderDialog
        {
            Title =
                "Выберите родительскую папку. CraneCAN создаст внутри новую папку для импортированного проекта.",
            Multiselect = false
        };
        if (folderDialog.ShowDialog(this) != true)
            return;

        var archiveStem = SanitizeFileName(
            Path.GetFileNameWithoutExtension(packageDialog.FileName));
        if (string.IsNullOrWhiteSpace(archiveStem))
            archiveStem = "CraneCAN_import";

        var destinationDirectory = Path.Combine(
            folderDialog.FolderName,
            archiveStem);
        if (Directory.Exists(destinationDirectory) ||
            File.Exists(destinationDirectory))
        {
            MessageBox.Show(
                "Папка назначения уже существует:\n" +
                destinationDirectory +
                "\n\nCraneCAN Import намеренно не перезаписывает существующие материалы. " +
                "Выберите другую родительскую папку или переименуйте ZIP.",
                "Импорт не начат",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            SetBusy(true, "Проверка SHA-256 manifest и безопасный импорт CraneCAN ZIP package…");
            var result = await CraneProjectPackageImporter.ImportZipAsync(
                packageDialog.FileName,
                destinationDirectory);

            _craneProject = result.Project;
            _craneProjectPath = result.ProjectPath;
            await ApplyOpenedProjectAsync();

            StatusText.Text =
                $"Project package импортирован: {Path.GetFileName(result.ProjectPath)} · " +
                $"{result.ResourceCount} resources · SHA-256 {result.ArchiveSha256}";

            MessageBox.Show(
                "CraneCAN ZIP package проверен, безопасно распакован и открыт.\n\n" +
                $"Проект: {result.ProjectPath}\n" +
                $"Resources: {result.ResourceCount}\n" +
                $"Файлов проверено: {result.FileCount}\n" +
                $"Распаковано: {FormatPackageBytes(result.UncompressedBytes)}\n" +
                $"ZIP: {FormatPackageBytes(result.ArchiveBytes)}\n" +
                $"SHA-256 архива:\n{result.ArchiveSha256}\n\n" +
                $"SHA-256 внутреннего package manifest:\n{result.PackageManifestSha256}\n\n" +
                "Проверены структура архива, безопасные пути, состав относительно .canproject, " +
                "записанный при экспорте SHA-256 и размер каждого payload entry, а затем Project Integrity. " +
                "Существующие файлы не перезаписывались.\n\n" +
                "Внутренний SHA-256 manifest подтверждает целостность package, но не является цифровой подписью автора.",
                "CraneCAN package импортирован",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (ProjectPackageIntegrityException exception)
        {
            StatusText.Text =
                $"Импорт package остановлен: ошибок {exception.IntegrityReport.ErrorCount}, " +
                $"предупреждений {exception.IntegrityReport.WarningCount}.";
            MessageBox.Show(
                exception.Message +
                "\n\nПапка проекта не опубликована. Исправьте package/source project и повторите импорт.",
                "Импорт остановлен",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ShowProjectIntegrityReport(exception.IntegrityReport);
        }
        catch (Exception exception)
        {
            StatusText.Text = "Импорт CraneCAN package не выполнен.";
            MessageBox.Show(
                FormatException(exception),
                "Ошибка импорта CraneCAN package",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
