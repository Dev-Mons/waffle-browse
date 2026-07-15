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

    public static void CompletedBuildSummarizesWarningsWithoutChangingSkippedCount()
    {
        var warningOnlyState = BuildState(new FileIndexBuildResult(
            [],
            [],
            ["MFT 접근이 거부되어 재귀 검색을 사용했습니다.", "메타데이터를 읽지 못했습니다.", "고아 레코드를 격리했습니다.", "볼륨 정보가 변경되었습니다."],
            SkippedPathCount: 0));

        TestAssert.True(
            warningOnlyState.ErrorMessage?.Contains("MFT 접근이 거부", StringComparison.Ordinal) == true,
            "Completed build should surface source warnings");
        TestAssert.True(
            warningOnlyState.ErrorMessage?.Contains("(외 1개)", StringComparison.Ordinal) == true,
            "Warning summary should report omitted warnings");
        TestAssert.False(
            warningOnlyState.ErrorMessage?.Contains("건너뜀", StringComparison.Ordinal) == true,
            "Warnings alone should not be reported as skipped paths");

        var skippedState = BuildState(new FileIndexBuildResult([], [], [], SkippedPathCount: 2));
        TestAssert.Equal(
            "일부 경로를 건너뜀: 2개",
            skippedState.ErrorMessage,
            "Skipped path summary should preserve the skipped count meaning");
    }

    public static void CanceledRebuildRestoresPreviousReadyGeneration()
    {
        var root = Path.GetTempPath();
        var entry = new FileIndexEntry(
            Path.Combine(root, "previous-generation.txt"),
            "previous-generation.txt",
            root,
            SearchItemKind.File,
            7,
            DateTimeOffset.UtcNow);
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var snapshot = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(FileIndexBuildState.Ready, 4, 1, completedAt, []),
            [entry]);
        using var provider = new WaffleFileSearchProvider(
            new CancelingFileIndexSource(),
            new InMemoryFileIndexStore(snapshot),
            [root],
            watchChanges: false);

        ExpectCancellation(provider.InitializeAsync());

        TestAssert.Equal(FileIndexBuildState.Ready, provider.State.BuildState, "Canceled rebuild should restore the previous ready state");
        TestAssert.Equal(4L, provider.State.Generation, "Canceled rebuild should preserve the previous generation number");
        TestAssert.Equal(completedAt, provider.State.LastCompletedAt, "Canceled rebuild should preserve completion metadata");
        TestAssert.Equal(1L, Search(provider, "previous-generation").TotalResults, "Canceled rebuild should keep the previous searchable snapshot");
    }

    public static void CanceledInitialRebuildRestoresEmptyState()
    {
        using var provider = new WaffleFileSearchProvider(
            new CancelingFileIndexSource(),
            new InMemoryFileIndexStore(),
            [Path.GetTempPath()],
            watchChanges: false);

        ExpectCancellation(provider.InitializeAsync());

        TestAssert.Equal(FileIndexBuildState.Empty, provider.State.BuildState, "Canceled first rebuild should restore the empty state");
        TestAssert.Equal(0L, provider.State.Generation, "Canceled first rebuild should not create a generation");
        TestAssert.Equal(0L, provider.State.ItemCount, "Canceled first rebuild should not publish items");
        TestAssert.Equal<DateTimeOffset?>(null, provider.State.LastCompletedAt, "Canceled first rebuild should not record completion");
    }

    public static void FailedRebuildAppliesBufferedChangesToPreviousGeneration()
    {
        var root = Path.GetTempPath();
        var previous = Entry(Path.Combine(root, "previous.txt"));
        var buffered = Entry(Path.Combine(root, "buffered.txt"));
        var snapshot = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(FileIndexBuildState.Ready, 2, 1, DateTimeOffset.UtcNow, []),
            [previous]);
        var source = new GatedFailureSource();
        using var provider = new WaffleFileSearchProvider(
            source,
            new InMemoryFileIndexStore(snapshot),
            [root],
            watchChanges: false);

        var initialize = provider.InitializeAsync();
        TestAssert.True(source.Started.Wait(TimeSpan.FromSeconds(2)), "Rebuild source should start");
        QueueBufferedChange(provider, FileIndexChange.Upsert(buffered));
        source.Continue.Set();
        initialize.GetAwaiter().GetResult();

        TestAssert.Equal(FileIndexBuildState.Ready, provider.State.BuildState, "A failed refresh should retain the previous ready generation");
        TestAssert.Equal(1L, Search(provider, "previous").TotalResults, "The previous generation should remain searchable");
        TestAssert.Equal(1L, Search(provider, "buffered").TotalResults, "A buffered watcher change should be applied to the previous generation");
    }

    public static void FailedInitialBuildDoesNotPublishBufferedPartialChanges()
    {
        var root = Path.GetTempPath();
        var source = new GatedFailureSource();
        using var provider = new WaffleFileSearchProvider(
            source,
            new InMemoryFileIndexStore(),
            [root],
            watchChanges: false);

        var initialize = provider.InitializeAsync();
        TestAssert.True(source.Started.Wait(TimeSpan.FromSeconds(2)), "Initial build source should start");
        QueueBufferedChange(provider, FileIndexChange.Upsert(Entry(Path.Combine(root, "partial.txt"))));
        source.Continue.Set();
        initialize.GetAwaiter().GetResult();

        TestAssert.Equal(FileIndexBuildState.Failed, provider.State.BuildState, "A failed first build should remain unavailable");
        TestAssert.Equal(0L, Search(provider, "partial").TotalResults, "Buffered events must not expose a partial first generation");
    }

    private static FileIndexState BuildState(FileIndexBuildResult result)
    {
        using var provider = new WaffleFileSearchProvider(
            new FixedFileIndexSource(result),
            new InMemoryFileIndexStore(),
            [Path.GetTempPath()],
            watchChanges: false);
        provider.InitializeAsync().GetAwaiter().GetResult();
        return provider.State;
    }

    private static void ExpectCancellation(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected the rebuild to be canceled.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static FileIndexEntry Entry(string path) =>
        new(
            path,
            Path.GetFileName(path),
            Path.GetDirectoryName(path) ?? string.Empty,
            SearchItemKind.File,
            1,
            DateTimeOffset.UtcNow);

    private static void QueueBufferedChange(WaffleFileSearchProvider provider, FileIndexChange change)
    {
        var method = typeof(WaffleFileSearchProvider).GetMethod(
            "ApplyChange",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        TestAssert.NotNull(method, "Provider buffered-change method should exist");
        method!.Invoke(provider, [change]);
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

    private sealed class FixedFileIndexSource(FileIndexBuildResult result) : IFileIndexSource
    {
        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class CancelingFileIndexSource : IFileIndexSource
    {
        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class GatedFailureSource : IFileIndexSource
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Continue { get; } = new();

        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default) =>
            Task.Run(new Func<FileIndexBuildResult>(() =>
            {
                Started.Set();
                Continue.Wait(cancellationToken);
                throw new IOException("Simulated rebuild failure.");
            }), cancellationToken);
    }

    private sealed class InMemoryFileIndexStore(FileIndexSnapshot? snapshot = null) : IFileIndexStore
    {
        private FileIndexSnapshot? snapshot = snapshot;

        public Task<FileIndexLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot is null
                ? new FileIndexLoadResult(FileIndexLoadKind.Missing)
                : new FileIndexLoadResult(FileIndexLoadKind.Loaded, snapshot));
        }

        public Task SaveAsync(FileIndexSnapshot next, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = next;
            return Task.CompletedTask;
        }
    }
}
