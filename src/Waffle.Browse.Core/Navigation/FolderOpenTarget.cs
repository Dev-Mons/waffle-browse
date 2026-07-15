namespace Waffle.Browse.Core.Navigation;

public sealed record FolderOpenTarget(string FileName, IReadOnlyList<string> Arguments)
{
    public static FolderOpenTarget ForPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FolderOpenTarget("explorer.exe", [path]);
    }
}
