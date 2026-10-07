using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly StatusBar shortcuts = new();
    private Shortcut[] shortcutCommands = [];

    private void BuildShortcuts()
    {
        shortcutCommands = [
            new Shortcut(Key.F1, "帮助", ShowHelp),
            new Shortcut(Key.F2, "暂停", TogglePause),
            new Shortcut(Key.F3, "清空", ClearTraffic),
            new Shortcut(Key.F4, "连接", ShowConnections),
            new Shortcut(Key.F5, "刷新", () => _ = RunUiAsync(RefreshPortsAsync)),
            new Shortcut(Key.F6, "复制", CopySelected),
            new Shortcut(Key.F7, "实时", ResumeLiveTraffic),
            new Shortcut(Key.F8, "设置", () => tabs.Value = settingsView),
            new Shortcut(Key.F9, "工具", ShowTools),
            new Shortcut(Key.Q.WithCtrl, "退出", () => app.RequestStop(window)),
        ];
        window.KeyDownNotHandled += (_, key) =>
        {
            if (shortcutCommands.FirstOrDefault(item => item.Key == key) is { } command)
            {
                key.Handled = true;
                command.Action?.Invoke();
            }
        };
        tabs.ValueChanged += (_, _) =>
        {
            UpdateWorkspaceTitle();
            UpdateShortcutHints();
        };
        input.HasFocusChanged += (_, _) => UpdateShortcutHints();
        traffic.HasFocusChanged += (_, _) => UpdateShortcutHints();
        connections.HasFocusChanged += (_, _) => UpdateShortcutHints();
    }

    private void UpdateWorkspaceTitle()
    {
        var text = window.Viewport.Width < 80 ? "SerialWorkbench" : $"SerialWorkbench / {tabs.Value?.Title[2..]}";
        if (title.Text != text)
        {
            title.Text = text;
        }
    }

    private void UpdateShortcutHints()
    {
        Key[] visible = tabs.Value == workbenchView && traffic.HasFocus
            ? [Key.F1, Key.F2, Key.F6, Key.F7, Key.Q.WithCtrl]
            : tabs.Value == workbenchView && input.HasFocus
                ? [Key.F1, Key.F4, Key.F8, Key.F9, Key.Q.WithCtrl]
                : tabs.Value == workbenchView
                    ? [Key.F1, Key.F4, Key.F5, Key.F8, Key.Q.WithCtrl]
                    : [Key.F1, Key.F4, Key.F9, Key.Q.WithCtrl];
        var selected = shortcutCommands.Where(command => visible.Contains(command.Key)).ToArray();
        if (shortcuts.SubViews.SequenceEqual(selected))
        {
            return;
        }
        foreach (var command in shortcuts.SubViews.ToArray())
        {
            shortcuts.Remove(command);
        }
        shortcuts.Add(selected);
    }

    private View BuildHistory()
    {
        history.SetSource(historyItems);
        history.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (history.Value is { } index && index >= 0 && index < historyItems.Count)
            {
                input.Text = historyItems[index];
                tabs.Value = workbenchView;
                input.SetFocus();
            }
        };
        var view = new View { Title = "发送历史", Width = Dim.Fill(), Height = Dim.Fill() };
        view.Add(history);
        return view;
    }

    internal View GetTool(string title) => tools.Single(tool => tool.Title == title);

    private void ShowTools()
    {
        using var dialog = new Dialog { Title = "工具", Width = 42, Height = 12 };
        var groups = new Tabs { Width = Dim.Fill(), Height = Dim.Fill(1), TabLineStyle = Terminal.Gui.Drawing.LineStyle.Single };
        string? selected = null;
        foreach (var group in new[] { ("设备操作", tools[..3]), ("数据分析", tools[3..]) })
        {
            var page = new View { Title = group.Item1 };
            var list = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
            var names = new ObservableCollection<string>(group.Item2.Select(static tool => tool.Title));
            list.SetSource(names);
            list.Accepting += (_, args) =>
            {
                args.Handled = true;
                if (list.Value is { } index && index >= 0 && index < names.Count)
                {
                    selected = names[index];
                    app.RequestStop(dialog);
                }
            };
            page.Add(list);
            groups.Add(page);
        }
        groups.Value = groups.TabCollection.First();
        dialog.Add(groups);
        dialog.AddButton(new Button { Text = "关闭", ShadowStyle = null });
        RunDialog(dialog);
        if (selected is not null)
        {
            ShowTool(selected);
        }
    }

    internal void ShowTool(string title)
    {
        var tool = GetTool(title);
        using var dialog = new Dialog { Title = $"工具 / {title}", Width = Dim.Fill(), Height = Dim.Fill() };
        tool.Height = Dim.Fill(1);
        dialog.Add(tool);
        dialog.AddButton(new Button { Text = "关闭", ShadowStyle = null });
        try
        {
            RunDialog(dialog);
        }
        finally
        {
            dialog.Remove(tool);
        }
    }
}
