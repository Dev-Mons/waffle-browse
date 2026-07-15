using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class ShellFolderViewSettingsTests
{
    public static void DefaultFolderViewUsesDetailsMode()
    {
        if (ShellFolderViewSettings.DetailsViewMode != 4)
        {
            throw new InvalidOperationException("Shell view should use details mode.");
        }
    }

    public static void InitialFolderSettingsDoNotIncludeFullRowSelection()
    {
        if ((ShellFolderViewSettings.InitialFlags & ShellFolderViewSettings.FullRowSelect) != 0)
        {
            throw new InvalidOperationException("Initial ExplorerBrowser folder settings should not include full row selection.");
        }
    }

    public static void CurrentFolderFlagsEnableFullRowSelection()
    {
        if ((ShellFolderViewSettings.CurrentFolderFlagMask & ShellFolderViewSettings.FullRowSelect) != ShellFolderViewSettings.FullRowSelect
            || (ShellFolderViewSettings.CurrentFolderFlags & ShellFolderViewSettings.FullRowSelect) != ShellFolderViewSettings.FullRowSelect)
        {
            throw new InvalidOperationException("Shell view should show the selected item across the whole details row.");
        }
    }

}
