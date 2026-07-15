using Waffle.Browse.Core.Search;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.Core.Tests.Search;

internal static class FileSearchIndexTests
{
    private static readonly SearchProviderStatus Ready = SearchProviderStatus.Ready("ready");

    public static void SearchesNameAndPathWithScopeAndLimit()
    {
        var index = new FileSearchIndex();
        index.Replace([
            Entry(@"C:\Work\Reports", SearchItemKind.Folder),
            Entry(@"C:\Work\Reports\alpha-report.txt", SearchItemKind.File, 10),
            Entry(@"C:\Other\alpha.txt", SearchItemKind.File, 20)
        ]);

        var response = index.Search(
            new SearchQuery("alpha", SearchScope.CurrentFolder, 1, @"C:\Work"),
            Ready,
            "test");

        TestAssert.Equal(1L, response.TotalResults, "Current-folder scope should exclude other roots");
        TestAssert.Equal("alpha-report.txt", response.Results.Single().Name, "Name search should return the indexed file");
    }

    public static void AppliesCreateDeleteAndDirectoryRename()
    {
        var index = new FileSearchIndex();
        index.Replace([
            Entry(@"C:\Work\Old", SearchItemKind.Folder),
            Entry(@"C:\Work\Old\child.txt", SearchItemKind.File, 5)
        ]);

        index.Apply([FileIndexChange.Rename(@"C:\Work\Old", @"C:\Work\New")]);
        var renamed = index.Search(new SearchQuery("child", SearchScope.GlobalIndex, 1000), Ready, "test");
        TestAssert.Equal(@"C:\Work\New\child.txt", renamed.Results.Single().FullPath, "Directory rename should move descendant paths");

        index.Apply([FileIndexChange.Upsert(Entry(@"C:\Work\created.txt", SearchItemKind.File, 7))]);
        TestAssert.Equal(1L, index.Search(new SearchQuery("created", SearchScope.GlobalIndex, 1000), Ready, "test").TotalResults, "Create should be searchable");

        index.Apply([FileIndexChange.Delete(@"C:\Work\New")]);
        TestAssert.Equal(0L, index.Search(new SearchQuery("child", SearchScope.GlobalIndex, 1000), Ready, "test").TotalResults, "Deleting a directory should remove descendants");
    }

    public static void SortsFoldersFirstAndCapsResults()
    {
        var index = new FileSearchIndex();
        index.Replace([
            Entry(@"C:\Zeta-item.txt", SearchItemKind.File),
            Entry(@"C:\Alpha-item", SearchItemKind.Folder),
            Entry(@"C:\Beta-item.txt", SearchItemKind.File)
        ]);

        var response = index.Search(new SearchQuery("item", SearchScope.GlobalIndex, 2), Ready, "test");
        TestAssert.Equal(3L, response.TotalResults, "Total should be measured before the result cap");
        TestAssert.Equal(SearchItemKind.Folder, response.Results[0].Kind, "Folders should sort before files");
        TestAssert.Equal(2, response.Results.Count, "Results should respect MaxResults");
    }

    public static void ReplaceAndApplyPublishesOnlyTheCompletedGeneration()
    {
        var index = new FileSearchIndex();
        index.Replace([Entry(@"C:\Stable\previous.txt", SearchItemKind.File)]);

        using var replacementStarted = new ManualResetEventSlim();
        using var continueReplacement = new ManualResetEventSlim();
        using var changesStarted = new ManualResetEventSlim();
        using var continueChanges = new ManualResetEventSlim();

        IEnumerable<FileIndexEntry> Replacement()
        {
            replacementStarted.Set();
            continueReplacement.Wait();
            yield return Entry(@"C:\Work\Old", SearchItemKind.Folder);
            yield return Entry(@"C:\Work\Old\keep.txt", SearchItemKind.File);
            yield return Entry(@"C:\Work\Old\Removed", SearchItemKind.Folder);
            yield return Entry(@"C:\Work\Old\Removed\child.txt", SearchItemKind.File);
        }

        IEnumerable<FileIndexChange> Changes()
        {
            changesStarted.Set();
            continueChanges.Wait();
            yield return FileIndexChange.Rename(@"C:\Work\Old", @"C:\Work\New");
            yield return FileIndexChange.Delete(@"C:\Work\New\Removed");
            yield return FileIndexChange.Upsert(Entry(@"C:\Work\New\created.txt", SearchItemKind.File));
        }

        var publish = Task.Run(() => index.ReplaceAndApply(Replacement(), Changes()));
        var replacementWasPreparedWithoutLock = false;
        var changesWereAppliedWithoutLock = false;
        var replacementEnumerationBegan = false;
        var changeEnumerationBegan = false;
        try
        {
            replacementEnumerationBegan = replacementStarted.Wait(TimeSpan.FromSeconds(2));
            if (replacementEnumerationBegan)
            {
                replacementWasPreparedWithoutLock = CanReadOnlyPreviousGeneration(index);
            }

            continueReplacement.Set();
            changeEnumerationBegan = changesStarted.Wait(TimeSpan.FromSeconds(2));
            if (changeEnumerationBegan)
            {
                changesWereAppliedWithoutLock = CanReadOnlyPreviousGeneration(index);
            }
        }
        finally
        {
            continueReplacement.Set();
            continueChanges.Set();
        }

        publish.GetAwaiter().GetResult();
        TestAssert.True(replacementEnumerationBegan, "Replacement enumeration should begin");
        TestAssert.True(changeEnumerationBegan, "Buffered change enumeration should begin");
        TestAssert.True(replacementWasPreparedWithoutLock, "Replacement should be prepared before entering the write lock");
        TestAssert.True(changesWereAppliedWithoutLock, "Buffered changes should be applied before entering the write lock");
        TestAssert.Equal(0L, Search(index, "previous").TotalResults, "The previous generation should be replaced");
        TestAssert.Equal(@"C:\Work\New\keep.txt", Search(index, "keep").Results.Single().FullPath, "Directory rename should move replacement descendants");
        TestAssert.Equal(0L, Search(index, "child").TotalResults, "Directory delete should remove replacement descendants");
        TestAssert.Equal(@"C:\Work\New\created.txt", Search(index, "created").Results.Single().FullPath, "Upsert should be included in the published generation");
    }

    public static void MetadataUpdatesPreserveNativeIdentityButCreatesReplaceIt()
    {
        const string path = @"C:\Work\native.txt";
        var reference = new FileIndexFileReference(10, 20);
        var index = new FileSearchIndex();
        index.Replace([
            new FileIndexEntry(
                path,
                "native.txt",
                @"C:\Work",
                SearchItemKind.File,
                1,
                DateTimeOffset.UnixEpoch,
                "volume-id",
                reference)
        ]);

        var metadata = new FileIndexEntry(
            path,
            "native.txt",
            @"C:\Work",
            SearchItemKind.File,
            2,
            DateTimeOffset.UnixEpoch.AddMinutes(1));
        index.Apply([FileIndexChange.UpdateMetadata(metadata)]);

        var updated = index.Snapshot().Single();
        TestAssert.Equal(2L, updated.Size, "Changed metadata should replace the previous size");
        TestAssert.Equal("volume-id", updated.VolumeId, "Changed metadata should preserve the native volume identity");
        TestAssert.Equal<FileIndexFileReference?>(reference, updated.FileReferenceNumber, "Changed metadata should preserve the native file reference");

        index.Apply([FileIndexChange.Upsert(metadata)]);
        var recreated = index.Snapshot().Single();
        TestAssert.Equal<string?>(null, recreated.VolumeId, "A create upsert must not reuse identity from a replaced file");
        TestAssert.Equal<FileIndexFileReference?>(null, recreated.FileReferenceNumber, "A create upsert must not reuse the old file reference");
    }

    private static bool CanReadOnlyPreviousGeneration(FileSearchIndex index)
    {
        var search = Task.Run(() => (
            Previous: Search(index, "previous").TotalResults,
            Replacement: Search(index, "keep").TotalResults));
        return search.Wait(TimeSpan.FromSeconds(1))
            && search.Result is { Previous: 1, Replacement: 0 };
    }

    private static SearchResponse Search(FileSearchIndex index, string text) =>
        index.Search(new SearchQuery(text, SearchScope.GlobalIndex, 1000), Ready, "test");

    private static FileIndexEntry Entry(string path, SearchItemKind kind, long? size = null) =>
        new(
            path,
            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetDirectoryName(path) ?? string.Empty,
            kind,
            size,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
}
