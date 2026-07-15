namespace Waffle.Browse.Core.Search.Indexing.Ntfs;

internal sealed record NtfsMftRecord(
    FileReferenceId FileReferenceId,
    FileReferenceId ParentFileReferenceId,
    string Name,
    FileAttributes Attributes,
    long Usn = 0,
    uint Reason = 0);

internal sealed record NtfsMftBatch(
    ulong NextFileReferenceNumber,
    IReadOnlyList<NtfsMftRecord> Records);

internal sealed record NtfsUsnJournalBatch(
    long NextUsn,
    IReadOnlyList<NtfsMftRecord> Records);

internal sealed record NtfsResolvedPath(
    NtfsMftRecord Record,
    string FullPath,
    string ParentPath);

internal sealed record NtfsPathResolutionResult(
    IReadOnlyList<NtfsResolvedPath> Entries,
    IReadOnlyList<string> Warnings,
    long SkippedPathCount);
