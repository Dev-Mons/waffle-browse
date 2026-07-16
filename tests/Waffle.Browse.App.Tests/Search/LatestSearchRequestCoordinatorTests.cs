using Waffle.Browse.App.Search;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Tests.Search;

internal static class LatestSearchRequestCoordinatorTests
{
    public static void LateResponseCannotReplaceLatestSearch()
    {
        var service = new DeferredFakeSearchService();
        using var coordinator = new LatestSearchRequestCoordinator(service);
        var first = coordinator.SearchAsync(new SearchQuery("first", SearchScope.CurrentFolder, 1000, @"C:\Work"));
        var second = coordinator.SearchAsync(new SearchQuery("second", SearchScope.CurrentFolder, 1000, @"C:\Work"));

        service.Complete("second");
        var secondResult = second.GetAwaiter().GetResult();
        service.Complete("first");
        var firstResult = first.GetAwaiter().GetResult();

        if (!secondResult.IsCurrent || firstResult.IsCurrent)
        {
            throw new InvalidOperationException("Only the latest search response should be current.");
        }
    }

    private sealed class DeferredFakeSearchService : ISearchProvider
    {
        private readonly Dictionary<string, TaskCompletionSource<SearchResponse>> requests = [];

        public string Id => "fake";

        public string DisplayName => "Fake";

        public Task<SearchProviderStatus> CheckStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SearchProviderStatus.Ready("available"));

        public Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<SearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add(query.Text, completion);
            return completion.Task;
        }

        public void Complete(string query)
        {
            requests[query].SetResult(new SearchResponse(
                [],
                0,
                SearchProviderStatus.Ready("available"),
                Id));
        }
    }
}
