using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Shell;

public sealed class ShellSearchTargetResolver
{
    public ShellSearchTargetResolver()
    {
    }

    internal ShellSearchTargetResolver(string searchesDirectory, Func<string, bool> canParseShellTarget)
    {
        _ = searchesDirectory;
        _ = canParseShellTarget;
    }

    public string Resolve(string queryText, IReadOnlyList<string> roots)
    {
        return WindowsSearchLocationBuilder.BuildSearchUri(queryText, roots);
    }
}
