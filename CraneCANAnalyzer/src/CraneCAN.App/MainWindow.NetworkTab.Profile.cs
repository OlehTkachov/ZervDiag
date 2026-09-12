using System.Windows;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async void SaveNetworkToProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_networkSnapshot is null) return;

        try
        {
            _machineProfile = _machineProfile with
            {
                Network = MachineNetworkKnowledge.MergeAutomatic(_machineProfile.Network, _networkSnapshot),
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(_machineProfilePath))
            {
                await GuidedJsonCodec.SaveProfileAsync(_machineProfilePath, _machineProfile);
                StatusText.Text = "Пассивно наблюдаемая карта сети добавлена в Machine Profile и сохранена.";
            }
            else
            {
                StatusText.Text = "Карта сети добавлена в текущий Machine Profile. Сохраните профиль, чтобы записать её на диск.";
            }

            MessageBox.Show(
                "Наблюдаемые узлы и логические потоки добавлены как автоматические evidence. " +
                "Документированные и подтверждённые пользователем данные не перезаписываются.",
                "Machine Profile",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Machine Profile",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
