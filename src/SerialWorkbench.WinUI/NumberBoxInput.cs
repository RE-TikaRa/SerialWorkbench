using Microsoft.UI.Xaml.Controls;

namespace SerialWorkbench.WinUI;

public static class NumberBoxInput
{
    public static void KeepLastValue(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = args.OldValue;
        }
    }
}
