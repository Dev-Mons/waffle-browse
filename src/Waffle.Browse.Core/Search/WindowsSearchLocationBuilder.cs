namespace Waffle.Browse.Core.Search;

public static class WindowsSearchLocationBuilder
{
    public static string BuildSearchUri(string queryText, IReadOnlyList<string> roots)
    {
        var query = queryText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query is required.", nameof(queryText));
        }

        var normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedRoots.Count == 0)
        {
            throw new ArgumentException("At least one search root is required.", nameof(roots));
        }

        var parts = new List<string>
        {
            $"search-ms:query={Uri.EscapeDataString(query)}"
        };
        parts.AddRange(normalizedRoots.Select(root =>
            $"crumb=location:{Uri.EscapeDataString(root)},include,recursive"));

        return string.Join("&", parts);
    }
}
