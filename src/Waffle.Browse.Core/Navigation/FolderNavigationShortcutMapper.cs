namespace Waffle.Browse.Core.Navigation;

public static class FolderNavigationShortcutMapper
{
    public static FolderNavigationAction ToAction(FolderNavigationShortcut shortcut)
    {
        return shortcut switch
        {
            FolderNavigationShortcut.MouseBackButton => FolderNavigationAction.Back,
            FolderNavigationShortcut.MouseForwardButton => FolderNavigationAction.Forward,
            FolderNavigationShortcut.Backspace => FolderNavigationAction.Back,
            FolderNavigationShortcut.AltLeft => FolderNavigationAction.Back,
            FolderNavigationShortcut.AltRight => FolderNavigationAction.Forward,
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut), shortcut, "Unknown navigation shortcut.")
        };
    }
}
