namespace Waffle.Browse.Core.Search.Indexing;

public enum FileIndexChangeKind
{
    Upsert,
    Delete,
    Rename
}

public sealed record FileIndexChange(
    FileIndexChangeKind Kind,
    string Path,
    FileIndexEntry? Entry = null,
    string? NewPath = null,
    bool PreserveIdentity = false)
{
    public static FileIndexChange Upsert(FileIndexEntry entry) =>
        new(FileIndexChangeKind.Upsert, entry.FullPath, entry);

    public static FileIndexChange UpdateMetadata(FileIndexEntry entry) =>
        new(FileIndexChangeKind.Upsert, entry.FullPath, entry, PreserveIdentity: true);

    public static FileIndexChange Delete(string path) =>
        new(FileIndexChangeKind.Delete, path);

    public static FileIndexChange Rename(string oldPath, string newPath) =>
        new(FileIndexChangeKind.Rename, oldPath, NewPath: newPath);
}
