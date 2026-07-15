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

    private static FileIndexEntry Entry(string path, SearchItemKind kind, long? size = null) =>
        new(
            path,
            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetDirectoryName(path) ?? string.Empty,
            kind,
            size,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
}
