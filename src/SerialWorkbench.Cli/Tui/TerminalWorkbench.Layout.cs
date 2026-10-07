using SerialWorkbench.Domain;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly Label trafficStatus = new() { Id = "traffic-status", Width = Dim.Fill(), Height = 1 };
    private readonly Label trafficMode = new() { Id = "traffic-mode", X = Pos.AnchorEnd(), Width = 14, Height = 1, TextAlignment = Alignment.End };
    private readonly Label trafficEmpty = new() { Id = "traffic-empty", X = Pos.Center(), Y = Pos.Center(), Height = 1 };
    private readonly Label connectionsEmpty = new() { Text = "无连接", X = Pos.Center(), Y = Pos.Center(), Height = 1 };
    private readonly Label terminalSize = new() { Id = "terminal-size", Text = "终端至少需要 60 列、20 行", Y = Pos.Center(), Width = Dim.Fill(), Height = 1, TextAlignment = Alignment.Center, Visible = false };
    private View? focusBeforeResize;

    private View BuildWorkbench()
    {
        var view = new View { Title = "工作台", Width = Dim.Fill(), Height = Dim.Fill() };
        var sending = BuildSending();
        trafficStatus.Y = Pos.Top(sending) - 1;
        trafficMode.Y = Pos.Top(trafficStatus);
        trafficStatus.Width = Dim.Fill(Dim.Width(trafficMode) + 1);
        var connectionsFrame = new FrameView
        {
            Title = "连接",
            Width = Dim.Func(_ => window.Viewport.Width >= 80 ? Math.Min(28, view.Viewport.Width / 4) : 0),
            Height = Dim.Fill(Dim.Height(sending) + Dim.Height(trafficStatus)),
        };
        connections.Accepting += (_, args) => { args.Handled = true; ShowConnections(); };
        connectionsFrame.Add(connections, connectionsEmpty);
        var trafficFrame = new FrameView
        {
            Title = "报文",
            X = Pos.Right(connectionsFrame),
            Width = Dim.Fill(),
            Height = Dim.Height(connectionsFrame),
        };
        trafficFrame.Add(traffic, trafficEmpty);
        view.Add(connectionsFrame, trafficFrame, trafficStatus, trafficMode, sending);
        view.SubViewLayout += (_, _) =>
        {
            var narrow = window.Viewport.Width < 80;
            var restoreFocus = narrow && connectionsFrame.HasFocus;
            connectionsFrame.Visible = !narrow;
            if (restoreFocus)
            {
                traffic.SetFocus();
            }
        };
        traffic.Style.ShowVerticalCellLines = false;
        traffic.Style.ShowVerticalHeaderLines = false;
        traffic.Style.ShowHorizontalHeaderOverline = false;
        traffic.Style.ShowHorizontalHeaderUnderline = false;
        traffic.Style.GetOrCreateColumnStyle(0).MaxWidth = 12;
        traffic.Style.GetOrCreateColumnStyle(1).MaxWidth = 4;
        traffic.Style.GetOrCreateColumnStyle(2).MaxWidth = 16;
        traffic.SubViewLayout += (_, _) =>
        {
            traffic.Style.GetOrCreateColumnStyle(2).Visible = traffic.Viewport.Width >= 72;
            traffic.Style.GetOrCreateColumnStyle(0).Visible = timestamps.Value == CheckState.Checked;
        };
        UpdateConnectionStatus();
        return view;
    }

    private void UpdateTerminalSize()
    {
        var tooSmall = window.Viewport.Width < 60 || window.Viewport.Height < 20;
        if (tooSmall == terminalSize.Visible)
        {
            return;
        }
        terminalSize.Visible = tooSmall;
        UpdateActivity();
        if (tooSmall)
        {
            focusBeforeResize = window.MostFocused;
            tabs.Visible = false;
        }
        else
        {
            tabs.Visible = true;
            focusBeforeResize?.SetFocus();
            focusBeforeResize = null;
        }
    }

    private void UpdateConnectionStatus()
    {
        var current = snapshots.FirstOrDefault(item => item.Id == connectionId);
        connectionsEmpty.Visible = snapshots.Count == 0;
        trafficEmpty.Visible = visibleRows.Length == 0;
        trafficEmpty.Text = current is null && replayBuffer is null ? "未连接"
            : paused ? "显示已暂停" : direction.Text != "全部" || filter.Text.Length > 0 || sourceFilter.Text.Length > 0 ? "无匹配报文" : "等待数据";
        var text = "未连接";
        if (current is not null)
        {
            var options = current.Options;
            var parity = options.Parity switch
            {
                SerialParity.None => "N",
                SerialParity.Odd => "O",
                SerialParity.Even => "E",
                SerialParity.Mark => "M",
                SerialParity.Space => "S",
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            };
            var stop = options.StopBits switch
            {
                SerialStopBits.One => "1",
                SerialStopBits.OnePointFive => "1.5",
                SerialStopBits.Two => "2",
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            };
            text = $"{options.PortName} · {options.BaudRate} {options.DataBits}{parity}{stop} · {ConnectionStateText(current.State)}";
        }
        if (status.Text != text)
        {
            status.Text = text;
        }
        var mode = paused ? "暂停" : replayBuffer is not null ? "回放" : follow.Value == CheckState.Checked ? "实时" : "浏览";
        var selected = follow.Value == CheckState.Checked ? 0 : traffic.GetAllSelectedCells().Select(static cell => cell.Y)
            .Distinct().Count(index => index >= 0 && index < visibleRows.Length);
        text = $"RX {current?.ReceivedBytes ?? 0:N0} B  TX {current?.TransmittedBytes ?? 0:N0} B  报文 {visibleRows.Length:N0}";
        if (selected > 0)
        {
            text += $"  已选 {selected} 条";
        }
        if (trafficStatus.Text != text)
        {
            trafficStatus.Text = text;
        }
        trafficMode.Text = $"{mode} · {format.Text}";
        trafficMode.SetScheme(paused || replayBuffer is not null ? warningStyle : accentStyle);
    }
}
