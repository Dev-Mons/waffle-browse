namespace Waffle.Browse.Core.Search;

public sealed record EverythingSearchResponse(
    IReadOnlyList<SearchResultItem> Results,
    uint TotalResults,
    EverythingAvailability Availability);
