using System.Collections.ObjectModel;
using System.Text.Json;
using SerialWorkbench.Application;
using SerialWorkbench.Ipc;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private void RegisterTrafficMenu()
    {
        trafficMenu = new PopoverMenu(Enum.GetValues<TrafficCopyFormat>().Select(value => (View)new MenuItem
        {
            Title = value switch
            {
                TrafficCopyFormat.CurrentDisplay => "复制显示内容",
                TrafficCopyFormat.Text => "复制文本",
                TrafficCopyFormat.Hex => "复制 HEX",
                TrafficCopyFormat.CompactHex => "复制连续 HEX",
                TrafficCopyFormat.Log => "复制日志",
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            },
            Action = () => { copyFormat.Value = value; CopySelected(); },
        }).Append(new MenuItem { Title = "查看详情", Action = ShowTrafficDetails }).ToArray());
        app.Popovers?.Register(trafficMenu);
    }

    private void ShowTrafficDetails()
    {
        var selected = traffic.GetAllSelectedCells().Select(static cell => cell.Y).Distinct()
            .Where(index => index >= 0 && index < visibleRows.Length).Order().Select(index => visibleRows[index]).ToArray();
        ShowText("报文详情", TrafficCopyFormatter.Format(selected, TrafficCopyFormat.Log) + Environment.NewLine
            + TrafficCopyFormatter.Format(selected, TrafficCopyFormat.Hex));
    }

    private async Task ShowConnectionAsync()
    {
        var id = RequiredConnection();
        var host = await client.GetStatusAsync(lifetime.Token).ConfigureAwait(false);
        var connection = host.Connections.Single(item => item.Id == id);
        await InvokeUiAsync(() => ShowText("连接详情", JsonSerializer.Serialize(connection, MachineOutput.DocumentOptions))).ConfigureAwait(false);
    }

    private void ShowText(string title, string text)
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(90), Height = Dim.Percent(85) };
        using var lines = new ListView { Width = Dim.Fill(), Height = Dim.Fill(1) };
        lines.SetSource(new ObservableCollection<string>(text.Split('\n')));
        var copy = new Button { Text = "复制全文" };
        copy.Accepting += (_, args) =>
        {
            args.Handled = true;
            app.Clipboard?.TrySetClipboardData(text);
        };
        dialog.Add(lines);
        dialog.AddButton(copy);
        dialog.AddButton(new Button { Text = "关闭" });
        app.Run(dialog);
    }
}
