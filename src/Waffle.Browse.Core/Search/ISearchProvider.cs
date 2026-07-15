namespace Waffle.Browse.Core.Search;

public interface ISearchProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<SearchProviderStatus> CheckStatusAsync(CancellationToken cancellationToken = default);

    Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
}
