using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.WinUI;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class TerminalPage : Page
{
    private readonly ObservableCollection<string> history = [];

    public TerminalPage()
    {
        InitializeComponent();
        HistoryComboBox.ItemsSource = history;
    }

    public event EventHandler? SendRequested;

    public ObservableCollection<TrafficRow> Rows { get; } = [];

    public string InputText => InputEditor.Text;

    public int InputFormatIndex => InputFormat.SelectedIndex;

    public int LineEndingIndex => LineEnding.SelectedIndex;

    public void BindRows(IEnumerable<TrafficRow> source)
    {
        Rows.Clear();
        foreach (var row in source)
        {
            Rows.Add(row);
        }

        UpdateEmptyState();
        ScrollLatest();
    }

    public void AppendRow(TrafficRow row)
    {
        Rows.Add(row);
        while (Rows.Count > 20_000)
        {
            Rows.RemoveAt(0);
        }

        UpdateEmptyState();
        ScrollLatest();
    }

    public void AddHistory(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        history.Remove(value);
        history.Insert(0, value);
        while (history.Count > 20)
        {
            history.RemoveAt(history.Count - 1);
        }
    }

    public void SetSending(bool sending) => SendButton.IsEnabled = !sending;

    public void ShowSendResult(string message, InfoBarSeverity severity)
    {
        SendStatus.Title = severity == InfoBarSeverity.Success ? "发送完成" : "发送";
        SendStatus.Message = message;
        SendStatus.Severity = severity;
        SendStatus.IsOpen = true;
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendRequested?.Invoke(this, EventArgs.Empty);

    private void ClearInputButton_Click(object sender, RoutedEventArgs e) => InputEditor.Text = "";

    private void CopyOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(string.Join(Environment.NewLine, Rows.Select(static item => item.Hex)));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void ClearOutputButton_Click(object sender, RoutedEventArgs e)
    {
        Rows.Clear();
        UpdateEmptyState();
    }

    private void HistoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryComboBox.SelectedItem is string value)
        {
            InputEditor.Text = value;
        }
    }

    private void UpdateEmptyState() => EmptyState.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ScrollLatest()
    {
        if (Rows.Count > 0)
        {
            TerminalList.ScrollIntoView(Rows[^1]);
        }
    }
}
