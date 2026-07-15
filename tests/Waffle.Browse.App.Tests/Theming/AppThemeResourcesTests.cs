using System.IO;
using System.Windows;
using System.Windows.Media;
using Waffle.Browse.App.Settings;
using Waffle.Browse.App.Theming;

namespace Waffle.Browse.App.Tests.Theming;

internal static class AppThemeResourcesTests
{
    public static void ApplyDarkThemeUpdatesCoreBrushes()
    {
        var resources = new ResourceDictionary();

        AppThemeResources.Apply(resources, UiTheme.Dark);

        AssertBrushColor(resources, AppThemeResources.WindowBackgroundBrushKey, "#FF101820");
        AssertBrushColor(resources, AppThemeResources.PanelBackgroundBrushKey, "#FF151F2A");
        AssertBrushColor(resources, AppThemeResources.ToolbarBackgroundBrushKey, "#FF1E2A36");
        AssertBrushColor(resources, AppThemeResources.PrimaryTextBrushKey, "#FFEEF3F8");
        AssertBrushColor(resources, AppThemeResources.ControlBackgroundBrushKey, "#FF243241");
    }

    public static void ApplyLightThemeUsesLightShellHostBackground()
    {
        var resources = new ResourceDictionary();

        AppThemeResources.Apply(resources, UiTheme.Light);

        AssertBrushColor(resources, AppThemeResources.ShellHostBackgroundBrushKey, "#FFFFFFFF");
    }

    public static void AppDefaultResourcesUseLightShellHostBackground()
    {
        var appXaml = File.ReadAllText(FindRepositoryFile(@"src\Waffle.Browse.App\App.xaml"));

        if (!appXaml.Contains("""<SolidColorBrush x:Key="ShellHostBackgroundBrush" Color="#FFFFFF" />""", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Default shell host background resource should match light theme.");
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

    private static void AssertBrushColor(ResourceDictionary resources, string key, string expected)
    {
        if (resources[key] is not SolidColorBrush brush)
        {
            throw new InvalidOperationException($"Resource '{key}' was not a SolidColorBrush.");
        }

        var actual = brush.Color.ToString();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected '{key}' to be {expected}, got {actual}.");
        }
    }
}
