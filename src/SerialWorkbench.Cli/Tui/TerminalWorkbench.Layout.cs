using SerialWorkbench.Domain;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly Label trafficStatus = new() { Id = "traffic-status", Width = Dim.Fill(), Height = 1 };

    private View BuildWorkbench()
    {
        var view = new View { Title = "工作台", Width = Dim.Fill(), Height = Dim.Fill() };
        var sending = BuildSending();
        trafficStatus.Y = Pos.Top(sending) - 1;
        var connectionsFrame = new FrameView
        {
            Title = "连接",
            Width = Dim.Percent(25),
            Height = Dim.Fill(Dim.Height(sending) + Dim.Height(trafficStatus)),
        };
        connections.Accepting += (_, args) => { args.Handled = true; ShowConnections(); };
        connectionsFrame.Add(connections);
        var trafficFrame = new FrameView
        {
            Title = "报文",
            X = Pos.Right(connectionsFrame),
            Width = Dim.Fill(),
            Height = Dim.Height(connectionsFrame),
        };
        trafficFrame.Add(traffic);
        view.Add(connectionsFrame, trafficFrame, trafficStatus, sending);
        UpdateConnectionStatus();
        return view;
    }

    private void UpdateConnectionStatus()
    {
        var current = snapshots.FirstOrDefault(item => item.Id == connectionId);
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
            text = $"{options.PortName} {ConnectionStateText(current.State)}  {options.BaudRate} {options.DataBits}{parity}{stop}";
        }
        if (status.Text != text)
        {
            status.Text = text;
        }
        var mode = paused ? "暂停" : replayBuffer is not null ? "回放" : follow.Value == CheckState.Checked ? "实时" : "浏览";
        text = $"RX {current?.ReceivedBytes ?? 0:N0} B   TX {current?.TransmittedBytes ?? 0:N0} B   报文 {visibleRows.Length:N0}   {mode}";
        if (trafficStatus.Text != text)
        {
            trafficStatus.Text = text;
        }
    }
}
