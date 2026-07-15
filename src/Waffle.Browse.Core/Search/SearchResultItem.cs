namespace Waffle.Browse.Core.Search;

public sealed record SearchResultItem(
    string Name,
    string FullPath,
    string ParentPath,
    SearchItemKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt);
