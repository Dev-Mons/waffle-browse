using Waffle.Browse.Core.Search;

namespace Waffle.Browse.Core.Tests.Search;

internal static class EverythingSearchLocationTests
{
    public static void GlobalLocationRoundTrips()
    {
        var value = EverythingSearchLocation.Build("report final", SearchScope.GlobalIndex, null);

        TestAssert.True(EverythingSearchLocation.TryParse(value, out var query), "Global search location should parse");
        TestAssert.Equal("report final", query.Text, "Search text should round-trip");
        TestAssert.Equal(SearchScope.GlobalIndex, query.Scope, "Global scope should round-trip");
        TestAssert.Equal(null, query.RootPath, "Global scope should not persist a root");
    }

    public static void CurrentFolderLocationRoundTrips()
    {
        var value = EverythingSearchLocation.Build("*.cs", SearchScope.CurrentFolder, @"C:\My Project");

        TestAssert.True(EverythingSearchLocation.TryParse(value, out var query), "Current-folder search location should parse");
        TestAssert.Equal(SearchScope.CurrentFolder, query.Scope, "Current-folder scope should round-trip");
        TestAssert.Equal(@"C:\My Project", query.RootPath, "Root path should round-trip");
    }

    public static void CurrentFolderLocationRequiresRoot()
    {
        var rejected = false;
        try
        {
            EverythingSearchLocation.Build("report", SearchScope.CurrentFolder, null);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        TestAssert.True(rejected, "Current-folder search should require a root path");
    }
}
