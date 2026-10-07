using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly SpinnerView activitySpinner = new() { Id = "activity-spinner", Y = Pos.AnchorEnd(2), Style = new SpinnerStyle.Line(), Visible = false };
    private readonly Label activityText = new() { X = 2, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, Visible = false };
    private readonly SpinnerView connectionSpinner = new() { Id = "connection-spinner", Y = Pos.AnchorEnd(), Style = new SpinnerStyle.Line(), Visible = false };
    private readonly CheckBox animations = new() { Id = "tui-animations", Text = "轻量动效", Value = CheckState.Checked };
    private readonly Dictionary<object, string> activities = [];
    private object? activityTimeout;
    private object? messageTimeout;
    private object? progressTimeout;
    private bool activityVisible;

    private void BeginActivity(object activity, string description)
    {
        activities.Add(activity, description);
        if (activityTimeout is null && !activityVisible)
        {
            activityTimeout = app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
            {
                activityTimeout = null;
                activityVisible = true;
                UpdateActivity();
                return false;
            });
        }
        UpdateActivity();
    }

    private void EndActivity(object activity)
    {
        activities.Remove(activity);
        if (activities.Count == 0)
        {
            if (activityTimeout is not null)
            {
                app.RemoveTimeout(activityTimeout);
                activityTimeout = null;
            }
            activityVisible = false;
        }
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var visible = activityVisible && activities.Count > 0;
        var text = visible ? activities.Last().Value : "";
        var animate = visible && animations.Value == CheckState.Checked;
        activitySpinner.Visible = animate && !terminalSize.Visible;
        activitySpinner.AutoSpin = activitySpinner.Visible;
        activityText.Visible = visible && !terminalSize.Visible;
        activityText.Text = text;
        message.Visible = !activityText.Visible && !terminalSize.Visible;
        connectionSpinner.Visible = animate && connectionDialog is not null;
        connectionSpinner.AutoSpin = connectionSpinner.Visible;
        connectionMessage.X = connectionSpinner.Visible ? 2 : 0;
        if (connectionDialog is not null)
        {
            connectionMessage.Text = visible ? text : message.Text;
        }
    }

    internal void ShowMessage(string text, Scheme? style = null, bool transient = false)
    {
        if (messageTimeout is not null)
        {
            app.RemoveTimeout(messageTimeout);
            messageTimeout = null;
        }
        var scheme = transient && animations.Value != CheckState.Checked ? mutedStyle : style ?? mutedStyle;
        message.SetScheme(scheme);
        connectionMessage.SetScheme(scheme);
        message.Text = text;
        if (transient && animations.Value == CheckState.Checked)
        {
            messageTimeout = app.AddTimeout(TimeSpan.FromSeconds(2), () =>
            {
                messageTimeout = null;
                message.SetScheme(mutedStyle);
                connectionMessage.SetScheme(mutedStyle);
                return false;
            });
        }
    }

    private void SetProgressAnimation(bool running)
    {
        if (!running || animations.Value != CheckState.Checked)
        {
            if (progressTimeout is not null)
            {
                app.RemoveTimeout(progressTimeout);
                progressTimeout = null;
            }
            return;
        }
        progressTimeout ??= app.AddTimeout(TimeSpan.FromMilliseconds(130), () =>
        {
            if (tabs.Value?.Title == "3 任务" && !terminalSize.Visible)
            {
                progress.Pulse();
            }
            return true;
        });
    }

    private void StopFeedback()
    {
        activitySpinner.AutoSpin = false;
        connectionSpinner.AutoSpin = false;
        foreach (var timeout in new[] { activityTimeout, messageTimeout, progressTimeout }.OfType<object>())
        {
            app.RemoveTimeout(timeout);
        }
    }
}
