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
            Action = () =>
            {
                copyFormat.Text = value switch
                {
                    TrafficCopyFormat.Text => "文本",
                    TrafficCopyFormat.Hex => "HEX",
                    TrafficCopyFormat.CompactHex => "连续 HEX",
                    TrafficCopyFormat.Log => "日志",
                    _ => "当前显示"
                };
                CopySelected();
            },
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
        var lines = new ListView { Width = Dim.Fill(), Height = Dim.Fill(1) };
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

    private void ShowHelp() => ShowText("快捷键与操作", """
        Tab / Shift+Tab 切换控件焦点
        F1 帮助    F2 暂停报文    F3 清空报文    F4 展开下拉选项
        F5 刷新端口    F6 复制    F7 返回实时    F8 设置    F9 工具    Ctrl+Q 退出
        Alt+1 工作台    Alt+2 历史    Alt+3 任务    Alt+4 会话    Alt+5 设置
        工具：Modbus、文件与回环、自动化、协议分析、波形；Esc 返回
        报文：Shift+方向键选择范围，Ctrl+单击选择多条
        Ctrl+C 复制所选内容，Ctrl+Space 或右键打开复制菜单
        Enter 查看完整报文或会话事件
        发送框：Enter 发送，格式与报文显示独立
        循环发送：次数 0 持续发送，停止按钮结束任务
        后台任务：勾选后退出保留任务；前台任务退出时取消
        Modbus 和回环参数支持滚轮、PageUp / PageDown，Tab 自动显示焦点
        步骤参数在独立对话框中编辑，波形方向键浏览历史
        回放结束后 F7 返回实时，Host 持续记录原始数据
        """);
}
