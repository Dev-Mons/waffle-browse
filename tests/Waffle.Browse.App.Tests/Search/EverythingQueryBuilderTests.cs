using Waffle.Browse.App.Search;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Tests.Search;

internal static class EverythingQueryBuilderTests
{
    public static void GlobalSearchPreservesEverythingSyntax()
    {
        var result = EverythingQueryBuilder.Build(new SearchQuery("*.cs dm:today", SearchScope.GlobalIndex, 1000));
        if (result != "*.cs dm:today")
        {
            throw new InvalidOperationException($"Unexpected global query: {result}");
        }
    }

    public static void CurrentFolderSearchPrefixesRecursivePath()
    {
        var result = EverythingQueryBuilder.Build(new SearchQuery("report", SearchScope.CurrentFolder, 1000, @"C:\My Work"));
        if (result != "\"C:\\My Work\\\" report")
        {
            throw new InvalidOperationException($"Unexpected current-folder query: {result}");
        }
    }
}
