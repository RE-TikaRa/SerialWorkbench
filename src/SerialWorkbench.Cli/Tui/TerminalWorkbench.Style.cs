using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using DrawingAttribute = Terminal.Gui.Drawing.Attribute;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly Scheme bodyStyle;
    private readonly Scheme mutedStyle;
    private readonly Scheme accentStyle;
    private readonly Scheme receiveStyle;
    private readonly Scheme transmitStyle;
    private readonly Scheme successStyle;
    private readonly Scheme warningStyle;
    private readonly Scheme errorStyle;
    private readonly HashSet<View> styledPanels = [];

    private static Scheme CreateBodyStyle(bool light, bool monochrome)
    {
        if (monochrome)
        {
            var normal = new DrawingAttribute(Color.None, Color.None);
            var selected = normal with { Style = TextStyle.Reverse };
            return new Scheme(normal)
            {
                Focus = selected with { Style = TextStyle.Reverse | TextStyle.Bold },
                Active = selected,
                Editable = normal,
                ReadOnly = normal,
                Disabled = normal with { Style = TextStyle.Faint },
            };
        }

        return new Scheme
        {
            Normal = new DrawingAttribute(light ? "#283044" : "#d5dbea", light ? "#f5f6fa" : "#171b2b"),
            Focus = new DrawingAttribute(light ? "#25183e" : "#f4efff", light ? "#ded5f1" : "#4b3b67", TextStyle.Bold),
            Active = new DrawingAttribute(light ? "#35304a" : "#ddd6ea", light ? "#e6e3ed" : "#303043"),
            Editable = new DrawingAttribute(light ? "#283044" : "#e1e5ef", light ? "#e9ebf3" : "#22283d"),
            ReadOnly = new DrawingAttribute(light ? "#283044" : "#d5dbea", light ? "#e9ebf3" : "#22283d"),
            Disabled = new DrawingAttribute(light ? "#7b8190" : "#737c93", light ? "#f5f6fa" : "#171b2b"),
        };
    }

    private Scheme CreateTextStyle(string darkColor, string lightColor, bool monochrome, bool light, TextStyle style = TextStyle.None) =>
        bodyStyle with
        {
            Normal = new DrawingAttribute(monochrome ? Color.None : new Color(light ? lightColor : darkColor), bodyStyle.Normal.Background, style),
        };

    private void ApplyStyles(View view)
    {
        view.SetScheme(view is Label ? mutedStyle : view is Terminal.Gui.Views.Button or ProgressBar ? accentStyle : bodyStyle);
        if (view is TableView table)
        {
            table.Style.HeaderScheme = mutedStyle with { Focus = mutedStyle.Normal with { Style = TextStyle.Bold } };
            table.Style.ShowVerticalCellLines = false;
            table.Style.ShowVerticalHeaderLines = false;
            table.Style.ShowHorizontalHeaderOverline = false;
            table.Style.ShowHorizontalHeaderUnderline = false;
        }

        if (view is Dialog or FileDialog)
        {
            view.Border.View?.SetScheme(accentStyle with { Focus = accentStyle.Normal });
        }
        else if (view is FrameView || view.SuperView is Tabs)
        {
            UpdatePanelStyle(view);
            if (styledPanels.Add(view))
            {
                view.HasFocusChanged += (_, _) => UpdatePanelStyle(view);
            }
        }

        foreach (var child in view.SubViews)
        {
            ApplyStyles(child);
        }
    }

    private void UpdatePanelStyle(View view) =>
        view.Border.View?.SetScheme((view.HasFocus ? accentStyle : mutedStyle) with { Focus = accentStyle.Normal });

    private void RunDialog(Runnable dialog)
    {
        var previous = window.MostFocused;
        ApplyStyles(dialog);
        app.Run(dialog);
        previous?.SetFocus();
    }

    private void StyleTraffic(TableView table)
    {
        table.Style.GetOrCreateColumnStyle(0).ColorGetter = _ => mutedStyle;
        table.Style.GetOrCreateColumnStyle(1).ColorGetter = cell => cell.Representation == "RX" ? receiveStyle : transmitStyle;
        table.Style.GetOrCreateColumnStyle(2).ColorGetter = _ => mutedStyle;
    }
}
