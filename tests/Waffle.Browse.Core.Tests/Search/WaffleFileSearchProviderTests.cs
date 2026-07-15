using Waffle.Browse.Core.Search;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.Core.Tests.Search;

internal static class WaffleFileSearchProviderTests
{
    public static void BuildsPersistsAndTracksFileChanges()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"waffle-index-test-{Guid.NewGuid():N}");
        var indexedRoot = Path.Combine(testRoot, "root");
        var indexPath = Path.Combine(testRoot, "index.json");
        Directory.CreateDirectory(indexedRoot);
        try
        {
            File.WriteAllText(Path.Combine(indexedRoot, "alpha.txt"), "alpha");
            using var provider = new WaffleFileSearchProvider(
                new RecursiveFileIndexSource(),
                new JsonFileIndexStore(indexPath),
                [indexedRoot]);
            provider.InitializeAsync().GetAwaiter().GetResult();

            TestAssert.Equal(1L, Search(provider, "alpha").TotalResults, "Initial recursive build should index existing files");
            TestAssert.True(File.Exists(indexPath), "Completed index should be persisted");

            var created = Path.Combine(indexedRoot, "created.txt");
            File.WriteAllText(created, "created");
            WaitUntil(() => Search(provider, "created").TotalResults == 1, "Created file was not indexed");

            var renamed = Path.Combine(indexedRoot, "renamed.txt");
            File.Move(created, renamed);
            WaitUntil(() => Search(provider, "renamed").TotalResults == 1, "Renamed file was not indexed");
            WaitUntil(() => Search(provider, "created").TotalResults == 0, "Old rename path remained indexed");

            File.Delete(renamed);
            WaitUntil(() => Search(provider, "renamed").TotalResults == 0, "Deleted file remained indexed");
        }
        finally
        {
            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void CorruptPersistenceTriggersSafeRebuild()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"waffle-index-corrupt-{Guid.NewGuid():N}");
        var indexedRoot = Path.Combine(testRoot, "root");
        var indexPath = Path.Combine(testRoot, "index.json");
        Directory.CreateDirectory(indexedRoot);
        try
        {
            File.WriteAllText(Path.Combine(indexedRoot, "recovered.txt"), "data");
            File.WriteAllText(indexPath, "{not-json");
            using var provider = new WaffleFileSearchProvider(
                new RecursiveFileIndexSource(),
                new JsonFileIndexStore(indexPath),
                [indexedRoot],
                watchChanges: false);

            provider.InitializeAsync().GetAwaiter().GetResult();

            TestAssert.Equal(FileIndexBuildState.Ready, provider.State.BuildState, "Corrupt persistence should be rebuilt safely");
            TestAssert.Equal(1L, Search(provider, "recovered").TotalResults, "Rebuild should produce a searchable snapshot");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    public static void RecursiveBuildHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            new RecursiveFileIndexSource()
                .BuildAsync([Path.GetTempPath()], cancellation.Token)
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException("Canceled index build should not complete.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static SearchResponse Search(WaffleFileSearchProvider provider, string text) =>
        provider.SearchAsync(new SearchQuery(text, SearchScope.GlobalIndex, 1000)).GetAwaiter().GetResult();

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new InvalidOperationException(message);
    }
}
