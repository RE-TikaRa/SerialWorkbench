using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class SessionsPage : Page
{
    private bool suppressSelection;
    private bool replayRunning;
    private readonly List<SessionRow> allSessions = [];

    public SessionsPage() => InitializeComponent();

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    public ObservableCollection<SessionEventRow> Events { get; } = [];

    public ObservableCollection<LoopbackHistoryRow> LoopbackResults { get; } = [];

    public SessionRow? SelectedSession => SessionList.SelectedItem as SessionRow;

    public event EventHandler? RefreshRequested;

    public event EventHandler<Guid>? SessionSelected;

    public event EventHandler<SessionEventFilter>? EventFilterChanged;

    public event EventHandler<string>? RevealRequested;

    public event EventHandler<Guid>? ExportRequested;

    public event EventHandler<Guid>? ReplayRequested;

    public event EventHandler<Guid>? ReplayPauseRequested;

    public event EventHandler<Guid>? ReplayStopRequested;

    public event EventHandler<Guid>? DeleteRequested;

    public void SetWorkspace(string path) => WorkspacePath.Text = path;

    public SessionEventFilter EventFilter => new(
        EventDirectionFilter.SelectedIndex switch
        {
            1 => SerialDirection.Receive,
            2 => SerialDirection.Transmit,
            _ => null,
        },
        EventSourceFilter.Text.Trim() is { Length: > 0 } source ? source : null,
        EventHexFilter.Text.Trim() is { Length: > 0 } hex ? hex : null);

    public Guid? SetSessions(IReadOnlyList<SessionDescriptor> sessions, Guid? activeSessionId)
    {
        var selectedId = (SessionList.SelectedItem as SessionRow)?.Id;
        suppressSelection = true;
        allSessions.Clear();
        Sessions.Clear();
        foreach (var session in sessions)
        {
            allSessions.Add(SessionRow.From(session, session.Id == activeSessionId));
        }

        ApplyFilter();

        var selected = Sessions.FirstOrDefault(item => item.Id == selectedId) ?? Sessions.FirstOrDefault();
        SessionList.SelectedItem = selected;
        suppressSelection = false;
        SessionsEmptyState.Visibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection(selected);
        return selected?.Id;
    }

    public void SetEvents(Guid sessionId, IReadOnlyList<SerialTrafficEvent> events)
    {
        if ((SessionList.SelectedItem as SessionRow)?.Id != sessionId)
        {
            return;
        }

        Events.Clear();
        foreach (var item in events)
        {
            Events.Add(SessionEventRow.From(item));
        }

        var session = (SessionRow)SessionList.SelectedItem;
        EventTitle.Text = session.EventCount > events.Count
            ? $"{session.Title} · 最近 {events.Count:N0} / {session.EventCount:N0} 条"
            : $"{session.Title} · {events.Count:N0} 条";
        EventsEmptyState.Text = events.Count == 0 ? "该会话没有报文" : "";
        EventsEmptyState.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetLoopbackResults(Guid sessionId, IReadOnlyList<LoopbackHistoryEntry> results)
    {
        if (SelectedSession?.Id != sessionId)
        {
            return;
        }

        LoopbackResults.Clear();
        foreach (var result in results)
        {
            LoopbackResults.Add(LoopbackHistoryRow.From(result));
        }

        LoopbackEmptyState.Visibility = LoopbackResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetBusy(bool busy)
    {
        LoadingRing.IsActive = busy;
        RefreshSessionsButton.IsEnabled = !busy;
    }

    public void SetEventsLoading(Guid sessionId)
    {
        if ((SessionList.SelectedItem as SessionRow)?.Id != sessionId)
        {
            return;
        }

        Events.Clear();
        LoopbackResults.Clear();
        LoopbackEmptyState.Visibility = Visibility.Visible;
        EventTitle.Text = "正在读取会话…";
        EventsEmptyState.Visibility = Visibility.Collapsed;
    }

    public void SetReplayState(Guid sessionId, bool running, bool paused, int current, long sequence, int total)
    {
        if (SelectedSession?.Id != sessionId)
        {
            return;
        }

        replayRunning = running;
        SessionList.IsEnabled = !running;
        ReplaySessionButton.IsEnabled = !running;
        ReplayPauseButton.IsEnabled = running;
        ReplayStopButton.IsEnabled = running;
        ReplayPauseIcon.Symbol = paused ? Symbol.Play : Symbol.Pause;
        ReplayProgressBar.Maximum = Math.Max(total, 1);
        ReplayProgressBar.Value = Math.Clamp(current, 0, Math.Max(total, 1));
        ReplayProgressBar.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReplayStatusText.Text = running
            ? $"{(paused ? "已暂停" : "正在回放")} · {current:N0}/{total:N0} · 序号 {sequence}"
            : current == 0
                ? ""
                : $"{(current == total ? "回放完成" : "已停止")} · {current:N0}/{total:N0} · 序号 {sequence}";
    }

    private void SessionsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        VisualStateManager.GoToState(this, e.NewSize.Width >= 760 ? "WideSessions" : "CompactSessions", false);
        VisualStateManager.GoToState(this, e.NewSize.Width < 641 ? "CompactPageMargins" : "StandardPageMargins", false);
    }

    private void RefreshSessionsButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void SessionFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void EventFilter_Changed(object sender, object e) => EventFilterChanged?.Invoke(this, EventFilter);

    private void ApplyFilter()
    {
        var selectedId = (SessionList.SelectedItem as SessionRow)?.Id;
        var filter = SessionFilter?.Text.Trim();
        var matches = string.IsNullOrEmpty(filter)
            ? allSessions
            : allSessions.Where(item => item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || item.Status.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || item.Summary.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        suppressSelection = true;
        Sessions.Clear();
        foreach (var session in matches)
        {
            Sessions.Add(session);
        }

        var selected = Sessions.FirstOrDefault(item => item.Id == selectedId) ?? Sessions.FirstOrDefault();
        SessionList.SelectedItem = selected;
        suppressSelection = false;
        SessionsEmptyState.Text = allSessions.Count == 0 ? "尚无会话记录" : "没有匹配的会话";
        SessionsEmptyState.Visibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection(selected);
    }

    private void RevealSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionRow session)
        {
            RevealRequested?.Invoke(this, session.Path);
        }
    }

    private void ExportSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is { } session)
        {
            ExportRequested?.Invoke(this, session.Id);
        }
    }

    private void ReplaySessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is { } session)
        {
            ReplayRequested?.Invoke(this, session.Id);
        }
    }

    private void ReplayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (replayRunning && SelectedSession is { } session)
        {
            ReplayPauseRequested?.Invoke(this, session.Id);
        }
    }

    private void ReplayStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (replayRunning && SelectedSession is { } session)
        {
            ReplayStopRequested?.Invoke(this, session.Id);
        }
    }

    private async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRow { IsActive: false } session)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除会话",
            Content = $"将永久删除 {Path.GetFileName(session.Path)}。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            DeleteRequested?.Invoke(this, session.Id);
        }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection)
        {
            return;
        }

        var session = SessionList.SelectedItem as SessionRow;
        UpdateSelection(session);
        if (session is not null)
        {
            SessionSelected?.Invoke(this, session.Id);
        }
    }

    private void UpdateSelection(SessionRow? session)
    {
        RevealSessionButton.IsEnabled = session is not null;
        ReplaySessionButton.IsEnabled = session is not null && !replayRunning;
        ReplayPauseButton.IsEnabled = false;
        ReplayStopButton.IsEnabled = false;
        ExportSessionButton.IsEnabled = session is not null;
        DeleteSessionButton.IsEnabled = session is { IsActive: false };
        Events.Clear();
        LoopbackResults.Clear();
        LoopbackEmptyState.Visibility = Visibility.Visible;
        EventTitle.Text = session is null ? "选择会话以查看报文" : session.Title;
        EventsEmptyState.Text = session is null ? "选择会话以查看报文" : "正在读取会话…";
        EventsEmptyState.Visibility = Visibility.Visible;
    }
}

public sealed record SessionEventFilter(SerialDirection? Direction, string? SourceContains, string? DataContainsHex);

public sealed class SessionRow
{
    public Guid Id { get; private init; }
    public string Path { get; private init; } = "";
    public string Title { get; private init; } = "";
    public string Status { get; private init; } = "";
    public string Summary { get; private init; } = "";
    public long EventCount { get; private init; }
    public bool IsActive { get; private init; }

    public static SessionRow From(SessionDescriptor session, bool active)
    {
        var status = active ? "记录中" : session.EndedUtc is null ? "未正常结束" : "已完成";
        return new SessionRow
        {
            Id = session.Id,
            Path = session.Path,
            Title = session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            Status = status,
            Summary = $"{session.EventCount:N0} 条 · {FormatBytes(session.RawByteCount)}",
            EventCount = session.EventCount,
            IsActive = active,
        };
    }

    private static string FormatBytes(long value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024d / 1024d:N1} MiB",
        >= 1024 => $"{value / 1024d:N1} KiB",
        _ => $"{value:N0} B",
    };
}

public sealed class SessionEventRow
{
    public string Time { get; private init; } = "";
    public string Direction { get; private init; } = "";
    public string Source { get; private init; } = "";
    public string Display { get; private init; } = "";

    public static SessionEventRow From(SerialTrafficEvent item) =>
        new()
        {
            Time = item.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Direction = item.Direction == SerialDirection.Receive ? "RX" : "TX",
            Source = item.Source,
            Display = Protocols.HexCodec.Format(item.Data),
        };
}

public sealed class LoopbackHistoryRow
{
    public string Time { get; private init; } = "";
    public string Status { get; private init; } = "";
    public string Summary { get; private init; } = "";
    public string Details { get; private init; } = "";

    public static LoopbackHistoryRow From(LoopbackHistoryEntry entry)
    {
        var result = entry.Result;
        var difference = result.FirstDifferenceIndex is { } index
            ? $"差异位置 {index} · 期望 {FormatByte(result.ExpectedByte)} · 实际 {FormatByte(result.ActualByte)}"
            : result.Error ?? "无错误";
        return new LoopbackHistoryRow
        {
            Time = entry.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            Status = result.Passed ? "通过" : "失败",
            Summary = $"{result.Iterations:N0} 次 · {result.Duration.TotalMilliseconds:N0} ms · {result.BytesPerSecond / 1024:N1} KiB/s · {difference}",
            Details = difference,
        };
    }

    private static string FormatByte(byte? value) => value is { } item ? $"0x{item:X2}" : "—";
}
