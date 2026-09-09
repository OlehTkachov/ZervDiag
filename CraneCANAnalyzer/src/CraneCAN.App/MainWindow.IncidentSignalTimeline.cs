using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private void ShowIncidentSignalTimeline(
        LoadedIncidentPackage package,
        IncidentEventChainStep step,
        Window owner)
    {
        IncidentSignalTimelineResult result;
        try
        {
            result = IncidentSignalTimelineAnalyzer.Analyze(
                package.Incident,
                step);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "График incident DATA",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var warnings = result.Warnings.Count == 0
            ? "Предупреждения: нет."
            : "Предупреждения:\n• " + string.Join("\n• ", result.Warnings);

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"{(result.IsExtended ? "Extended" : "Standard")} ID 0x{result.Id:X} · DATA[{result.DataIndex}]\n" +
                $"Кадров: {result.SourceFrameCount:N0}; отображено: {result.RenderedPoints.Count:N0}; " +
                $"raw min/max: {result.MinimumRawValue} / {result.MaximumRawValue}; " +
                $"окно: {FormatTimelineSeconds(result.StartMilliseconds)} … {FormatTimelineSeconds(result.EndMilliseconds)}.\n" +
                $"Marker = 0.000 s; кандидат перехода = {FormatTimelineSeconds(result.TransitionMilliseconds)}.\n" +
                warnings + "\n" +
                "График показывает raw байт 0…255. Он не применяет scale/offset Machine Profile и не доказывает физическое назначение сигнала."
        };

        var canvas = new Canvas
        {
            MinHeight = 360,
            Background = SystemColors.WindowBrush,
            ClipToBounds = true
        };
        var border = new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Child = canvas
        };

        void Redraw()
        {
            DrawIncidentTimeline(canvas, result);
        }

        canvas.Loaded += (_, _) => Redraw();
        canvas.SizeChanged += (_, _) => Redraw();

        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(border, 1);
        Grid.SetRow(close, 2);
        layout.Children.Add(summary);
        layout.Children.Add(border);
        layout.Children.Add(close);

        var window = new Window
        {
            Owner = owner,
            Title = "CraneCAN — график incident DATA",
            Width = 1080,
            Height = 620,
            MinWidth = 760,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static void DrawIncidentTimeline(
        Canvas canvas,
        IncidentSignalTimelineResult result)
    {
        canvas.Children.Clear();

        var width = Math.Max(160, canvas.ActualWidth);
        var height = Math.Max(160, canvas.ActualHeight);
        const double left = 58;
        const double right = 18;
        const double top = 18;
        const double bottom = 42;

        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);

        var xMin = result.StartMilliseconds;
        var xMax = result.EndMilliseconds;
        if (xMax <= xMin)
        {
            xMin -= 1;
            xMax += 1;
        }

        double yMin = result.MinimumRawValue;
        double yMax = result.MaximumRawValue;
        if (Math.Abs(yMax - yMin) < 0.001)
        {
            yMin = Math.Max(0, yMin - 1);
            yMax = Math.Min(255, yMax + 1);
            if (Math.Abs(yMax - yMin) < 0.001)
            {
                yMin = Math.Max(0, yMin - 1);
                yMax = yMin + 1;
            }
        }

        double X(double milliseconds) =>
            left + (milliseconds - xMin) / (xMax - xMin) * plotWidth;
        double Y(double value) =>
            top + (yMax - value) / (yMax - yMin) * plotHeight;

        AddTimelineLine(canvas, left, top, left, top + plotHeight, SystemColors.ControlTextBrush, 1);
        AddTimelineLine(canvas, left, top + plotHeight, left + plotWidth, top + plotHeight, SystemColors.ControlTextBrush, 1);

        AddTimelineLabel(canvas, result.MaximumRawValue.ToString(), 4, top - 8);
        AddTimelineLabel(canvas, result.MinimumRawValue.ToString(), 4, top + plotHeight - 10);
        AddTimelineLabel(canvas, FormatTimelineSeconds(xMin), left - 18, top + plotHeight + 8);
        AddTimelineLabel(canvas, FormatTimelineSeconds(xMax), left + plotWidth - 52, top + plotHeight + 8);

        if (xMin <= 0 && xMax >= 0)
        {
            var markerX = X(0);
            AddTimelineLine(canvas, markerX, top, markerX, top + plotHeight, Brushes.Gray, 1);
            AddTimelineLabel(canvas, "MARKER", markerX + 3, top + 2);
        }

        if (result.TransitionMilliseconds >= xMin &&
            result.TransitionMilliseconds <= xMax)
        {
            var transitionX = X(result.TransitionMilliseconds);
            AddTimelineLine(canvas, transitionX, top, transitionX, top + plotHeight, Brushes.DarkOrange, 1.5);
            AddTimelineLabel(canvas, "CHANGE", transitionX + 3, top + 18);
        }

        var points = new PointCollection(result.RenderedPoints.Count);
        foreach (var point in result.RenderedPoints)
            points.Add(new Point(X(point.RelativeMilliseconds), Y(point.RawValue)));

        canvas.Children.Add(new Polyline
        {
            Stroke = SystemColors.HighlightBrush,
            StrokeThickness = 1.5,
            Points = points
        });
    }

    private static void AddTimelineLine(
        Canvas canvas,
        double x1,
        double y1,
        double x2,
        double y2,
        Brush brush,
        double thickness)
    {
        canvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness
        });
    }

    private static void AddTimelineLabel(
        Canvas canvas,
        string text,
        double left,
        double top)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = SystemColors.ControlTextBrush,
            FontSize = 11
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        canvas.Children.Add(label);
    }

    private static string FormatTimelineSeconds(double milliseconds)
    {
        var seconds = milliseconds / 1000.0;
        return seconds >= 0 ? $"+{seconds:0.000} s" : $"{seconds:0.000} s";
    }
}
