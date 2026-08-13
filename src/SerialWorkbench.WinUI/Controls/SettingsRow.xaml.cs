using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialWorkbench.WinUI.Controls;

public sealed partial class SettingsRow : UserControl
{
    public SettingsRow() => InitializeComponent();

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Description
    {
        get => DescriptionText.Text;
        set => DescriptionText.Text = value;
    }

    public UIElement? Action
    {
        get => ActionContent.Content as UIElement;
        set => ActionContent.Content = value;
    }

    public string Glyph
    {
        get => Icon.Glyph;
        set => Icon.Glyph = value;
    }
}
