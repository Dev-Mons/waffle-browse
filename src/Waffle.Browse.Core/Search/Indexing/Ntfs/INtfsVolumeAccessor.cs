namespace Waffle.Browse.Core.Search.Indexing.Ntfs;

internal sealed class NtfsJournalInvalidatedException(string message, Exception? innerException = null)
    : IOException(message, innerException);

internal sealed record NtfsVolumeIdentity(
    string RootPath,
    string VolumeId,
    string FileSystem,
    uint SerialNumber,
    FileReferenceId RootFileReferenceNumber);

internal sealed record NtfsFileMetadata(long? Size, DateTimeOffset? ModifiedAt);

internal sealed record NtfsJournalCheckpoint(ulong JournalId, long NextUsn);

internal sealed record NtfsJournalState(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long MaximumUsn);

internal interface INtfsVolumeAccessorFactory
{
    INtfsVolumeAccessor Open(string rootPath);
}

internal interface INtfsVolumeAccessor : IDisposable
{
    NtfsVolumeIdentity Identity { get; }

    NtfsMftBatch? ReadMftBatch(ulong startFileReferenceNumber, CancellationToken cancellationToken);

    NtfsFileMetadata? TryReadMetadata(FileReferenceId fileReferenceNumber, bool isDirectory);

    FileIndexEntry? TryReadCurrentEntry(FileReferenceId fileReferenceNumber);

    IReadOnlyList<FileIndexEntry> TryReadCurrentEntries(FileReferenceId fileReferenceNumber);

    NtfsJournalCheckpoint? TryReadJournalCheckpoint();

    NtfsJournalState? TryReadJournalState();

    NtfsUsnJournalBatch ReadUsnJournalBatch(
        long startUsn,
        ulong journalId,
        CancellationToken cancellationToken);

    void EnsureIdentityUnchanged();
}
