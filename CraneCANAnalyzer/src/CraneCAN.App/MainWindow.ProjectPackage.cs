using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _projectPackageButton;

    private void EnsureProjectPackageButton()
    {
        if (_projectPackageButton is not null)
            return;
        if (ClearButton.Parent is not Panel panel)
            return;

        var button = new Button
        {
            Content = "Экспорт ZIP…",
            ToolTip =
                "Создать проверенный переносимый ZIP package из *.canproject и только зарегистрированных resources.",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 0, 0, 0)
        };
        button.Click += ProjectPackageButton_Click;

        var integrityIndex = _projectIntegrityButton is null
            ? -1
            : panel.Children.IndexOf(_projectIntegrityButton);
        var bindingIndex = _projectTraceBindingsButton is null
            ? -1
            : panel.Children.IndexOf(_projectTraceBindingsButton);
        var projectIndex = _projectButton is null
            ? -1
            : panel.Children.IndexOf(_projectButton);
        var clearIndex = panel.Children.IndexOf(ClearButton);
        var insertIndex = integrityIndex >= 0
            ? integrityIndex + 1
            : bindingIndex >= 0
                ? bindingIndex + 1
                : projectIndex >= 0
                    ? projectIndex + 1
                    : clearIndex >= 0
                        ? clearIndex + 1
                        : panel.Children.Count;

        panel.Children.Insert(insertIndex, button);
        _projectPackageButton = button;
    }

    private async void ProjectPackageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_craneProjectPath))
        {
            MessageBox.Show(
                "Сначала создайте или откройте *.canproject и сохраните его на диск.",
                "Экспорт CraneCAN package",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var packageName = SanitizeFileName(
            string.IsNullOrWhiteSpace(_craneProject.Name)
                ? "CraneCAN_project"
                : _craneProject.Name);

        var dialog = new SaveFileDialog
        {
            Title = "Экспортировать переносимый CraneCAN package",
            Filter = "CraneCAN ZIP package (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = packageName + "_package.zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var allowOverwrite = File.Exists(dialog.FileName);

        try
        {
            SetBusy(true, "Проверка и упаковка CraneCAN project…");
            var result = await CraneProjectPackageExporter.ExportZipAsync(
                _craneProjectPath,
                _craneProject,
                dialog.FileName,
                allowOverwrite);

            StatusText.Text =
                $"Project package создан: {Path.GetFileName(result.ArchivePath)} · " +
                $"{result.ResourceCount} resources · SHA-256 {result.Sha256}";

            MessageBox.Show(
                "Переносимый CraneCAN package создан и проверен.\n\n" +
                $"Файл: {result.ArchivePath}\n" +
                $"Resources: {result.ResourceCount}\n" +
                $"Файлов в ZIP: {result.FileCount}\n" +
                $"Исходный объём: {FormatPackageBytes(result.UncompressedBytes)}\n" +
                $"ZIP: {FormatPackageBytes(result.ArchiveBytes)}\n" +
                $"SHA-256:\n{result.Sha256}\n\n" +
                "Проверены source project, staged copy и содержимое ZIP. " +
                "Исходные *.canproject/resources не изменялись.",
                "CraneCAN package готов",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (ProjectPackageIntegrityException exception)
        {
            StatusText.Text =
                $"Project package не создан: ошибок {exception.IntegrityReport.ErrorCount}, " +
                $"предупреждений {exception.IntegrityReport.WarningCount}.";
            MessageBox.Show(
                exception.Message +
                "\n\nИсправьте Project Integrity и повторите экспорт. ZIP не создан.",
                "Экспорт остановлен",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ShowProjectIntegrityReport(exception.IntegrityReport);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Ошибка экспорта CraneCAN package",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string FormatPackageBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        var kib = bytes / 1024d;
        if (kib < 1024)
            return kib.ToString("F2", CultureInfo.InvariantCulture) + " KiB";

        var mib = kib / 1024d;
        if (mib < 1024)
            return mib.ToString("F2", CultureInfo.InvariantCulture) + " MiB";

        return (mib / 1024d).ToString("F2", CultureInfo.InvariantCulture) + " GiB";
    }
}
