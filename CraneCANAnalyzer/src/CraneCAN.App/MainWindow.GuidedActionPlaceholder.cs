using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace CraneCAN.App;

public partial class MainWindow
{
    private bool _guidedActionPlaceholderInitialized;
    private Brush? _guidedActionNormalBackground;
    private Brush? _guidedActionPlaceholderBackground;

    [ModuleInitializer]
    internal static void InstallGuidedActionPlaceholderInitializer()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(GuidedActionPlaceholderMainWindowLoaded));
    }

    private static void GuidedActionPlaceholderMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeGuidedActionPlaceholder();
    }

    private void InitializeGuidedActionPlaceholder()
    {
        if (_guidedActionPlaceholderInitialized)
            return;

        _guidedActionPlaceholderInitialized = true;
        _guidedActionNormalBackground = GuidedActionNameTextBox.Background;
        _guidedActionPlaceholderBackground = CreateGuidedActionPlaceholderBrush();

        // "Joystick EXTEND" used to be a real default value. It must not be saved as
        // user input: replace only that legacy startup value with an empty TextBox.
        if (string.Equals(GuidedActionNameTextBox.Text, "Joystick EXTEND", StringComparison.Ordinal))
            GuidedActionNameTextBox.Text = string.Empty;

        GuidedActionNameTextBox.GotKeyboardFocus += (_, _) => RefreshGuidedActionPlaceholder();
        GuidedActionNameTextBox.LostKeyboardFocus += (_, _) => RefreshGuidedActionPlaceholder();
        GuidedActionNameTextBox.TextChanged += (_, _) => RefreshGuidedActionPlaceholder();

        RefreshGuidedActionPlaceholder();
    }

    private void RefreshGuidedActionPlaceholder()
    {
        var showPlaceholder =
            string.IsNullOrWhiteSpace(GuidedActionNameTextBox.Text) &&
            !GuidedActionNameTextBox.IsKeyboardFocusWithin;

        GuidedActionNameTextBox.Background = showPlaceholder
            ? _guidedActionPlaceholderBackground
            : _guidedActionNormalBackground;
    }

    private static Brush CreateGuidedActionPlaceholderBrush()
    {
        var hint = new System.Windows.Controls.TextBlock
        {
            Text = "джойстик на выдвижение",
            Foreground = new SolidColorBrush(Color.FromRgb(145, 145, 145)),
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        return new VisualBrush(hint)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Center
        };
    }
}
