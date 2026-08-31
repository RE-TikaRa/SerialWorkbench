using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class ConnectionsPage : Page
{
    private bool suppressSelection;

    public ConnectionsPage() => InitializeComponent();

    public ObservableCollection<ConnectionRow> Connections { get; } = [];

    public ConnectionRow? SelectedConnection => ConnectionList.SelectedItem as ConnectionRow;

    public SerialPortDescriptor? SelectedPort => PortComboBox.SelectedItem as SerialPortDescriptor;

    public int RoleIndex => RoleComboBox.SelectedIndex;

    public int BaudRate => checked((int)BaudRateNumberBox.Value);

    public event EventHandler? RefreshRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler<Guid>? ConnectionSelected;
    public event EventHandler<Guid>? CloseRequested;

    public void SetConnections(IReadOnlyList<ConnectionSnapshot> snapshots, Guid? selectedId)
    {
        suppressSelection = true;
        Connections.Clear();
        foreach (var snapshot in snapshots)
        {
            Connections.Add(ConnectionRow.From(snapshot, snapshot.Id == selectedId));
        }

        ConnectionList.SelectedItem = Connections.FirstOrDefault(item => item.Id == selectedId);
        suppressSelection = false;
        EmptyState.Visibility = Connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = SelectedConnection is not null;
    }

    public void SetPorts(IReadOnlyList<SerialPortDescriptor> ports)
    {
        var selectedName = SelectedPort?.PortName;
        PortComboBox.ItemsSource = ports;
        SerialPortDescriptor? selected = null;
        if (selectedName is not null)
        {
            foreach (var port in ports)
            {
                if (port.PortName.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                {
                    selected = port;
                    break;
                }
            }
        }

        PortComboBox.SelectedItem = selected ?? (ports.Count > 0 ? ports[0] : null);
        OpenButton.IsEnabled = SelectedPort is not null;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is { } connection)
        {
            CloseRequested?.Invoke(this, connection.Id);
        }
    }

    private void ConnectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CloseButton.IsEnabled = SelectedConnection is not null;
        if (!suppressSelection && SelectedConnection is { } connection)
        {
            ConnectionSelected?.Invoke(this, connection.Id);
        }
    }
}

public sealed class ConnectionRow
{
    public Guid Id { get; private init; }
    public string Name { get; private init; } = "";
    public string Device { get; private init; } = "";
    public string Status { get; private init; } = "";
    public string Counters { get; private init; } = "";
    public string ControlLines { get; private init; } = "";

    public static ConnectionRow From(ConnectionSnapshot snapshot, bool selected)
    {
        var role = snapshot.Options.Role.ToString().ToUpperInvariant();
        var device = snapshot.Options.DeviceInstanceId is { Length: > 0 } instanceId
            ? $"{snapshot.Options.PortName} · {instanceId}"
            : snapshot.Options.PortName;
        var status = selected ? $"{snapshot.State} · 当前" : snapshot.State.ToString();
        var counters = $"RX {snapshot.ReceivedBytes:N0} · TX {snapshot.TransmittedBytes:N0}";
        var lines = snapshot.ControlLines is { } controlLines
            ? $"CTS {(controlLines.CtsHolding ? 1 : 0)} · DSR {(controlLines.DsrHolding ? 1 : 0)} · DCD {(controlLines.CarrierDetect ? 1 : 0)} · RI {(controlLines.RingIndicator is { } ring ? ring ? "1" : "0" : "-")}"
            : "CTS - · DSR - · DCD - · RI -";
        return new ConnectionRow
        {
            Id = snapshot.Id,
            Name = $"{role} · {snapshot.Options.BaudRate:N0} baud",
            Device = device,
            Status = status,
            Counters = counters,
            ControlLines = lines,
        };
    }
}
