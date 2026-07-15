namespace Waffle.Browse.Core.Search.Indexing;

public enum FileIndexLoadKind
{
    Missing,
    Loaded,
    Corrupt
}

public sealed record FileIndexLoadResult(
    FileIndexLoadKind Kind,
    FileIndexSnapshot? Snapshot = null,
    string? ErrorMessage = null);

public interface IFileIndexStore
{
    Task<FileIndexLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(FileIndexSnapshot snapshot, CancellationToken cancellationToken = default);
}
