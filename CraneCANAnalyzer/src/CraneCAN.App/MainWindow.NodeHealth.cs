using System.Windows;
using CraneCAN.Core.Live;

namespace CraneCAN.App;

public partial class MainWindow
{
    private string _nodeHealthLastEventText = string.Empty;

    private void HandleNodeHealthEvent(LiveCanReceiver receiver, NodeHealthEvent healthEvent)
    {
        if (!ReferenceEquals(receiver, _liveReceiver)) return;

        var id = healthEvent.IsExtended ? $"0x{healthEvent.Id:X8} EXT" : $"0x{healthEvent.Id:X3} STD";
        var protocolContext = DescribeNodeHealthProtocolContext(receiver, healthEvent);
        _nodeHealthLastEventText = healthEvent.Kind == NodeHealthEventKind.Timeout
            ? $"Таймаут {id}: последний кадр {healthEvent.LastSeen.ToLocalTime():HH:mm:ss.fff}, " +
              $"ожидался период ≈ {healthEvent.ExpectedPeriod.TotalMilliseconds:0.###} мс.{protocolContext}"
            : $"Восстановление {id}: поток сообщений снова появился.{protocolContext}";

        if (healthEvent.Kind == NodeHealthEventKind.Timeout &&
            NodeHealthAutoTriggerCheckBox.IsChecked == true)
        {
            RetainIncident();
            var active = receiver.Incidents.IsCollecting;

            if (!active &&
                _retainedIncident is { } previous &&
                _savedIncidentId != previous.IncidentId)
            {
                _nodeHealthLastEventText +=
                    " Автозапись пропущена: предыдущий incident ещё не сохранён.";
                UpdateNodeHealthDisplay();
                return;
            }

            if (active &&
                receiver.Incidents.ActiveWindowEnd is { } activeEnd &&
                healthEvent.DetectedAt >= activeEnd)
            {
                _nodeHealthLastEventText +=
                    " Автозапись пропущена: активное incident-окно уже закончилось по времени, но поток ещё не завершил его кадром.";
                UpdateNodeHealthDisplay();
                return;
            }

            try
            {
                receiver.Incidents.MarkAt(
                    healthEvent.DetectedAt,
                    $"AUTO NODE HEALTH · {id} · timeout {healthEvent.Timeout.TotalMilliseconds:0.###} ms{protocolContext}");
                _nodeHealthLastEventText +=
                    active ? " Отметка добавлена в активный incident." : " Автоматическая запись −10 / +5 с запущена.";
                UpdateIncidentDisplay();
            }
            catch (Exception exception)
            {
                _nodeHealthLastEventText += $" Автотриггер не выполнен: {FormatException(exception)}";
            }
        }

        UpdateNodeHealthDisplay();
    }

    private void UpdateNodeHealthDisplay()
    {
        if (NodeHealthStatusText is null || NodeHealthAutoTriggerCheckBox is null) return;

        var receiver = _liveReceiver;
        if (receiver is null)
        {
            NodeHealthStatusText.Text = "Node Health: нет активного потока.";
            return;
        }

        var summary = receiver.NodeHealth.Summary;
        var mode = NodeHealthAutoTriggerCheckBox.IsChecked == true
            ? "автотриггер включён"
            : "автотриггер выключен";

        NodeHealthStatusText.Text =
            $"Node Health: ID {summary.TrackedNodes}; периодических {summary.ArmedNodes}; " +
            $"в таймауте {summary.TimedOutNodes}; {mode}.";

        if (!string.IsNullOrWhiteSpace(_nodeHealthLastEventText))
            NodeHealthStatusText.Text += "\n" + _nodeHealthLastEventText;
    }
}
