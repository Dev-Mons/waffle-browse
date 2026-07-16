using System.IO;

namespace Waffle.Browse.App.Tests.Controls;

internal static class ExplorerPanelControlVisualTests
{
    public static void ExplorerPanelUsesSoftGridChrome()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\Controls\ExplorerPanelControl.xaml"));
        var code = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\Controls\ExplorerPanelControl.xaml.cs"));

        var requiredXaml = new[]
        {
            "Margin=\"2\"",
            "CornerRadius=\"0\"",
            "BorderThickness=\"2\"",
            "Height=\"32\"",
            "Padding=\"9,2\"",
            "ScrollViewer.VerticalScrollBarVisibility=\"Disabled\"",
            "x:Name=\"AddTabButton\"",
            "Click=\"OnCloseTabButtonClick\"",
            "Background=\"{DynamicResource PanelHeaderBackgroundBrush}\""
        };

        if (requiredXaml.Any(fragment => !xaml.Contains(fragment, StringComparison.Ordinal))
            || !code.Contains("OnAddTabClick", StringComparison.Ordinal)
            || !code.Contains("OnCloseTabButtonClick", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Explorer panels should expose compact, uninterrupted soft-grid chrome and explicit tab actions.");
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
