using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CraneCAN.App;

public partial class MainWindow
{
    private TabControl BuildNetworkDetailsTabs()
    {
        var tabs = new TabControl();
        _networkNodesGrid = CreateNetworkGrid();
        _networkFlowsGrid = CreateNetworkGrid();
        _networkStreamsGrid = CreateNetworkGrid();
        _networkEventsGrid = CreateNetworkGrid();

        AddNodeColumns(_networkNodesGrid);
        AddFlowColumns(_networkFlowsGrid);
        AddStreamColumns(_networkStreamsGrid);
        AddEventColumns(_networkEventsGrid);

        tabs.Items.Add(new TabItem { Header = "Узлы", Content = _networkNodesGrid });
        tabs.Items.Add(new TabItem { Header = "Потоки", Content = _networkFlowsGrid });
        tabs.Items.Add(new TabItem { Header = "Периодические ID", Content = _networkStreamsGrid });
        tabs.Items.Add(new TabItem { Header = "События", Content = _networkEventsGrid });
        return tabs;
    }

    private static DataGrid CreateNetworkGrid() => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        EnableRowVirtualization = true,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow
    };

    private static void AddNodeColumns(DataGrid grid)
    {
        AddNetworkColumn(grid, "Протокол", nameof(NetworkNodeRow.Protocol), 90);
        AddNetworkColumn(grid, "Узел / SA", nameof(NetworkNodeRow.Address), 105);
        AddNetworkColumn(grid, "Identity", nameof(NetworkNodeRow.Identity), 190);
        AddNetworkColumn(grid, "State", nameof(NetworkNodeRow.State), 120);
        AddNetworkColumn(grid, "Health", nameof(NetworkNodeRow.Health), 90);
        AddNetworkColumn(grid, "Кадры", nameof(NetworkNodeRow.Frames), 85);
        AddNetworkColumn(grid, "Hz", nameof(NetworkNodeRow.Frequency), 75);
        AddNetworkColumn(grid, "Периодичность", nameof(NetworkNodeRow.Periodicity), 105);
        AddNetworkColumn(grid, "PGN / COB-ID", nameof(NetworkNodeRow.Messages), 240);
        AddNetworkColumn(grid, "Связи", nameof(NetworkNodeRow.Peers), 150);
        AddNetworkColumn(grid, "Confidence", nameof(NetworkNodeRow.Confidence), 90);
        AddNetworkColumn(grid, "Evidence", nameof(NetworkNodeRow.Evidence), 1, true);
    }

    private static void AddFlowColumns(DataGrid grid)
    {
        AddNetworkColumn(grid, "SA", nameof(NetworkFlowRow.Source), 80);
        AddNetworkColumn(grid, "DA", nameof(NetworkFlowRow.Destination), 80);
        AddNetworkColumn(grid, "Тип", nameof(NetworkFlowRow.Kind), 100);
        AddNetworkColumn(grid, "PGN", nameof(NetworkFlowRow.Pgns), 220);
        AddNetworkColumn(grid, "Кадры", nameof(NetworkFlowRow.Frames), 90);
        AddNetworkColumn(grid, "Hz", nameof(NetworkFlowRow.Frequency), 90);
        AddNetworkColumn(grid, "Первый", nameof(NetworkFlowRow.FirstSeen), 165);
        AddNetworkColumn(grid, "Последний", nameof(NetworkFlowRow.LastSeen), 165);
    }

    private static void AddStreamColumns(DataGrid grid)
    {
        AddNetworkColumn(grid, "ID", nameof(NetworkStreamRow.Id), 105);
        AddNetworkColumn(grid, "Формат", nameof(NetworkStreamRow.Format), 80);
        AddNetworkColumn(grid, "Кадры", nameof(NetworkStreamRow.Frames), 80);
        AddNetworkColumn(grid, "Медиана, мс", nameof(NetworkStreamRow.Median), 105);
        AddNetworkColumn(grid, "Jitter", nameof(NetworkStreamRow.Jitter), 85);
        AddNetworkColumn(grid, "Timeout, мс", nameof(NetworkStreamRow.Timeout), 105);
        AddNetworkColumn(grid, "Класс", nameof(NetworkStreamRow.Classification), 150);
        AddNetworkColumn(grid, "Последний", nameof(NetworkStreamRow.LastSeen), 175);
    }

    private static void AddEventColumns(DataGrid grid)
    {
        AddNetworkColumn(grid, "Время", nameof(NetworkEventRow.Timestamp), 175);
        AddNetworkColumn(grid, "Событие", nameof(NetworkEventRow.Kind), 170);
        AddNetworkColumn(grid, "Узел", nameof(NetworkEventRow.Node), 120);
        AddNetworkColumn(grid, "ID", nameof(NetworkEventRow.Id), 105);
        AddNetworkColumn(grid, "Описание", nameof(NetworkEventRow.Description), 1, true);
    }

    private static void AddNetworkColumn(DataGrid grid, string header, string property, double width, bool star = false)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            Width = star ? new DataGridLength(width, DataGridLengthUnitType.Star) : new DataGridLength(width)
        });
    }
}
