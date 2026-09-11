using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _projectPackageVerifyButton;

    private void EnsureProjectPackageVerifyButton()
    {
        if (_projectPackageVerifyButton is not null)
            return;
        if (ClearButton.Parent is not Panel panel)
            return;

        var button = new Button
        {
            Content = "Проверить ZIP…",
            ToolTip =
                "Проверить Project Package прямо внутри ZIP: безопасные пути, .canproject, внутренний SHA-256 manifest, каждый payload и при наличии внешний trusted fingerprint. Распаковка не выполняется.",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 0, 0, 0)
        };
        button.Click += ProjectPackageVerifyButton_Click;

        var exportIndex = _projectPackageButton is null
            ? -1
            : panel.Children.IndexOf(_projectPackageButton);
        var insertIndex = exportIndex >= 0
            ? exportIndex + 1
            : panel.Children.Count;

        panel.Children.Insert(insertIndex, button);
        _projectPackageVerifyButton = button;
    }

    private async void ProjectPackageVerifyButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Проверить CraneCAN Project Package без распаковки",
            Filter = "CraneCAN ZIP package (*.zip)|*.zip|Все файлы (*.*)|*.*",
            DefaultExt = ".zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SetBusy(true, "Проверка CraneCAN ZIP без распаковки…");
            var result = await CraneProjectPackageVerifier.VerifyZipAsync(dialog.FileName);

            ProjectPackageTrustedFingerprintComparison? trusted = null;
            if (result.IsValid)
                trusted = ResolveTrustedFingerprint(result);

            var displayStatus = BuildProjectPackageDisplayStatus(result, trusted);
            StatusText.Text =
                $"Project Package Verify: {displayStatus} · {Path.GetFileName(result.ArchivePath)} · " +
                $"SHA-256 {result.ArchiveSha256}";
            ShowProjectPackageVerificationReport(result, trusted);
        }
        catch (Exception exception)
        {
            StatusText.Text = "Проверка CraneCAN ZIP не выполнена.";
            MessageBox.Show(
                FormatException(exception),
                "Ошибка проверки CraneCAN Project Package",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private ProjectPackageTrustedFingerprintComparison? ResolveTrustedFingerprint(
        ProjectPackageVerificationResult verification)
    {
        var fingerprintPath =
            CraneProjectPackageTrustedFingerprintCodec.GetSidecarPath(
                verification.ArchivePath);

        if (!File.Exists(fingerprintPath))
        {
            var choose = MessageBox.Show(
                "Внешний CraneCAN fingerprint рядом с ZIP не найден.\n\n" +
                "Если вы получили fingerprint отдельно по доверенному каналу, выбрать его сейчас?\n\n" +
                "«Нет» — выполнить только внутреннюю проверку ZIP.",
                "Внешний trusted fingerprint",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (choose != MessageBoxResult.Yes)
                return null;

            var fingerprintDialog = new OpenFileDialog
            {
                Title = "Выберите внешний CraneCAN package fingerprint",
                Filter =
                    "CraneCAN package fingerprint (*.cranefingerprint.json)|*.cranefingerprint.json|" +
                    "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (fingerprintDialog.ShowDialog(this) != true)
                return null;

            fingerprintPath = fingerprintDialog.FileName;
        }

        try
        {
            var fingerprint =
                CraneProjectPackageTrustedFingerprintCodec.Load(
                    fingerprintPath);
            return CraneProjectPackageTrustedFingerprintCodec.Compare(
                fingerprintPath,
                fingerprint,
                verification);
        }
        catch (Exception exception)
        {
            return new ProjectPackageTrustedFingerprintComparison(
                Path.GetFullPath(fingerprintPath),
                false,
                new[]
                {
                    "Внешний fingerprint не читается или некорректен: " +
                    exception.Message
                });
        }
    }

    private void ShowProjectPackageVerificationReport(
        ProjectPackageVerificationResult result,
        ProjectPackageTrustedFingerprintComparison? trusted)
    {
        var displayStatus = BuildProjectPackageDisplayStatus(result, trusted);
        var text = BuildProjectPackageVerificationText(result, trusted);
        var textBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(10)
        };

        var closeButton = new Button
        {
            Content = "Закрыть",
            MinWidth = 100,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8)
        };

        var root = new DockPanel();
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        footer.Children.Add(closeButton);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(textBox);

        var window = new Window
        {
            Owner = this,
            Title = $"Project Package Verify — {displayStatus}",
            Width = 980,
            Height = 680,
            MinWidth = 700,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        closeButton.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string BuildProjectPackageDisplayStatus(
        ProjectPackageVerificationResult result,
        ProjectPackageTrustedFingerprintComparison? trusted)
    {
        if (!result.IsValid)
            return "INVALID";
        if (trusted is null)
            return "VALID / NO TRUSTED FINGERPRINT";
        return trusted.IsMatch
            ? "TRUSTED MATCH"
            : "TRUSTED MISMATCH";
    }

    private static string BuildProjectPackageVerificationText(
        ProjectPackageVerificationResult result,
        ProjectPackageTrustedFingerprintComparison? trusted)
    {
        var builder = new StringBuilder();
        var displayStatus = BuildProjectPackageDisplayStatus(result, trusted);
        builder.AppendLine("CraneCAN Project Package Verify");
        builder.AppendLine(new string('=', 72));
        builder.AppendLine($"STATUS: {displayStatus}");
        builder.AppendLine($"INTERNAL STATUS: {result.Status}");
        builder.AppendLine($"ZIP: {result.ArchivePath}");
        builder.AppendLine($"ZIP SHA-256: {result.ArchiveSha256}");
        builder.AppendLine($"ZIP bytes: {result.ArchiveBytes}");
        builder.AppendLine($"Uncompressed bytes: {result.UncompressedBytes}");
        builder.AppendLine($"Archive files: {result.FileCount}");
        builder.AppendLine($"Project ID: {(result.ProjectId.HasValue ? result.ProjectId.Value.ToString("D") : "—")}");
        builder.AppendLine($"Project: {(string.IsNullOrWhiteSpace(result.ProjectName) ? "—" : result.ProjectName)}");
        builder.AppendLine($"Resources: {result.ResourceCount}");
        builder.AppendLine($"Internal manifest SHA-256: {(string.IsNullOrWhiteSpace(result.PackageManifestSha256) ? "—" : result.PackageManifestSha256)}");
        builder.AppendLine();

        builder.AppendLine("EXTERNAL TRUSTED FINGERPRINT");
        if (trusted is null)
        {
            builder.AppendLine("Status: NOT SUPPLIED");
            builder.AppendLine(
                "Выполнена только внутренняя проверка. Для проверки против отдельной контрольной копии " +
                "передайте *.cranefingerprint.json независимо от ZIP и выберите его при следующей проверке.");
        }
        else
        {
            builder.AppendLine($"Status: {trusted.Status}");
            builder.AppendLine($"File: {trusted.FingerprintPath}");
            foreach (var error in trusted.Errors)
                builder.AppendLine("- " + error);
            if (trusted.IsMatch)
            {
                builder.AppendLine(
                    "Project ID, ZIP size, ZIP SHA-256, internal manifest SHA-256 и имя .canproject " +
                    "совпадают с внешним fingerprint.");
            }
        }
        builder.AppendLine();

        if (result.Files.Count > 0)
        {
            builder.AppendLine("PAYLOAD SHA-256");
            foreach (var file in result.Files)
            {
                builder.AppendLine(
                    $"[{(file.IsMatch ? "OK" : "FAIL")}] {file.EntryName} · {file.ActualLength} bytes");
                builder.AppendLine($"  recorded: {file.RecordedSha256}");
                builder.AppendLine($"  actual:   {file.ActualSha256}");
            }
            builder.AppendLine();
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine("ERRORS");
            foreach (var error in result.Errors)
                builder.AppendLine("- " + error);
            builder.AppendLine();
        }

        if (!result.IsValid)
        {
            builder.AppendLine(
                "INVALID: package не прошёл одну или несколько внутренних проверок.");
        }
        else if (trusted is { IsMatch: false })
        {
            builder.AppendLine(
                "TRUSTED MISMATCH: ZIP внутренне согласован, но не совпадает с выбранным внешним контрольным fingerprint.");
        }
        else if (trusted is { IsMatch: true })
        {
            builder.AppendLine(
                "TRUSTED MATCH: ZIP внутренне валиден и точно совпадает с выбранным внешним контрольным fingerprint.");
        }
        else
        {
            builder.AppendLine(
                "VALID: структура package и все записанные SHA-256/размеры payload совпадают; внешний fingerprint не проверялся.");
        }

        builder.AppendLine();
        builder.AppendLine(
            "Проверка выполнялась напрямую внутри ZIP; project resources не распаковывались и не публиковались.");
        builder.AppendLine(
            "Внешний fingerprint имеет смысл как доверенный только если он получен/хранится отдельно от ZIP по независимому доверенному каналу.");
        builder.AppendLine(
            "Fingerprint и SHA-256 не являются цифровой подписью и сами по себе не доказывают авторство.");
        builder.AppendLine("Операция полностью офлайн и не выполняет CAN Tx.");
        return builder.ToString();
    }
}
