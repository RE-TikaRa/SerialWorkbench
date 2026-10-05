using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Application;
using SerialWorkbench.WinUI;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class TerminalPage : Page
{
    private readonly ObservableCollection<string> history = [];
    private readonly TrafficCopyMenu copyMenu;
    private HashSet<TrafficRowIdentity> retainedSelection = [];
    private ObservableCollection<TrafficRow>? sourceRows;

    public TerminalPage()
    {
        InitializeComponent();
        copyMenu = new TrafficCopyMenu(TerminalList);
        HistoryComboBox.ItemsSource = history;
    }

    public event EventHandler? SendRequested;
    public event EventHandler? ClearRequested;

    public ObservableCollection<TrafficRow> Rows { get; } = [];

    public string InputText => InputEditor.Text;

    public int InputFormatIndex => InputFormat.SelectedIndex;

    public int LineEndingIndex => LineEnding.SelectedIndex;

    private void TerminalPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        VisualStateManager.GoToState(this, e.NewSize.Width < 641 ? "Compact" : "Wide", false);
        InputEditor.MaxHeight = Math.Max(InputEditor.MinHeight, e.NewSize.Height / 3);
    }

    public void BindRows(ObservableCollection<TrafficRow> source)
    {
        if (sourceRows is not null)
        {
            sourceRows.CollectionChanged -= SourceRows_CollectionChanged;
        }

        sourceRows = source;
        retainedSelection.Clear();
        TrafficSelection.UpdateRows(Rows, source);

        sourceRows.CollectionChanged += SourceRows_CollectionChanged;
        UpdateEmptyState();
        ScrollLatest();
    }

    private void SourceRows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            retainedSelection = copyMenu.CaptureSelection();
            Rows.Clear();
        }

        if (e.OldItems is not null)
        {
            foreach (TrafficRow row in e.OldItems)
            {
                Rows.Remove(row);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (TrafficRow row in e.NewItems)
            {
                Rows.Add(row);
                if (retainedSelection.Remove(row.Identity))
                {
                    TerminalList.SelectedItems.Add(row);
                }
            }
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

    public void HideSendResult() => SendStatus.IsOpen = false;

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendRequested?.Invoke(this, EventArgs.Empty);

    private void InputEditor_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter
            && SendButton.IsEnabled
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            SendRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearInputButton_Click(object sender, RoutedEventArgs e) => InputEditor.Text = "";

    private void CopyOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(string.Join(Environment.NewLine, Rows.Select(static item => item.Hex)));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void ClearOutputButton_Click(object sender, RoutedEventArgs e)
    {
        ClearRequested?.Invoke(this, EventArgs.Empty);
        retainedSelection.Clear();
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
        if (Rows.Count > 0 && !copyMenu.HasSelection)
        {
            TerminalList.ScrollIntoView(Rows[^1]);
        }
    }
}
