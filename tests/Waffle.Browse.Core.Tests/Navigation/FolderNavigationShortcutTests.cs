using Waffle.Browse.Core.Navigation;

namespace Waffle.Browse.Core.Tests.Navigation;

internal static class FolderNavigationShortcutTests
{
    public static void MouseButtonsMapToHistoryActions()
    {
        TestAssert.Equal(
            FolderNavigationAction.Back,
            FolderNavigationShortcutMapper.ToAction(FolderNavigationShortcut.MouseBackButton),
            "Mouse back button should run folder back navigation");
        TestAssert.Equal(
            FolderNavigationAction.Forward,
            FolderNavigationShortcutMapper.ToAction(FolderNavigationShortcut.MouseForwardButton),
            "Mouse forward button should run folder forward navigation");
    }

    public static void KeyboardShortcutsMapToHistoryActions()
    {
        TestAssert.Equal(
            FolderNavigationAction.Back,
            FolderNavigationShortcutMapper.ToAction(FolderNavigationShortcut.Backspace),
            "Backspace should run folder back navigation");
        TestAssert.Equal(
            FolderNavigationAction.Back,
            FolderNavigationShortcutMapper.ToAction(FolderNavigationShortcut.AltLeft),
            "Alt+Left should run folder back navigation");
        TestAssert.Equal(
            FolderNavigationAction.Forward,
            FolderNavigationShortcutMapper.ToAction(FolderNavigationShortcut.AltRight),
            "Alt+Right should run folder forward navigation");
    }
}
