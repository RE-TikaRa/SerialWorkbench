using System.Runtime.CompilerServices;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using DrawingAttribute = Terminal.Gui.Drawing.Attribute;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly bool monochrome = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    private Scheme bodyStyle = new();
    private Scheme mutedStyle = new();
    private Scheme accentStyle = new();
    private Scheme receiveStyle = new();
    private Scheme transmitStyle = new();
    private Scheme successStyle = new();
    private Scheme warningStyle = new();
    private Scheme errorStyle = new();
    private readonly ConditionalWeakTable<View, object> styledPanels = new();

    private void SetTheme(bool light)
    {
        bodyStyle = CreateBodyStyle(light, monochrome);
        mutedStyle = CreateTextStyle("#949eb5", "#626b80", monochrome, light);
        accentStyle = CreateTextStyle("#bcabeb", "#69469b", monochrome, light, TextStyle.Bold);
        receiveStyle = CreateTextStyle("#71d5cf", "#08776f", monochrome, light);
        transmitStyle = CreateTextStyle("#e5bd78", "#95601e", monochrome, light);
        successStyle = CreateTextStyle("#91c9a4", "#28643c", monochrome, light, TextStyle.Bold);
        warningStyle = CreateTextStyle("#e5bd78", "#95601e", monochrome, light, TextStyle.Bold);
        errorStyle = CreateTextStyle("#ec929c", "#a22e43", monochrome, light, TextStyle.Bold);
    }

    private void ApplyWorkbenchStyles()
    {
        ApplyStyles(window);
        foreach (var view in tools.Concat([serialSettings, controlSettings, profileSettings, connectionSettings, trafficSettings, sendSettings, sequenceEditor]))
        {
            ApplyStyles(view);
        }
        title.SetScheme(accentStyle);
        status.SetScheme(bodyStyle);
        StyleTraffic(traffic);
        StyleTraffic(sessionEvents);
        shortcuts.SetScheme(bodyStyle);
        activitySpinner.SetScheme(accentStyle);
        activityText.SetScheme(accentStyle);
        UpdateConnectionStatus();
        if ((displayedTasks.FirstOrDefault(item => item.Id == SelectedTaskId()) ?? displayedTasks.FirstOrDefault()) is { } operation)
        {
            ShowProgress(operation);
        }
    }

    private void OnTerminalColorsChanged(object? sender, ValueChangedEventArgs<DrawingAttribute?> args)
    {
        var previous = message.GetScheme();
        Func<Scheme> feedbackStyle = previous == errorStyle ? () => errorStyle
            : previous == warningStyle ? () => warningStyle
            : previous == successStyle ? () => successStyle
            : previous == accentStyle ? () => accentStyle : () => mutedStyle;
        SetTheme(args.NewValue?.Background.IsDarkColor() == false);
        ApplyWorkbenchStyles();
        if (app.TopRunnableView is Runnable current && current != window)
        {
            ApplyStyles(current);
        }
        message.SetScheme(feedbackStyle());
        connectionMessage.SetScheme(feedbackStyle());
    }

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
            styledPanels.GetValue(view, panel =>
            {
                panel.HasFocusChanged += (_, _) => UpdatePanelStyle(panel);
                return new object();
            });
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
