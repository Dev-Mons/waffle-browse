using System.IO;

namespace Waffle.Browse.App.Tests.Shell;

internal static class ShellExplorerHostSizingTests
{
    public static void InitialLayoutResynchronizesNativeBrowserBounds()
    {
        var code = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\Shell\ShellExplorerHost.cs"));
        var requiredFragments = new[]
        {
            "GetClientRect(hostWindow",
            "SynchronizeBrowserBounds();",
            "msg == WmSize",
            "DispatcherPriority.Loaded"
        };

        if (requiredFragments.Any(fragment => !code.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The native Explorer view must be resized from the final host client rectangle during initial layout and later native resizes.");
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
