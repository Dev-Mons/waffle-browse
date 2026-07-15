namespace Waffle.Browse.Core.Search;

public interface IEverythingSearchService
{
    Task<EverythingAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<EverythingSearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
