namespace Waffle.Browse.App.Shell;

public enum ShellFocusedItemSelectionMode
{
    Mouse,
    Keyboard
}

[Flags]
public enum ShellViewSelectionFlags : uint
{
    None = 0,
    Select = 0x1,
    DeselectOthers = 0x4,
    EnsureVisible = 0x8,
    Focused = 0x10,
    SelectionMark = 0x40,
    KeyboardSelect = 0x401
}

public sealed class ShellFocusedItemSelectionManager
{
    public bool SelectFocusedItem(
        Func<(int Result, int Item)> getFocusedItem,
        Func<int, ShellViewSelectionFlags, int> selectItem,
        ShellFocusedItemSelectionMode mode,
        Func<(int Result, int Count)>? getSelectedItemCount = null)
    {
        if (mode == ShellFocusedItemSelectionMode.Mouse && getSelectedItemCount is not null)
        {
            var (selectionCountResult, selectionCount) = getSelectedItemCount();
            if (selectionCountResult >= 0 && selectionCount <= 0)
            {
                return false;
            }
        }

        var (result, item) = getFocusedItem();
        if (result < 0 || item < 0)
        {
            return false;
        }

        var flags = ShellViewSelectionFlags.Select
            | ShellViewSelectionFlags.Focused
            | ShellViewSelectionFlags.EnsureVisible
            | ShellViewSelectionFlags.SelectionMark;

        if (mode == ShellFocusedItemSelectionMode.Keyboard)
        {
            flags |= ShellViewSelectionFlags.KeyboardSelect | ShellViewSelectionFlags.DeselectOthers;
        }

        return selectItem(item, flags) >= 0;
    }
}
