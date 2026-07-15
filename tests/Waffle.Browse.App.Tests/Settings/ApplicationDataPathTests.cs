using System.IO;
using Waffle.Browse.App.Settings;

namespace Waffle.Browse.App.Tests.Settings;

internal static class ApplicationDataPathTests
{
    public static void ResolveUsesCurrentDirectoryForNewInstall()
    {
        WithTemporaryRoot(root =>
        {
            var expected = Path.Combine(root, "Waffle Browse");
            var actual = ApplicationDataPath.Resolve(root);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
            }
        });
    }

    public static void ResolveMigratesLegacyDirectory()
    {
        WithTemporaryRoot(root =>
        {
            var legacyPath = Path.Combine(root, "F-Finder");
            var currentPath = Path.Combine(root, "Waffle Browse");
            Directory.CreateDirectory(legacyPath);
            File.WriteAllText(Path.Combine(legacyPath, "settings.json"), "{}");

            var actual = ApplicationDataPath.Resolve(root);

            if (!string.Equals(actual, currentPath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(currentPath, "settings.json"))
                || Directory.Exists(legacyPath))
            {
                throw new InvalidOperationException("Legacy application data was not moved to the current directory.");
            }
        });
    }

    public static void ResolvePrefersExistingCurrentDirectory()
    {
        WithTemporaryRoot(root =>
        {
            var legacyPath = Path.Combine(root, "F-Finder");
            var currentPath = Path.Combine(root, "Waffle Browse");
            Directory.CreateDirectory(legacyPath);
            Directory.CreateDirectory(currentPath);

            var actual = ApplicationDataPath.Resolve(root);

            if (!string.Equals(actual, currentPath, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(legacyPath))
            {
                throw new InvalidOperationException("An existing current application data directory should take precedence.");
            }
        });
    }

    private static void WithTemporaryRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            test(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
