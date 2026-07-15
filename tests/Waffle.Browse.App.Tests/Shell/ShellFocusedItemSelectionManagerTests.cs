using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class ShellFocusedItemSelectionManagerTests
{
    public static void SelectFocusedItemSelectsAndFocusesCurrentItem()
    {
        var manager = new ShellFocusedItemSelectionManager();
        var selectedItem = -1;
        var selectedFlags = ShellViewSelectionFlags.None;

        if (!manager.SelectFocusedItem(
                () => (0, 3),
                (item, flags) =>
                {
                    selectedItem = item;
                    selectedFlags = flags;
                    return 0;
                },
                ShellFocusedItemSelectionMode.Mouse))
        {
            throw new InvalidOperationException("Focused item selection should succeed.");
        }

        if (selectedItem != 3)
        {
            throw new InvalidOperationException("Focused item should be selected.");
        }

        var requiredFlags = ShellViewSelectionFlags.Select
            | ShellViewSelectionFlags.Focused
            | ShellViewSelectionFlags.EnsureVisible
            | ShellViewSelectionFlags.SelectionMark;
        if ((selectedFlags & requiredFlags) != requiredFlags)
        {
            throw new InvalidOperationException("Selection should select, focus, mark, and ensure the item is visible.");
        }
    }

    public static void KeyboardSelectionMarksItemAsKeyboardSelected()
    {
        var manager = new ShellFocusedItemSelectionManager();
        var selectedFlags = ShellViewSelectionFlags.None;

        manager.SelectFocusedItem(
            () => (0, 2),
            (_, flags) =>
            {
                selectedFlags = flags;
                return 0;
            },
            ShellFocusedItemSelectionMode.Keyboard);

        if ((selectedFlags & ShellViewSelectionFlags.KeyboardSelect) != ShellViewSelectionFlags.KeyboardSelect)
        {
            throw new InvalidOperationException("Keyboard selection should use the shell keyboard selection flag.");
        }

        if ((selectedFlags & ShellViewSelectionFlags.DeselectOthers) != ShellViewSelectionFlags.DeselectOthers)
        {
            throw new InvalidOperationException("Plain keyboard selection should replace the previous selection.");
        }
    }

    public static void SelectFocusedItemSkipsWhenNoItemIsFocused()
    {
        var manager = new ShellFocusedItemSelectionManager();
        var selected = false;

        if (manager.SelectFocusedItem(
                () => (0, -1),
                (_, _) =>
                {
                    selected = true;
                    return 0;
                },
                ShellFocusedItemSelectionMode.Mouse))
        {
            throw new InvalidOperationException("Selection should fail when no item is focused.");
        }

        if (selected)
        {
            throw new InvalidOperationException("No item should be selected when no item is focused.");
        }
    }

    public static void MouseSelectionSkipsWhenShellSelectionIsEmpty()
    {
        var manager = new ShellFocusedItemSelectionManager();
        var selected = false;

        if (manager.SelectFocusedItem(
                () => (0, 3),
                (_, _) =>
                {
                    selected = true;
                    return 0;
                },
                ShellFocusedItemSelectionMode.Mouse,
                () => (0, 0)))
        {
            throw new InvalidOperationException("Mouse selection sync should not reselect the focused item after a blank shell click clears the selection.");
        }

        if (selected)
        {
            throw new InvalidOperationException("Blank shell clicks should leave the focused item unselected.");
        }
    }

    public static void SelectFocusedItemSkipsWhenFocusQueryFails()
    {
        var manager = new ShellFocusedItemSelectionManager();
        var selected = false;

        if (manager.SelectFocusedItem(
                () => (unchecked((int)0x80004005), 0),
                (_, _) =>
                {
                    selected = true;
                    return 0;
                },
                ShellFocusedItemSelectionMode.Mouse))
        {
            throw new InvalidOperationException("Selection should fail when focused item query fails.");
        }

        if (selected)
        {
            throw new InvalidOperationException("No item should be selected after a failed focused item query.");
        }
    }
}
