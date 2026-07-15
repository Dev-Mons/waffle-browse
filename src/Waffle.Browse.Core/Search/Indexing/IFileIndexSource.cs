namespace Waffle.Browse.Core.Search.Indexing;

public sealed record FileIndexBuildResult(
    IReadOnlyList<FileIndexEntry> Entries,
    IReadOnlyList<FileIndexCheckpoint> Checkpoints,
    IReadOnlyList<string> Warnings,
    long SkippedPathCount = 0);

public interface IFileIndexSource
{
    Task<FileIndexBuildResult> BuildAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default);
}

public interface IFileIndexSnapshotSource : IFileIndexSource
{
    Task<FileIndexBuildResult> RefreshAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot baseline,
        CancellationToken cancellationToken = default);
}
