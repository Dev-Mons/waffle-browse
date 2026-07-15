namespace Waffle.Browse.Core.Search.Indexing;

public enum FileIndexBuildState
{
    Empty,
    Loading,
    Ready,
    Rebuilding,
    NeedsRebuild,
    Failed
}

public sealed record FileIndexCheckpoint(
    string RootPath,
    string? VolumeId,
    string? FileSystem,
    ulong? JournalId,
    long? NextUsn,
    DateTimeOffset CapturedAt,
    uint? VolumeSerialNumber = null);

public sealed record FileIndexState(
    FileIndexBuildState BuildState,
    long Generation,
    long ItemCount,
    DateTimeOffset? LastCompletedAt,
    IReadOnlyList<FileIndexCheckpoint> Checkpoints,
    string? ErrorMessage = null)
{
    public static FileIndexState Empty { get; } =
        new(FileIndexBuildState.Empty, 0, 0, null, []);
}

public sealed record FileIndexSnapshot(
    int FormatVersion,
    FileIndexState State,
    IReadOnlyList<FileIndexEntry> Entries)
{
    public const int CurrentFormatVersion = 3;
}
