using System.IO;

namespace Waffle.Browse.App.Tests;

internal static class MainWindowSearchToolbarTests
{
    public static void SearchToolbarUsesCurrentFolderOnly()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml"));

        if (!xaml.Contains("QuickSearchBox", StringComparison.Ordinal)
            || xaml.Contains("SearchScopeBox", StringComparison.Ordinal)
            || xaml.Contains("<ComboBoxItem Content=\"전체\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Search toolbar should search only the current folder without a global scope selector.");
        }
    }

    public static void MainWindowExposesFileIndexProgress()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml"));
        var requiredIds = new[]
        {
            "IndexStatusBar",
            "IndexStatusText",
            "IndexProgressBar",
            "IndexCountText",
            "IndexProgressText",
            "IndexDetailText"
        };

        if (requiredIds.Any(id => !xaml.Contains(id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Main window should expose index status, progress, count, and detail fields.");
        }
    }

    public static void FileIndexStatusBarOnlyShowsWhileBusy()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml"));
        var code = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml.cs"));
        if (!xaml.Contains("x:Name=\"IndexStatusBar\"", StringComparison.Ordinal)
            || !xaml.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal)
            || !code.Contains("IndexStatusBar.Visibility = isBusy", StringComparison.Ordinal)
            || !code.Contains("? Visibility.Visible", StringComparison.Ordinal)
            || !code.Contains(": Visibility.Collapsed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The index status bar should only be visible while indexing is active.");
        }
    }

    public static void SearchFocusStartsCurrentFolderIndexing()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml"));
        var code = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml.cs"));

        if (!xaml.Contains("GotKeyboardFocus=\"OnQuickSearchGotKeyboardFocus\"", StringComparison.Ordinal)
            || !code.Contains("IndexFolderAsync(root", StringComparison.Ordinal)
            || code.Contains("_ = InitializeFileIndexAsync()", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Indexing should start on search focus for the current folder, not during window startup.");
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
