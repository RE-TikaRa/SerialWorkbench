using Microsoft.UI.Xaml.Controls;

namespace SerialWorkbench.WinUI.Controls;

public sealed partial class PageHeader : UserControl
{
    public PageHeader() => InitializeComponent();

    public string Title { get => TitleText.Text; set => TitleText.Text = value; }

    public string Description { get => DescriptionText.Text; set => DescriptionText.Text = value; }
}
