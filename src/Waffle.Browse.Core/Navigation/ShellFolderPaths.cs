namespace Waffle.Browse.Core.Navigation;

public static class ShellFolderPaths
{
    public const string ThisPc = "shell:MyComputerFolder";
    public const string ThisPcDisplayName = "내 PC";

    public static bool IsThisPc(string path)
    {
        return string.Equals(path, ThisPc, StringComparison.OrdinalIgnoreCase);
    }
}
