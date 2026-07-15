namespace Waffle.Browse.Core.Search;

public sealed class FileSearchService
{
    public List<SearchResultItem> Search(
        IReadOnlyList<string> roots,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Text) || query.MaxResults <= 0)
        {
            return [];
        }

        var results = new List<SearchResultItem>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = query.Text.Trim();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!seenRoots.Add(fullRoot) || !Directory.Exists(fullRoot))
            {
                continue;
            }

            SearchDirectory(fullRoot, text, query.MaxResults, results, seenResults, cancellationToken);
            if (results.Count >= query.MaxResults)
            {
                break;
            }
        }

        return results;
    }

    private static void SearchDirectory(
        string directory,
        string text,
        int maxResults,
        List<SearchResultItem> results,
        HashSet<string> seenResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (results.Count >= maxResults)
        {
            return;
        }

        var childDirectories = SafeEnumerateDirectories(directory);
        foreach (var childDirectory in childDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddDirectoryIfMatch(childDirectory, text, maxResults, results, seenResults);
            if (results.Count >= maxResults)
            {
                return;
            }
        }

        foreach (var childDirectory in childDirectories)
        {
            SearchDirectory(childDirectory, text, maxResults, results, seenResults, cancellationToken);
            if (results.Count >= maxResults)
            {
                return;
            }
        }

        foreach (var file in SafeEnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddFileIfMatch(file, text, maxResults, results, seenResults);
            if (results.Count >= maxResults)
            {
                return;
            }
        }
    }

    private static void AddDirectoryIfMatch(
        string directory,
        string text,
        int maxResults,
        List<SearchResultItem> results,
        HashSet<string> seenResults)
    {
        if (results.Count >= maxResults)
        {
            return;
        }

        var info = new DirectoryInfo(directory);
        if (!Contains(info.Name, text) || !seenResults.Add(info.FullName))
        {
            return;
        }

        results.Add(new SearchResultItem(
            info.Name,
            info.FullName,
            info.Parent?.FullName ?? string.Empty,
            SearchItemKind.Folder,
            null,
            SafeGetModifiedAt(info)));
    }

    private static void AddFileIfMatch(
        string file,
        string text,
        int maxResults,
        List<SearchResultItem> results,
        HashSet<string> seenResults)
    {
        if (results.Count >= maxResults)
        {
            return;
        }

        var info = new FileInfo(file);
        if (!Contains(info.Name, text) || !seenResults.Add(info.FullName))
        {
            return;
        }

        results.Add(new SearchResultItem(
            info.Name,
            info.FullName,
            info.DirectoryName ?? string.Empty,
            SearchItemKind.File,
            SafeGetLength(info),
            SafeGetModifiedAt(info)));
    }

    private static List<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static List<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static long? SafeGetLength(FileInfo info)
    {
        try
        {
            return info.Length;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeGetModifiedAt(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
        {
            return null;
        }
    }

    private static bool Contains(string value, string text)
    {
        return value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
