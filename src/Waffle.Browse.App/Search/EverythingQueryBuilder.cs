using System.IO;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Search;

public static class EverythingQueryBuilder
{
    public static string Build(SearchQuery query)
    {
        var text = query.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Search query is required.", nameof(query));
        }

        if (query.Scope != SearchScope.CurrentFolder)
        {
            return text;
        }

        if (string.IsNullOrWhiteSpace(query.RootPath))
        {
            throw new ArgumentException("A root path is required for a current-folder search.", nameof(query));
        }

        var root = query.RootPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"\"{root}\\\" {text}";
    }
}
