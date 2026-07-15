namespace Waffle.Browse.Core.Search;

public static class WaffleSearchLocation
{
    private const string Prefix = "waffle-search:?";

    public static string Build(string query, SearchScope scope, string? rootPath)
    {
        var text = query.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Search query is required.", nameof(query));
        }

        if (scope == SearchScope.CurrentFolder && string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A root path is required for a current-folder search.", nameof(rootPath));
        }

        return Prefix
            + $"query={Uri.EscapeDataString(text)}"
            + $"&scope={Uri.EscapeDataString(scope.ToString())}"
            + (string.IsNullOrWhiteSpace(rootPath) ? string.Empty : $"&root={Uri.EscapeDataString(rootPath)}");
    }

    public static bool TryParse(string value, out SearchQuery query)
    {
        query = new SearchQuery(string.Empty, SearchScope.GlobalIndex, 1000);
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = value[Prefix.Length..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part[1]),
                StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("query", out var text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var scope = values.TryGetValue("scope", out var scopeText)
            && Enum.TryParse<SearchScope>(scopeText, true, out var parsedScope)
                ? parsedScope
                : SearchScope.GlobalIndex;
        values.TryGetValue("root", out var rootPath);
        if (scope == SearchScope.CurrentFolder && string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        query = new SearchQuery(text, scope, 1000, rootPath);
        return true;
    }
}
