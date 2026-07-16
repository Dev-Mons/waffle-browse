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

    public static void MainWindowUsesWaffleToolbarHierarchy()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml"));

        var requiredFragments = new[]
        {
            "Tag=\"파일 및 폴더 검색\"",
            "Style=\"{StaticResource ToolbarIconButtonStyle}\"",
            "Style=\"{StaticResource SegmentButtonStyle}\"",
            "Margin=\"4,2,4,4\"",
            "Data=\"{StaticResource LayoutSingleGeometry}\"",
            "Data=\"{StaticResource LayoutColumnTwoGeometry}\"",
            "Data=\"{StaticResource LayoutRowTwoGeometry}\"",
            "Data=\"{StaticResource LayoutColumnTwoSplitRightGeometry}\"",
            "Data=\"{StaticResource LayoutCellFourGeometry}\""
        };

        if (requiredFragments.Any(fragment => !xaml.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Main window should use the Waffle toolbar and soft-grid workspace hierarchy.");
        }

        if (xaml.Contains("<TextBlock Text=\"Waffle Browse\"", StringComparison.Ordinal)
            || xaml.Contains("Source=\"Assets/AppIcon.png\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The native title bar should own product identity without duplicating it in the search toolbar.");
        }

        var design = File.ReadAllText(FindRepositoryFile("DESIGN.md"));
        if (!design.Contains("Toasted Soft Grid", StringComparison.Ordinal)
            || !design.Contains("#D99018", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DESIGN.md should define the Waffle visual direction and dark honey accent.");
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
