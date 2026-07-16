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

public sealed class FileIndexProgressEventArgs(
    int completedRootCount,
    int totalRootCount,
    string? currentRoot,
    long discoveredItemCount,
    long skippedPathCount) : EventArgs
{
    public int CompletedRootCount { get; } = completedRootCount;

    public int TotalRootCount { get; } = totalRootCount;

    public string? CurrentRoot { get; } = currentRoot;

    public long DiscoveredItemCount { get; } = discoveredItemCount;

    public long SkippedPathCount { get; } = skippedPathCount;

    public static FileIndexProgressEventArgs Initial { get; } = new(0, 0, null, 0, 0);
}

public interface IFileIndexProgressSource
{
    event EventHandler<FileIndexProgressEventArgs>? ProgressChanged;
}

public interface IFileIndexSnapshotSource : IFileIndexSource
{
    Task<FileIndexBuildResult> RefreshAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot baseline,
        CancellationToken cancellationToken = default);
}
