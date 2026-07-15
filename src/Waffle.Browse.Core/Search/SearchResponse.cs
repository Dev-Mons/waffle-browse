namespace Waffle.Browse.Core.Search;

public sealed record SearchResponse(
    IReadOnlyList<SearchResultItem> Results,
    long TotalResults,
    SearchProviderStatus Status,
    string ProviderId);
