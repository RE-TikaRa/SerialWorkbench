using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class ConnectionsPage : Page
{
    private bool suppressSelection;

    public ConnectionsPage()
    {
        InitializeComponent();
        BaudRateNumberBox.ValueChanged += NumberBoxInput.KeepLastValue;
    }

    public ObservableCollection<ConnectionRow> Connections { get; } = [];

    public ConnectionRow? SelectedConnection => ConnectionList.SelectedItem as ConnectionRow;

    public SerialPortDescriptor? SelectedPort => PortComboBox.SelectedItem as SerialPortDescriptor;

    public int RoleIndex => RoleComboBox.SelectedIndex;

    public int BaudRate => checked((int)BaudRateNumberBox.Value);

    private void ConnectionsPage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        VisualStateManager.GoToState(this, e.NewSize.Width < 641 ? "Compact" : "Wide", false);

    public event EventHandler? RefreshRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler<Guid>? ConnectionSelected;
    public event EventHandler<Guid>? CloseRequested;

    public void SetConnections(IReadOnlyList<ConnectionSnapshot> snapshots, Guid? selectedId)
    {
        suppressSelection = true;
        for (var index = Connections.Count - 1; index >= 0; index--)
        {
            if (snapshots.All(item => item.Id != Connections[index].Id))
            {
                Connections.RemoveAt(index);
            }
        }

        foreach (var snapshot in snapshots)
        {
            if (Connections.FirstOrDefault(item => item.Id == snapshot.Id) is { } row)
            {
                row.Update(snapshot, snapshot.Id == selectedId);
            }
            else
            {
                Connections.Add(ConnectionRow.From(snapshot, snapshot.Id == selectedId));
            }
        }

        ConnectionList.SelectedItem = Connections.FirstOrDefault(item => item.Id == selectedId);
        suppressSelection = false;
        EmptyState.Visibility = Connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = SelectedConnection is not null;
    }

    public void SetHostMetrics(long latestSequence, long pendingEvents, double persistenceEventsPerSecond = 0) =>
        HostMetricsText.Text = $"事件序号 {latestSequence:N0} · 持久化队列 {pendingEvents:N0} · 写入 {persistenceEventsPerSecond:N0} 事件/s";

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

public sealed class ConnectionRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; private init; }
    public string Name { get; private set; } = "";
    public string Device { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string Counters { get; private set; } = "";
    public string ControlLines { get; private set; } = "";
    public string Rates { get; private set; } = "";
    public string Diagnostics { get; private set; } = "";

    public static ConnectionRow From(ConnectionSnapshot snapshot, bool selected)
    {
        var row = new ConnectionRow { Id = snapshot.Id };
        row.Update(snapshot, selected);
        return row;
    }

    public void Update(ConnectionSnapshot snapshot, bool selected)
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
        var rates = $"RX {FormatRate(snapshot.ReceivedBytesPerSecond)} · TX {FormatRate(snapshot.TransmittedBytesPerSecond)}";
        var diagnostics = $"丢弃 {snapshot.ObserverDroppedBlocks:N0} 块";
        Name = $"{role} · {snapshot.Options.BaudRate:N0} baud";
        Device = device;
        Status = status;
        Counters = counters;
        ControlLines = lines;
        Rates = rates;
        Diagnostics = diagnostics;
        foreach (var name in (string[])[nameof(Name), nameof(Device), nameof(Status), nameof(Counters), nameof(ControlLines), nameof(Rates), nameof(Diagnostics)])
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static string FormatRate(double value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024d / 1024d:N1} MiB/s",
        >= 1024 => $"{value / 1024d:N1} KiB/s",
        _ => $"{value:N0} B/s",
    };
}
