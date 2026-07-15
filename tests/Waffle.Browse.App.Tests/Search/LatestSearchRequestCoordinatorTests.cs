using Waffle.Browse.App.Search;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Tests.Search;

internal static class LatestSearchRequestCoordinatorTests
{
    public static void LateResponseCannotReplaceLatestSearch()
    {
        var service = new DeferredFakeSearchService();
        using var coordinator = new LatestSearchRequestCoordinator(service);
        var first = coordinator.SearchAsync(new SearchQuery("first", SearchScope.GlobalIndex, 1000));
        var second = coordinator.SearchAsync(new SearchQuery("second", SearchScope.GlobalIndex, 1000));

        service.Complete("second");
        var secondResult = second.GetAwaiter().GetResult();
        service.Complete("first");
        var firstResult = first.GetAwaiter().GetResult();

        if (!secondResult.IsCurrent || firstResult.IsCurrent)
        {
            throw new InvalidOperationException("Only the latest search response should be current.");
        }
    }

    private sealed class DeferredFakeSearchService : IEverythingSearchService
    {
        private readonly Dictionary<string, TaskCompletionSource<EverythingSearchResponse>> requests = [];

        public Task<EverythingAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new EverythingAvailability(EverythingAvailabilityKind.Available, "available"));

        public Task<EverythingSearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<EverythingSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add(query.Text, completion);
            return completion.Task;
        }

        public void Complete(string query)
        {
            requests[query].SetResult(new EverythingSearchResponse(
                [],
                0,
                new EverythingAvailability(EverythingAvailabilityKind.Available, "available")));
        }
    }
}
