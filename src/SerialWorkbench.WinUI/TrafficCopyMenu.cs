using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SerialWorkbench.Application;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SerialWorkbench.WinUI;

public sealed class TrafficCopyMenu
{
    private readonly ListView list;
    private readonly MenuFlyoutItem copyItem = new() { Text = "复制所选", Icon = new SymbolIcon(Symbol.Copy) };
    private readonly MenuFlyoutSubItem formatItem = new() { Text = "复制为" };
    private readonly MenuFlyoutItem selectAllItem = new() { Text = "全选" };
    private readonly MenuFlyoutItem clearSelectionItem = new() { Text = "清除选择" };
    private readonly KeyboardAccelerator copyShortcut;

    public TrafficCopyMenu(ListView list)
    {
        this.list = list;
        var menu = new MenuFlyout();
        menu.Items.Add(copyItem);
        menu.Items.Add(formatItem);
        (string Text, TrafficCopyFormat Format)[] formats =
        [
            ("文本（保留行尾）", TrafficCopyFormat.Text),
            ("HEX（空格分隔）", TrafficCopyFormat.Hex),
            ("连续 HEX", TrafficCopyFormat.CompactHex),
            ("带时间、方向和来源", TrafficCopyFormat.Log),
        ];
        foreach (var (text, format) in formats)
        {
            var item = new MenuFlyoutItem { Text = text, Tag = format };
            item.Click += CopyFormatItem_Click;
            formatItem.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(selectAllItem);
        menu.Items.Add(clearSelectionItem);
        menu.Opening += Menu_Opening;
        list.ContextFlyout = menu;
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(List_PointerPressed), true);
        list.SelectionChanged += List_SelectionChanged;
        copyItem.Click += CopyItem_Click;
        selectAllItem.Click += SelectAllItem_Click;
        clearSelectionItem.Click += ClearSelectionItem_Click;
        copyShortcut = new KeyboardAccelerator { Key = VirtualKey.C, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = list };
        copyShortcut.Invoked += CopyShortcut_Invoked;
        list.KeyboardAccelerators.Add(copyShortcut);
        UpdateSelectionState();
    }

    public bool HasSelection => list.SelectedItems.Count != 0;

    public HashSet<TrafficRowIdentity> CaptureSelection() =>
        list.SelectedItems.OfType<TrafficRow>().Select(static row => row.Identity).ToHashSet();

    public void RestoreSelection(IReadOnlySet<TrafficRowIdentity> selected)
    {
        if (selected.Count == 0)
        {
            return;
        }

        var existing = list.SelectedItems.OfType<TrafficRow>().ToHashSet();
        foreach (var row in TrafficSelection.GetSelectedRows(list.Items.OfType<TrafficRow>(), selected))
        {
            if (existing.Add(row))
            {
                list.SelectedItems.Add(row);
            }
        }
    }

    private void List_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(list).Properties.IsRightButtonPressed
            && e.OriginalSource is FrameworkElement { DataContext: TrafficRow row }
            && !list.SelectedItems.Contains(row))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(row);
            list.Focus(FocusState.Pointer);
        }
    }

    private void Menu_Opening(object? sender, object e)
    {
        selectAllItem.IsEnabled = list.Items.Count != 0;
        UpdateSelectionState();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void UpdateSelectionState()
    {
        var count = list.SelectedItems.Count;
        copyItem.Text = count == 0 ? "复制所选" : $"复制所选（{count} 条）";
        copyItem.IsEnabled = count != 0;
        formatItem.IsEnabled = count != 0;
        clearSelectionItem.IsEnabled = count != 0;
        copyShortcut.IsEnabled = count != 0;
    }

    private void CopyItem_Click(object sender, RoutedEventArgs e) => CopySelection(TrafficCopyFormat.CurrentDisplay);

    private void CopyFormatItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: TrafficCopyFormat format })
        {
            CopySelection(format);
        }
    }

    private void CopyShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CopySelection(TrafficCopyFormat.CurrentDisplay);
        args.Handled = true;
    }

    private void CopySelection(TrafficCopyFormat format)
    {
        var rows = TrafficSelection.GetSelectedRows(list.Items.OfType<TrafficRow>(), CaptureSelection());
        if (rows.Count == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(TrafficCopyFormatter.Format(rows, format));
        Clipboard.SetContent(package);
    }

    private void SelectAllItem_Click(object sender, RoutedEventArgs e) => list.SelectAll();

    private void ClearSelectionItem_Click(object sender, RoutedEventArgs e) => list.SelectedItems.Clear();
}
