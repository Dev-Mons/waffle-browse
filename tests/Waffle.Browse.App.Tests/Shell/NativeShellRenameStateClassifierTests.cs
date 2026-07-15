using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class NativeShellRenameStateClassifierTests
{
    public static void EditBoxInsideShellPanelIsInlineRename()
    {
        if (!NativeShellRenameStateClassifier.IsInlineRenameActive("Edit", focusedWindowBelongsToShellPanel: true))
        {
            throw new InvalidOperationException("A focused Edit window inside a shell panel should be treated as inline rename.");
        }
    }

    public static void EditBoxClassNameIsCaseInsensitive()
    {
        if (!NativeShellRenameStateClassifier.IsInlineRenameActive("edit", focusedWindowBelongsToShellPanel: true))
        {
            throw new InvalidOperationException("The Edit window class match should be case-insensitive.");
        }
    }

    public static void EditBoxOutsideShellPanelIsNotInlineRename()
    {
        if (NativeShellRenameStateClassifier.IsInlineRenameActive("Edit", focusedWindowBelongsToShellPanel: false))
        {
            throw new InvalidOperationException("An Edit window outside any shell panel must not suppress shell input handling.");
        }
    }

    public static void NonEditFocusInsideShellPanelIsNotInlineRename()
    {
        if (NativeShellRenameStateClassifier.IsInlineRenameActive("DirectUIHWND", focusedWindowBelongsToShellPanel: true))
        {
            throw new InvalidOperationException("Ordinary shell view focus must not be mistaken for inline rename.");
        }
    }

    public static void MissingClassNameIsNotInlineRename()
    {
        if (NativeShellRenameStateClassifier.IsInlineRenameActive(null, focusedWindowBelongsToShellPanel: true))
        {
            throw new InvalidOperationException("A missing window class name must not be treated as inline rename.");
        }
    }

    public static void ArrowKeyDownIsRoutedToRenameEdit()
    {
        int[] arrowKeys =
        [
            NativeShellKeyboardInputClassifier.VkLeft,
            NativeShellKeyboardInputClassifier.VkUp,
            NativeShellKeyboardInputClassifier.VkRight,
            NativeShellKeyboardInputClassifier.VkDown
        ];

        foreach (var key in arrowKeys)
        {
            if (!NativeShellRenameStateClassifier.ShouldRouteKeyToRenameEdit(NativeShellKeyboardInputClassifier.WmKeyDown, key))
            {
                throw new InvalidOperationException($"Arrow key 0x{key:X} should be routed to the rename edit box.");
            }
        }
    }

    public static void CharacterKeyDownIsNotRoutedToRenameEdit()
    {
        const int vkA = 0x41;
        if (NativeShellRenameStateClassifier.ShouldRouteKeyToRenameEdit(NativeShellKeyboardInputClassifier.WmKeyDown, vkA))
        {
            throw new InvalidOperationException("Character keys must flow to the rename edit box natively, not be re-sent.");
        }
    }

    public static void NonKeyDownMessageIsNotRoutedToRenameEdit()
    {
        if (NativeShellRenameStateClassifier.ShouldRouteKeyToRenameEdit(NativeShellKeyboardInputClassifier.WmSysKeyDown, NativeShellKeyboardInputClassifier.VkLeft))
        {
            throw new InvalidOperationException("Only WM_KEYDOWN arrow keys should be re-sent to the rename edit box.");
        }
    }
}
