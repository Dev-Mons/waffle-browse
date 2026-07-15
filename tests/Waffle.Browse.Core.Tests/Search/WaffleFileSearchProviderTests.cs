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

    public static void LoadedSnapshotUsesIncrementalRefresh()
    {
        var root = Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath();
        var baselineEntry = Entry(Path.Combine(root, "before.txt"));
        var baseline = Snapshot(baselineEntry, generation: 4);
        var refreshedEntry = Entry(Path.Combine(root, "after.txt"));
        var source = new RecordingSnapshotSource(
            (_, loaded) =>
            {
                TestAssert.Equal(baseline, loaded, "Incremental refresh should receive the loaded snapshot");
                return new FileIndexBuildResult([refreshedEntry], loaded.State.Checkpoints, []);
            });
        var store = new MemoryIndexStore(baseline);
        using var provider = new WaffleFileSearchProvider(source, store, [root], watchChanges: false);

        provider.InitializeAsync().GetAwaiter().GetResult();

        TestAssert.Equal(0, source.BuildCallCount, "A valid loaded snapshot should not force a full source build");
        TestAssert.Equal(1, source.RefreshCallCount, "A valid loaded snapshot should use incremental refresh once");
        TestAssert.Equal(1L, Search(provider, "after").TotalResults, "The completed incremental generation should be published");
        TestAssert.Equal(0L, Search(provider, "before").TotalResults, "The old generation should be replaced only after refresh completes");
        TestAssert.NotNull(store.Saved, "The refreshed generation and checkpoint should be persisted together");
    }

    public static void FailedIncrementalRefreshKeepsLastGoodSnapshot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath();
        var baselineEntry = Entry(Path.Combine(root, "stable.txt"));
        var baseline = Snapshot(baselineEntry, generation: 9);
        var source = new RecordingSnapshotSource((_, _) => throw new InvalidDataException("journal wrapped"));
        var store = new MemoryIndexStore(baseline);
        using var provider = new WaffleFileSearchProvider(source, store, [root], watchChanges: false);

        provider.InitializeAsync().GetAwaiter().GetResult();

        TestAssert.Equal(1L, Search(provider, "stable").TotalResults, "A failed refresh must keep the last good generation searchable");
        TestAssert.Equal(9L, provider.State.Generation, "A failed refresh must not advance the generation");
        TestAssert.Equal(FileIndexBuildState.Ready, provider.State.BuildState, "A loaded generation should remain ready after refresh failure");
        TestAssert.True(provider.State.ErrorMessage?.Contains("journal wrapped", StringComparison.Ordinal) == true, "The refresh failure should remain visible in provider state");
        TestAssert.True(store.Saved is null, "A failed refresh must not overwrite the last good persisted snapshot");
    }

    public static void JsonPersistencePreservesFileIdWidth()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"waffle-index-id-width-{Guid.NewGuid():N}");
        var indexPath = Path.Combine(testRoot, "index.json");
        Directory.CreateDirectory(testRoot);
        try
        {
            var entry = Entry(Path.Combine(testRoot, "wide.txt")) with
            {
                FileReferenceNumber = new FileReferenceId(42, 0, FileReferenceIdWidth.Bits128)
            };
            var snapshot = Snapshot(entry, generation: 1);
            var store = new JsonFileIndexStore(indexPath);

            store.SaveAsync(snapshot).GetAwaiter().GetResult();
            var loaded = store.LoadAsync().GetAwaiter().GetResult();

            TestAssert.Equal(FileIndexLoadKind.Loaded, loaded.Kind, "The current file-ID format should load successfully");
            TestAssert.True(
                loaded.Snapshot!.Entries.Single().FileReferenceNumber!.Value.Is128Bit,
                "JSON persistence must preserve a 128-bit ID whose high half is zero");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    public static void NativeWatcherChangesUseCheckpointRefresh()
    {
        var root = Path.Combine(Path.GetTempPath(), $"waffle-native-watcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var initial = Entry(Path.Combine(root, "initial.txt")) with
            {
                VolumeId = "native-volume",
                FileReferenceNumber = new FileReferenceId(1)
            };
            var created = Entry(Path.Combine(root, "native-created.txt")) with
            {
                VolumeId = "native-volume",
                FileReferenceNumber = new FileReferenceId(2)
            };
            var checkpoint = new FileIndexCheckpoint(
                root,
                "native-volume",
                "NTFS",
                10,
                100,
                DateTimeOffset.UtcNow,
                20);
            var source = new RecordingSnapshotSource(
                (_, loaded) => new FileIndexBuildResult([initial, created], loaded.State.Checkpoints, []),
                _ => new FileIndexBuildResult([initial], [checkpoint], []));
            var store = new MemoryIndexStore(null);
            using var provider = new WaffleFileSearchProvider(source, store, [root], watchChanges: true);
            provider.InitializeAsync().GetAwaiter().GetResult();

            File.WriteAllText(created.FullPath, "created");
            WaitUntil(
                () => source.RefreshCallCount > 0 && Search(provider, "native-created").TotalResults == 1,
                "A native watcher event should trigger checkpoint refresh instead of a recursive upsert");

            var persistedCreated = store.Saved!.Entries.Single(entry => entry.Name == "native-created.txt");
            TestAssert.Equal(
                new FileReferenceId(2),
                persistedCreated.FileReferenceNumber,
                "Checkpoint refresh must preserve the native file reference ID");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeWatcherRefreshesOnlyChangedRoot()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"waffle-root-refresh-{Guid.NewGuid():N}");
        var firstRoot = Path.Combine(testRoot, "first");
        var secondRoot = Path.Combine(testRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var first = Entry(Path.Combine(firstRoot, "first.txt")) with
            {
                VolumeId = "first-volume",
                FileReferenceNumber = new FileReferenceId(1)
            };
            var second = Entry(Path.Combine(secondRoot, "second.txt")) with
            {
                VolumeId = "second-volume",
                FileReferenceNumber = new FileReferenceId(2)
            };
            var created = Entry(Path.Combine(firstRoot, "exclusive-new-item.txt")) with
            {
                VolumeId = "first-volume",
                FileReferenceNumber = new FileReferenceId(3)
            };
            var firstCheckpoint = NativeCheckpoint(firstRoot, "first-volume", 10);
            var secondCheckpoint = NativeCheckpoint(secondRoot, "second-volume", 20);
            var source = new RecordingSnapshotSource(
                (refreshRoots, _) => string.Equals(
                    refreshRoots.Single(),
                    firstRoot,
                    StringComparison.OrdinalIgnoreCase)
                    ? new FileIndexBuildResult([first, created], [firstCheckpoint], [])
                    : new FileIndexBuildResult([second], [secondCheckpoint], []),
                _ => new FileIndexBuildResult(
                    [first, second],
                    [firstCheckpoint, secondCheckpoint],
                    [$"{secondRoot}: second root degraded"]));
            using var provider = new WaffleFileSearchProvider(
                source,
                new MemoryIndexStore(null),
                [firstRoot, secondRoot],
                watchChanges: true);
            provider.InitializeAsync().GetAwaiter().GetResult();

            provider.ScheduleRootRefresh(firstRoot);
            WaitUntil(
                () => source.RefreshCallCount > 0,
                $"The targeted source refresh did not start (error={provider.State.ErrorMessage})");
            WaitUntil(
                () => provider.State.Generation >= 2,
                "The targeted source refresh did not publish a generation");

            TestAssert.Equal(1, source.LastRefreshRoots!.Count, "One watcher root should produce one targeted source refresh");
            TestAssert.Equal(firstRoot, source.LastRefreshRoots[0], "The unaffected root should not be recursively refreshed");
            TestAssert.Equal(1L, Search(provider, "exclusive-new-item").TotalResults, "The changed native root should publish its refreshed entries");
            TestAssert.Equal(1L, Search(provider, "second").TotalResults, "Targeted refresh must retain other root generations");
            TestAssert.True(
                provider.State.ErrorMessage?.Contains("second root degraded", StringComparison.Ordinal) == true,
                "Targeted refresh should retain warnings for unaffected roots");

            provider.ScheduleRootRefresh(secondRoot);
            WaitUntil(
                () => provider.State.Generation >= 3,
                "The recovered root refresh did not publish a generation");
            TestAssert.True(
                provider.State.ErrorMessage is null,
                "Refreshing a recovered root should clear only that root's stale warning");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    public static void NestedRootRefreshPreservesAncestorCheckpoint()
    {
        var parentRoot = Path.Combine(Path.GetTempPath(), $"waffle-parent-refresh-{Guid.NewGuid():N}");
        var childRoot = Path.Combine(parentRoot, "child");
        Directory.CreateDirectory(childRoot);
        try
        {
            var parentEntry = Entry(Path.Combine(parentRoot, "parent-only.txt")) with
            {
                VolumeId = "shared-volume",
                FileReferenceNumber = new FileReferenceId(1)
            };
            var childEntry = Entry(Path.Combine(childRoot, "child-new.txt")) with
            {
                VolumeId = "shared-volume",
                FileReferenceNumber = new FileReferenceId(2)
            };
            var parentCheckpoint = NativeCheckpoint(parentRoot, "shared-volume", 10);
            var childCheckpoint = NativeCheckpoint(childRoot, "shared-volume", 20);
            var source = new RecordingSnapshotSource(
                (_, _) => new FileIndexBuildResult([childEntry], [childCheckpoint], []),
                _ => new FileIndexBuildResult(
                    [parentEntry],
                    [parentCheckpoint, childCheckpoint],
                    []));
            using var provider = new WaffleFileSearchProvider(
                source,
                new MemoryIndexStore(null),
                [parentRoot, childRoot],
                watchChanges: false);
            provider.InitializeAsync().GetAwaiter().GetResult();

            provider.ScheduleRootRefresh(childRoot);
            WaitUntil(
                () => provider.State.Generation >= 2,
                "The nested root refresh did not publish a generation");

            TestAssert.True(
                provider.State.Checkpoints.Any(checkpoint => string.Equals(
                    checkpoint.RootPath,
                    parentRoot,
                    StringComparison.OrdinalIgnoreCase)),
                "Refreshing a child root must preserve its ancestor checkpoint");
            TestAssert.True(
                provider.State.Checkpoints.Any(checkpoint => string.Equals(
                    checkpoint.RootPath,
                    childRoot,
                    StringComparison.OrdinalIgnoreCase)),
                "Refreshing a child root should replace its own checkpoint");
            TestAssert.Equal(1L, Search(provider, "parent-only").TotalResults, "Ancestor-only entries should remain searchable");
            TestAssert.Equal(1L, Search(provider, "child-new").TotalResults, "The child root should publish its refreshed entries");
        }
        finally
        {
            Directory.Delete(parentRoot, recursive: true);
        }
    }

    private static SearchResponse Search(WaffleFileSearchProvider provider, string text) =>
        provider.SearchAsync(new SearchQuery(text, SearchScope.GlobalIndex, 1000)).GetAwaiter().GetResult();

    private static FileIndexEntry Entry(string path) =>
        new(
            path,
            Path.GetFileName(path),
            Path.GetDirectoryName(path) ?? string.Empty,
            SearchItemKind.File,
            1,
            DateTimeOffset.Parse("2026-07-15T00:00:00Z"));

    private static FileIndexSnapshot Snapshot(FileIndexEntry entry, long generation) =>
        new(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                generation,
                1,
                DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
                []),
            [entry]);

    private static FileIndexCheckpoint NativeCheckpoint(string root, string volumeId, long nextUsn) =>
        new(root, volumeId, "NTFS", 1, nextUsn, DateTimeOffset.UtcNow, 1);

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

    private sealed class RecordingSnapshotSource(
        Func<IReadOnlyList<string>, FileIndexSnapshot, FileIndexBuildResult> refresh,
        Func<IReadOnlyList<string>, FileIndexBuildResult>? build = null) : IFileIndexSnapshotSource
    {
        public int BuildCallCount { get; private set; }

        public int RefreshCallCount { get; private set; }

        public IReadOnlyList<string>? LastRefreshRoots { get; private set; }

        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCallCount++;
            return build is null
                ? throw new InvalidOperationException("The test did not expect a full build.")
                : Task.FromResult(build(roots));
        }

        public Task<FileIndexBuildResult> RefreshAsync(
            IReadOnlyList<string> roots,
            FileIndexSnapshot baseline,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCallCount++;
            LastRefreshRoots = roots.ToList();
            return Task.FromResult(refresh(roots, baseline));
        }
    }

    private sealed class MemoryIndexStore(FileIndexSnapshot? loaded) : IFileIndexStore
    {
        public FileIndexSnapshot? Saved { get; private set; }

        public Task<FileIndexLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(loaded is null
                ? new FileIndexLoadResult(FileIndexLoadKind.Missing)
                : new FileIndexLoadResult(FileIndexLoadKind.Loaded, loaded));
        }

        public Task SaveAsync(FileIndexSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved = snapshot;
            return Task.CompletedTask;
        }
    }
}
