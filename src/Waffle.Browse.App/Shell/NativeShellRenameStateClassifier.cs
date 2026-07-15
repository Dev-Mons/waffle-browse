namespace Waffle.Browse.App.Shell;

public static class NativeShellRenameStateClassifier
{
    public const string EditWindowClassName = "Edit";

    public static bool IsInlineRenameActive(string? focusedWindowClassName, bool focusedWindowBelongsToShellPanel)
    {
        return focusedWindowBelongsToShellPanel
            && string.Equals(focusedWindowClassName, EditWindowClassName, StringComparison.OrdinalIgnoreCase);
    }

    // Arrow keys must be delivered to the rename edit box and marked handled so WPF does
    // not treat them as directional focus navigation and pull focus out of the shell host,
    // which would cancel the rename. Every other key (characters, Delete, Backspace, Enter,
    // Escape, Tab) flows to the edit box natively and is left untouched here.
    public static bool ShouldRouteKeyToRenameEdit(int message, int virtualKey)
    {
        return message == NativeShellKeyboardInputClassifier.WmKeyDown
            && virtualKey is NativeShellKeyboardInputClassifier.VkLeft
                or NativeShellKeyboardInputClassifier.VkUp
                or NativeShellKeyboardInputClassifier.VkRight
                or NativeShellKeyboardInputClassifier.VkDown;
    }
}
