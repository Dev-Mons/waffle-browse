namespace Waffle.Browse.Core.Search.Indexing;

public readonly record struct FileIndexFileReference(ulong Low, ulong High = 0);

public sealed record FileIndexEntry(
    string FullPath,
    string Name,
    string ParentPath,
    SearchItemKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? VolumeId = null,
    FileIndexFileReference? FileReferenceNumber = null)
{
    public SearchResultItem ToSearchResult() =>
        new(Name, FullPath, ParentPath, Kind, Size, ModifiedAt);
}
