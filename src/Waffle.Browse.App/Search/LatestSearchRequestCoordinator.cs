using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Search;

public sealed class LatestSearchRequestCoordinator : IDisposable
{
    private readonly ISearchProvider searchService;
    private readonly object syncRoot = new();
    private CancellationTokenSource? currentCancellation;
    private long currentVersion;
    private bool disposed;

    public LatestSearchRequestCoordinator(ISearchProvider searchService)
    {
        this.searchService = searchService;
    }

    public async Task<LatestSearchRequestResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource requestCancellation;
        long requestVersion;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requestVersion = ++currentVersion;
            currentCancellation?.Cancel();
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            currentCancellation = requestCancellation;
        }

        try
        {
            var response = await searchService.SearchAsync(query, requestCancellation.Token).ConfigureAwait(false);
            lock (syncRoot)
            {
                var isCurrent = !disposed
                    && requestVersion == currentVersion
                    && !requestCancellation.IsCancellationRequested;
                return new LatestSearchRequestResult(isCurrent, response);
            }
        }
        finally
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(currentCancellation, requestCancellation))
                {
                    currentCancellation = null;
                }
            }

            requestCancellation.Dispose();
        }
    }

    public void Cancel()
    {
        lock (syncRoot)
        {
            currentVersion++;
            currentCancellation?.Cancel();
            currentCancellation = null;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            currentVersion++;
            currentCancellation?.Cancel();
            currentCancellation = null;
        }
    }
}

public sealed record LatestSearchRequestResult(
    bool IsCurrent,
    SearchResponse Response);
