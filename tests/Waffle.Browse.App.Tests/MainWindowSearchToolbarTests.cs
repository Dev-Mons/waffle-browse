using System.IO;

namespace Waffle.Browse.App.Tests;

internal static class MainWindowSearchToolbarTests
{
    public static void SearchToolbarDoesNotExposeSearchScopeSelector()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml"));

        if (xaml.Contains("SearchScopeBox", StringComparison.Ordinal)
            || xaml.Contains("All open panels", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Search toolbar should not expose a search scope selector.");
        }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
