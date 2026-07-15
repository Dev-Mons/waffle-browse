using System.IO;
using System.Reflection;
using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class ShellSearchTargetResolverTests
{
    public static void ResolveReturnsSearchUriWhenShellCanParseTarget()
    {
        var root = Directory.GetCurrentDirectory();
        var resolver = CreateResolver(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => true);

        var resolved = resolver.Resolve("report", [root]);

        AssertStartsWith("search-ms:", resolved);
    }

    public static void ResolveDoesNotCreateSavedSearchFallback()
    {
        var root = Directory.GetCurrentDirectory();
        var searchesDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var resolver = CreateResolver(searchesDirectory, _ => false);

        var resolved = resolver.Resolve("report", [root]);

        AssertStartsWith("search-ms:", resolved);
        if (Directory.Exists(searchesDirectory)
            && Directory.EnumerateFiles(searchesDirectory, "*.search-ms").Any())
        {
            throw new InvalidOperationException("Resolver created a .search-ms fallback file.");
        }
    }

    private static ShellSearchTargetResolver CreateResolver(string searchesDirectory, Func<string, bool> canParseShellTarget)
    {
        var constructor = typeof(ShellSearchTargetResolver).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(Func<string, bool>)],
            modifiers: null);

        if (constructor is null)
        {
            throw new InvalidOperationException("Could not find test constructor.");
        }

        return (ShellSearchTargetResolver)constructor.Invoke([searchesDirectory, canParseShellTarget]);
    }

    private static void AssertStartsWith(string expectedPrefix, string actual)
    {
        if (!actual.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected '{actual}' to start with '{expectedPrefix}'.");
        }
    }

}
