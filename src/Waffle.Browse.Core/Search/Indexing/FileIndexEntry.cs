namespace Waffle.Browse.Core.Search.Indexing;

public sealed record FileIndexEntry(
    string FullPath,
    string Name,
    string ParentPath,
    SearchItemKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? VolumeId = null,
    FileReferenceId? FileReferenceNumber = null)
{
    public SearchResultItem ToSearchResult() =>
        new(Name, FullPath, ParentPath, Kind, Size, ModifiedAt);
}
