namespace Waffle.Browse.App.Shell;

public static class ShellFolderViewSettings
{
    public const uint DetailsViewMode = 4;
    public const uint FullRowSelect = 0x00200000;

    public static uint InitialFlags => 0;

    public static uint CurrentFolderFlagMask => FullRowSelect;

    public static uint CurrentFolderFlags => FullRowSelect;
}
