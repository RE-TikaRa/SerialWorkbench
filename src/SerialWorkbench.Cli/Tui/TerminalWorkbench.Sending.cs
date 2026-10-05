using System.Collections.ObjectModel;
using System.Text;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Protocols;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly DropDownList sendFormat = new()
    {
        Id = "send-format",
        Width = 10,
        ReadOnly = true,
        Text = "HEX",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["HEX", "文本"]))
    };
    private readonly DropDownList checksum = new()
    {
        Id = "send-checksum",
        Width = 20,
        ReadOnly = true,
        Text = "无校验",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["无校验", "XOR", "SUM8", "CRC16 Modbus", "CRC16 XMODEM", "CRC32"]))
    };
    private readonly NumericUpDown<int> sendInterval = new() { CanEdit = true, Value = 1000, Width = 10 };
    private readonly NumericUpDown<int> sendCount = new() { CanEdit = true, Value = 0, Width = 10 };
    private readonly View sendSettings = new() { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill(1) };
    private Guid? repeatOperationId;

    internal View SendSettings => sendSettings;

    private FrameView BuildSending()
    {
        var view = new FrameView { Id = "send-panel", Title = "发送", Y = Pos.AnchorEnd(), Height = 3, Width = Dim.Fill() };
        var send = Button("发送", () => RunUiAsync(SendAsync));
        send.X = Pos.AnchorEnd();
        input.X = Pos.Right(sendFormat) + 1;
        input.Width = Dim.Fill(Dim.Width(send) + 1);
        input.Accepting += (_, args) => { args.Handled = true; _ = RunUiAsync(SendAsync); };
        sendFormat.KeyBindings.Remove(Key.F4);
        view.Add(sendFormat, input, send);
        return view;
    }

    private void BuildSendSettings()
    {
        AddSetting(sendSettings, "文本行尾", lineEnding, 0);
        AddSetting(sendSettings, "追加校验", checksum, 2);
        AddSetting(sendSettings, "循环间隔 ms", sendInterval, 4);
        AddSetting(sendSettings, "循环次数", sendCount, 6);
        var repeat = Button("循环发送", () => RunUiAsync(RepeatSendAsync));
        repeat.Y = Pos.Bottom(sendCount) + 1;
        var stop = Button("停止", () => RunUiAsync(async () =>
        {
            if (repeatOperationId is { } id)
            {
                await client.CancelOperationAsync(id, lifetime.Token).ConfigureAwait(false);
                repeatOperationId = null;
                await RefreshTasksAsync().ConfigureAwait(false);
            }
        }));
        stop.X = Pos.Right(repeat) + 1;
        stop.Y = Pos.Top(repeat);
        sendSettings.Add(repeat, stop);
    }

    internal SendRequest CreateSendRequest(Guid? selectedConnection = null)
    {
        var id = selectedConnection ?? RequiredConnection();
        var ending = lineEnding.Text switch { "CR" => "\r", "LF" => "\n", "CRLF" => "\r\n", _ => "" };
        var data = sendFormat.Text == "HEX" ? HexCodec.Parse(input.Text) : Encoding.GetEncoding(encoding.Text).GetBytes(input.Text + ending);
        var kind = checksum.Text switch
        {
            "XOR" => ChecksumKind.Xor,
            "SUM8" => ChecksumKind.Sum8,
            "CRC16 Modbus" => ChecksumKind.Crc16Modbus,
            "CRC16 XMODEM" => ChecksumKind.Crc16Xmodem,
            "CRC32" => ChecksumKind.Crc32,
            _ => ChecksumKind.None,
        };
        return new SendRequest(id, Checksums.Append(data, kind), "tui.send");
    }

    private async Task RepeatSendAsync()
    {
        var request = CreateSendRequest();
        var definition = new RepeatSendRequest(request.Data, sendInterval.Value, sendCount.Value, "tui.repeat");
        var historyText = input.Text;
        await StartTaskAsync(OperationJson.Create("send.repeat", request.ConnectionId, definition), operation =>
        {
            repeatOperationId = operation.Id;
            _ = RunUiAsync(async () =>
            {
                var saved = await client.AddSendHistoryAsync(historyText, lifetime.Token).ConfigureAwait(false);
                await InvokeUiAsync(() => ApplyConfiguration(saved)).ConfigureAwait(false);
            });
        }).ConfigureAwait(false);
    }
}
