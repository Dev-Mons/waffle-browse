namespace Waffle.Browse.Core.Search;

public sealed record SearchQuery(
    string Text,
    SearchScope Scope,
    int MaxResults,
    string? RootPath = null,
    SearchSort Sort = SearchSort.NameAscending);
