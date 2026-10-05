using System.Collections.ObjectModel;
using System.Text;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Protocols;
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
        X = 25,
        Width = 20,
        ReadOnly = true,
        Text = "无校验",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["无校验", "XOR", "SUM8", "CRC16 Modbus", "CRC16 XMODEM", "CRC32"]))
    };
    private readonly NumericUpDown<int> sendInterval = new() { X = 9, Y = 3, Value = 1000, Width = 10 };
    private readonly NumericUpDown<int> sendCount = new() { X = 32, Y = 3, Value = 0, Width = 10 };
    private Guid? repeatOperationId;

    private FrameView BuildSending()
    {
        var view = new FrameView { Title = "发送 · Enter 发送 · 次数 0 持续发送", Y = Pos.AnchorEnd(7), Height = 6, Width = Dim.Fill() };
        lineEnding.X = 12;
        lineEnding.Y = 0;
        lineEnding.Width = 11;
        var repeat = Button("循环发送", () => RunUiAsync(RepeatSendAsync));
        repeat.X = 44;
        repeat.Y = 3;
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
        stop.Y = 3;
        view.Add(sendFormat, lineEnding, checksum, new Label { Text = "间隔 ms", Y = 3 }, sendInterval,
            new Label { Text = "次数", X = 22, Y = 3 }, sendCount, repeat, stop);
        return view;
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
