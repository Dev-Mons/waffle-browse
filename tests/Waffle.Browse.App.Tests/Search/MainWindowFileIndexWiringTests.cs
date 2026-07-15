using System.IO;

namespace Waffle.Browse.App.Tests.Search;

internal static class MainWindowFileIndexWiringTests
{
    public static void UsesWindowsSourceAndVersionTwoSnapshot()
    {
        var source = ReadMainWindowSource();
        if (!source.Contains("new WindowsFileIndexSource()", StringComparison.Ordinal)
            || !source.Contains("search-index-v2.json", StringComparison.Ordinal)
            || source.Contains("search-index-v1.json", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MainWindow should compose WindowsFileIndexSource with the version 2 snapshot path.");
        }
    }

    public static void IncludesReadyFixedDrivesBeforeFilesystemSelection()
    {
        var source = ReadMainWindowSource();
        const string methodStart = "private static IReadOnlyList<string> ResolveIndexRoots";
        const string nextMethod = "private void ApplyTheme";
        var start = source.IndexOf(methodStart, StringComparison.Ordinal);
        var end = start < 0 ? -1 : source.IndexOf(nextMethod, start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException("Could not locate MainWindow.ResolveIndexRoots for the wiring regression check.");
        }

        var method = source[start..end];
        if (!method.Contains("drive.IsReady", StringComparison.Ordinal)
            || !method.Contains("DriveType.Fixed", StringComparison.Ordinal)
            || method.Contains("DriveFormat", StringComparison.Ordinal)
            || method.Contains("\"NTFS\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ResolveIndexRoots should include every ready fixed drive and leave filesystem selection to WindowsFileIndexSource.");
        }
    }

    private static string ReadMainWindowSource() =>
        File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\MainWindow.xaml.cs"));

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
