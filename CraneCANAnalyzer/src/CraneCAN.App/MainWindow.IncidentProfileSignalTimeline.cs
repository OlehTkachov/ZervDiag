using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private void ShowIncidentProfileSignalTimeline(
        LoadedIncidentPackage package,
        IncidentEventChainStep step,
        Window owner)
    {
        var matches = _machineProfile.KnownSignals
            .Concat(_machineProfile.ExperimentalSignals)
            .Where(signal =>
                signal.Confidence != SignalKnowledgeState.Rejected &&
                IncidentProfileSignalMatchesStepIdentifier(signal, step) &&
                IncidentProfileSignalOverlapsStep(signal, step))
            .GroupBy(signal => signal.SignalId)
            .Select(group => group.First())
            .OrderByDescending(signal =>
                IncidentProfileSignalConfidenceRank(signal.Confidence))
            .ThenBy(signal => signal.Name, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            MessageBox.Show(
                "В текущем Machine Profile нет декодируемого сигнала, поле которого пересекает выбранный DATA[n]. " +
                "Для J1939 поиск выполняется по PGN и не требует совпадения Source Address. " +
                "Сначала используйте «Добавить DATA-шаг в профиль…» либо Cross-Incident → Machine Profile / Import DBC.",
                "Profile signal timeline",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var signal = matches.Length == 1
            ? matches[0]
            : ChooseIncidentProfileSignal(
                matches,
                owner);
        if (signal is null)
            return;

        ShowIncidentProfileSignalTimelineForSignal(
            package,
            step,
            signal,
            owner);
    }

    private static bool IncidentProfileSignalMatchesStepIdentifier(
        MachineSignal signal,
        IncidentEventChainStep step)
    {
        if (signal.IsExtended != step.IsExtended)
            return false;

        if (J1939TraceSignalAnalyzer.IsJ1939Signal(signal))
        {
            try
            {
                return J1939SignalDecoder.MatchesIdentifier(
                    signal,
                    step.Id,
                    step.IsExtended);
            }
            catch
            {
                return false;
            }
        }

        return signal.CanId == step.Id;
    }

    private static bool IncidentProfileSignalOverlapsStep(
        MachineSignal signal,
        IncidentEventChainStep step)
    {
        if (!step.DataIndex.HasValue)
            return false;

        try
        {
            return MachineSignalDecoder.CoversDataByte(
                signal,
                step.DataIndex.Value);
        }
        catch
        {
            // An invalid signal definition is not a valid timeline candidate.
            // The Machine Profile evidence/editor remains responsible for
            // correcting the definition; the selection list does not guess.
            return false;
        }
    }

    private static int IncidentProfileSignalConfidenceRank(
        SignalKnowledgeState state) => state switch
    {
        SignalKnowledgeState.Confirmed => 4,
        SignalKnowledgeState.Probable => 3,
        SignalKnowledgeState.Candidate => 2,
        SignalKnowledgeState.Unknown => 1,
        _ => 0
    };

    private static MachineSignal? ChooseIncidentProfileSignal(
        IReadOnlyList<MachineSignal> signals,
        Window owner)
    {
        var choices = signals
            .Select(signal =>
                new IncidentProfileSignalChoice(signal))
            .ToArray();

        var combo = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath =
                nameof(IncidentProfileSignalChoice.DisplayText),
            SelectedIndex = 0,
            MinWidth = 520,
            Margin = new Thickness(0, 8, 0, 12)
        };

        var info = new TextBlock
        {
            Text =
                "Несколько сигналов Machine Profile пересекают выбранный DATA[n]. Выберите, какой декодировать. Для J1939 PGN/SPN и ByteOrder отображаются явно.",
            TextWrapping = TextWrapping.Wrap
        };

        var open = new Button
        {
            Content = "Открыть график",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var cancel = new Button
        {
            Content = "Отмена",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment =
                HorizontalAlignment.Right
        };
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);

        var layout = new DockPanel
        {
            Margin = new Thickness(14)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        var content = new StackPanel();
        content.Children.Add(info);
        content.Children.Add(combo);
        layout.Children.Add(content);

        var dialog = new Window
        {
            Owner = owner,
            Title = "CraneCAN — выбор Profile signal",
            Width = 700,
            Height = 220,
            MinWidth = 580,
            MinHeight = 200,
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner,
            Content = layout
        };

        MachineSignal? selected = null;
        open.Click += (_, _) =>
        {
            if (combo.SelectedItem is
                IncidentProfileSignalChoice choice)
            {
                selected = choice.Signal;
                dialog.DialogResult = true;
            }
        };
        cancel.Click += (_, _) => dialog.Close();

        return dialog.ShowDialog() == true
            ? selected
            : null;
    }

    private void ShowIncidentProfileSignalTimelineForSignal(
        LoadedIncidentPackage package,
        IncidentEventChainStep step,
        MachineSignal signal,
        Window owner)
    {
        IncidentProfileSignalTimelineResult result;
        J1939IncidentProfileTimelineResult? j1939 = null;
        try
        {
            if (J1939TraceSignalAnalyzer.IsJ1939Signal(signal))
            {
                j1939 = J1939IncidentAnalyzer.AnalyzeProfileTimeline(
                    package.Incident,
                    step,
                    signal);
                result = j1939.Timeline;
            }
            else
            {
                result = IncidentProfileSignalTimelineAnalyzer.Analyze(
                    package.Incident,
                    step,
                    signal);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "График Profile signal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var warnings = result.Warnings.Count == 0
            ? "Предупреждения: нет."
            : "Предупреждения:\n• " +
              string.Join("\n• ", result.Warnings);

        var unit = string.IsNullOrWhiteSpace(signal.Unit)
            ? string.Empty
            : " " + signal.Unit.Trim();
        var protocolDetails = j1939 is null
            ? string.Empty
            : "\nJ1939: " +
              $"PGN 0x{j1939.Pgn:X5}" +
              (j1939.Spn.HasValue ? $" · SPN {j1939.Spn.Value}" : string.Empty) +
              " · observed SA " +
              string.Join(", ", j1939.SourceAddresses.Select(value => $"0x{value:X2}")) +
              ". Matching выполняется по PGN; PDU1 Destination Address фиксирован.";

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Signal: {signal.Name} · {signal.Confidence.ToString().ToUpperInvariant()}\n" +
                $"Observed {(step.IsExtended ? "Extended" : "Standard")} ID 0x{step.Id.ToString(step.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)}; " +
                $"Profile canonical ID 0x{signal.CanId.ToString(signal.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)} · " +
                $"field DATA[{signal.StartByte}] bit {signal.StartBit}, {signal.BitLength} bit, {signal.ByteOrder}, " +
                $"{(signal.IsSigned ? "signed" : "unsigned")} · scale {FormatIncidentProfileNumber(signal.Scale)} · " +
                $"offset {FormatIncidentProfileNumber(signal.Offset)}{unit}." +
                protocolDetails + "\n" +
                $"Кадров: {result.MatchingFrameCount:N0}; декодировано: {result.SourceFrameCount:N0}; " +
                $"короткий DLC: {result.ShortFrameCount:N0}; диапазон: " +
                $"{FormatIncidentProfileNumber(result.MinimumEngineeringValue)} … " +
                $"{FormatIncidentProfileNumber(result.MaximumEngineeringValue)}{unit}.\n" +
                $"Marker = 0.000 s; кандидат перехода = {FormatTimelineSeconds(result.TransitionMilliseconds)}.\n" +
                warnings + "\n" +
                "Engineering value вычислено строго по текущему Machine Profile. " +
                "BigEndian использует явно определённую DBC/Motorola sawtooth convention. " +
                "PGN/SPN/engineering-аннотация не доказывает физическое назначение сигнала; raw DATA-график остаётся независимым evidence."
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

        void Redraw() =>
            DrawIncidentProfileSignalTimeline(
                canvas,
                result,
                unit);

        canvas.Loaded += (_, _) => Redraw();
        canvas.SizeChanged += (_, _) => Redraw();

        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            HorizontalAlignment =
                HorizontalAlignment.Right
        };

        var layout = new Grid
        {
            Margin = new Thickness(12)
        };
        layout.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(
                    1,
                    GridUnitType.Star)
            });
        layout.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(summary, 0);
        Grid.SetRow(border, 1);
        Grid.SetRow(close, 2);
        layout.Children.Add(summary);
        layout.Children.Add(border);
        layout.Children.Add(close);

        var window = new Window
        {
            Owner = owner,
            Title = "CraneCAN — Profile signal timeline",
            Width = 1080,
            Height = 680,
            MinWidth = 760,
            MinHeight = 500,
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner,
            Content = layout
        };

        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static void DrawIncidentProfileSignalTimeline(
        Canvas canvas,
        IncidentProfileSignalTimelineResult result,
        string unitSuffix)
    {
        canvas.Children.Clear();

        var width = Math.Max(160, canvas.ActualWidth);
        var height = Math.Max(160, canvas.ActualHeight);
        const double left = 78;
        const double right = 18;
        const double top = 18;
        const double bottom = 42;

        var plotWidth =
            Math.Max(1, width - left - right);
        var plotHeight =
            Math.Max(1, height - top - bottom);

        var xMin = result.StartMilliseconds;
        var xMax = result.EndMilliseconds;
        if (xMax <= xMin)
        {
            xMin -= 1;
            xMax += 1;
        }

        var yMin = result.MinimumEngineeringValue;
        var yMax = result.MaximumEngineeringValue;
        if (Math.Abs(yMax - yMin) < 1e-12)
        {
            var padding = Math.Max(
                1.0,
                Math.Abs(yMin) * 0.01);
            yMin -= padding;
            yMax += padding;
        }

        double X(double milliseconds) =>
            left +
            (milliseconds - xMin) /
            (xMax - xMin) *
            plotWidth;
        double Y(double value) =>
            top +
            (yMax - value) /
            (yMax - yMin) *
            plotHeight;

        AddTimelineLine(
            canvas,
            left,
            top,
            left,
            top + plotHeight,
            SystemColors.ControlTextBrush,
            1);
        AddTimelineLine(
            canvas,
            left,
            top + plotHeight,
            left + plotWidth,
            top + plotHeight,
            SystemColors.ControlTextBrush,
            1);

        AddTimelineLabel(
            canvas,
            FormatIncidentProfileNumber(
                result.MaximumEngineeringValue) +
            unitSuffix,
            4,
            top - 8);
        AddTimelineLabel(
            canvas,
            FormatIncidentProfileNumber(
                result.MinimumEngineeringValue) +
            unitSuffix,
            4,
            top + plotHeight - 10);
        AddTimelineLabel(
            canvas,
            FormatTimelineSeconds(xMin),
            left - 18,
            top + plotHeight + 8);
        AddTimelineLabel(
            canvas,
            FormatTimelineSeconds(xMax),
            left + plotWidth - 52,
            top + plotHeight + 8);

        if (xMin <= 0 && xMax >= 0)
        {
            var markerX = X(0);
            AddTimelineLine(
                canvas,
                markerX,
                top,
                markerX,
                top + plotHeight,
                Brushes.Gray,
                1);
            AddTimelineLabel(
                canvas,
                "MARKER",
                markerX + 3,
                top + 2);
        }

        if (result.TransitionMilliseconds >= xMin &&
            result.TransitionMilliseconds <= xMax)
        {
            var transitionX =
                X(result.TransitionMilliseconds);
            AddTimelineLine(
                canvas,
                transitionX,
                top,
                transitionX,
                top + plotHeight,
                Brushes.DarkOrange,
                1.5);
            AddTimelineLabel(
                canvas,
                "CHANGE",
                transitionX + 3,
                top + 18);
        }

        var points =
            new PointCollection(result.RenderedPoints.Count);
        foreach (var point in result.RenderedPoints)
        {
            points.Add(
                new Point(
                    X(point.RelativeMilliseconds),
                    Y(point.EngineeringValue)));
        }

        canvas.Children.Add(new Polyline
        {
            Stroke = SystemColors.HighlightBrush,
            StrokeThickness = 1.5,
            Points = points
        });
    }

    private static string FormatIncidentProfileNumber(
        double value) =>
        value.ToString(
            "0.###",
            CultureInfo.InvariantCulture);

    private sealed record IncidentProfileSignalChoice(
        MachineSignal Signal)
    {
        public string DisplayText =>
            $"{(string.IsNullOrWhiteSpace(Signal.Name) ? "(без имени)" : Signal.Name)} · " +
            $"{Signal.Confidence.ToString().ToUpperInvariant()} · " +
            J1939Text +
            $"DATA[{Signal.StartByte}] bit {Signal.StartBit}, {Signal.BitLength} bit · " +
            $"{Signal.ByteOrder} · " +
            $"{(Signal.IsSigned ? "signed" : "unsigned")} · " +
            $"scale {FormatIncidentProfileNumber(Signal.Scale)}, offset {FormatIncidentProfileNumber(Signal.Offset)}";

        private string J1939Text => J1939TraceSignalAnalyzer.IsJ1939Signal(Signal)
            ? $"PGN 0x{Signal.J1939Pgn!.Value:X5}" +
              (Signal.J1939Spn.HasValue ? $" / SPN {Signal.J1939Spn.Value}" : string.Empty) +
              " · "
            : string.Empty;
    }
}
