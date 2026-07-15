using Waffle.Browse.Core.Search;

namespace Waffle.Browse.Core.Tests.Search;

internal static class FileSearchServiceTests
{
    public static void WindowsSearchLocationBuilderCreatesSingleRootSearchUri()
    {
        var uri = WindowsSearchLocationBuilder.BuildSearchUri("report final", [@"C:\Work"]);

        TestAssert.Equal(
            @"search-ms:query=report%20final&crumb=location:C%3A%5CWork,include,recursive",
            uri,
            "Shell search URI should include encoded query and one recursive location crumb");
    }

    public static void WindowsSearchLocationBuilderCreatesMultiRootSearchUri()
    {
        var uri = WindowsSearchLocationBuilder.BuildSearchUri("alpha", [@"C:\One", @"D:\Two"]);

        TestAssert.Equal(
            @"search-ms:query=alpha&crumb=location:C%3A%5COne,include,recursive&crumb=location:D%3A%5CTwo,include,recursive",
            uri,
            "Shell search URI should include one location crumb per distinct root");
    }

    public static void WindowsSearchLocationBuilderRejectsEmptyInputs()
    {
        var rejectedEmptyQuery = false;
        var rejectedEmptyRoots = false;

        try
        {
            WindowsSearchLocationBuilder.BuildSearchUri(" ", [@"C:\Work"]);
        }
        catch (ArgumentException)
        {
            rejectedEmptyQuery = true;
        }

        try
        {
            WindowsSearchLocationBuilder.BuildSearchUri("report", []);
        }
        catch (ArgumentException)
        {
            rejectedEmptyRoots = true;
        }

        TestAssert.True(rejectedEmptyQuery, "Builder should reject empty query text");
        TestAssert.True(rejectedEmptyRoots, "Builder should reject empty root list");
    }

    public static void SearchReturnsMatchingFilesAndFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Reports"));
        File.WriteAllText(Path.Combine(root, "report-final.txt"), "x");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "x");

        var results = new FileSearchService().Search([root], new SearchQuery("report", SearchScope.CurrentPanel, 50), CancellationToken.None);

        TestAssert.Equal(2, results.Count, "Search should find matching file and folder names");
        TestAssert.True(results.Any(item => item.Kind == SearchItemKind.File && item.Name == "report-final.txt"), "Matching file should be returned");
        TestAssert.True(results.Any(item => item.Kind == SearchItemKind.Folder && item.Name == "Reports"), "Matching folder should be returned");
    }

    public static void SearchSkipsUnavailablePathsAndRespectsLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "alpha-1.txt"), "x");
        File.WriteAllText(Path.Combine(root, "alpha-2.txt"), "x");

        var results = new FileSearchService().Search([root, Path.Combine(root, "missing")], new SearchQuery("alpha", SearchScope.AllOpenPanels, 1), CancellationToken.None);

        TestAssert.Equal(1, results.Count, "Search should stop at the requested result limit");
    }
}
