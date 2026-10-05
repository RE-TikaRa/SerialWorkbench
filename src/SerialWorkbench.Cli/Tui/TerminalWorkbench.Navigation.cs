using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    internal View GetTool(string title) => tools.Single(tool => tool.Title == title);

    private void ShowTools()
    {
        using var dialog = new Dialog { Title = "工具", Width = 40, Height = 11 };
        var list = new ListView { Width = Dim.Fill(), Height = Dim.Fill(1) };
        list.SetSource(new ObservableCollection<string>(tools.Select(static tool => tool.Title)));
        string? selected = null;
        list.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (list.Value is { } index && index >= 0 && index < tools.Length)
            {
                selected = tools[index].Title;
                app.RequestStop(dialog);
            }
        };
        dialog.Add(list);
        dialog.AddButton(new Button { Text = "关闭", ShadowStyle = null });
        app.Run(dialog);
        if (selected is not null)
        {
            ShowTool(selected);
        }
    }

    internal void ShowTool(string title)
    {
        var tool = GetTool(title);
        using var dialog = new Dialog { Title = title, Width = Dim.Fill(), Height = Dim.Fill() };
        tool.Height = Dim.Fill(1);
        dialog.Add(tool);
        dialog.AddButton(new Button { Text = "关闭", ShadowStyle = null });
        try
        {
            app.Run(dialog);
        }
        finally
        {
            dialog.Remove(tool);
        }
    }
}
