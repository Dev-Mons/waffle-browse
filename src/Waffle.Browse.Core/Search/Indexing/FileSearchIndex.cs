using System.IO.Enumeration;

namespace Waffle.Browse.Core.Search.Indexing;

public sealed class FileSearchIndex
{
    internal sealed class PreparedReplacement
    {
        internal PreparedReplacement(Dictionary<string, FileIndexEntry> entries)
        {
            Entries = entries;
        }

        internal Dictionary<string, FileIndexEntry> Entries { get; }
    }

    private readonly ReaderWriterLockSlim gate = new();
    private Dictionary<string, FileIndexEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            gate.EnterReadLock();
            try
            {
                return entries.Count;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }
    }

    public void Replace(IEnumerable<FileIndexEntry> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var next = BuildEntries(replacement, CancellationToken.None);

        ReplaceCore(next);
    }

    public void ReplaceAndApply(
        IEnumerable<FileIndexEntry> replacement,
        IEnumerable<FileIndexChange> changes)
    {
        var prepared = PrepareReplacement(replacement, CancellationToken.None);
        ReplaceAndApply(prepared, changes);
    }

    internal PreparedReplacement PrepareReplacement(
        IEnumerable<FileIndexEntry> replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return new PreparedReplacement(BuildEntries(replacement, cancellationToken));
    }

    internal void ReplaceAndApply(PreparedReplacement replacement, IEnumerable<FileIndexChange> changes)
        => ReplaceAndApply(replacement, changes, CancellationToken.None);

    internal void ReplaceAndApply(
        PreparedReplacement replacement,
        IEnumerable<FileIndexChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(changes);
        ApplyChangesCore(replacement.Entries, changes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceCore(replacement.Entries);
    }

    private void ReplaceCore(Dictionary<string, FileIndexEntry> replacement)
    {
        gate.EnterWriteLock();
        try
        {
            entries = replacement;
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private static Dictionary<string, FileIndexEntry> BuildEntries(
        IEnumerable<FileIndexEntry> replacement,
        CancellationToken cancellationToken)
    {
        var next = new Dictionary<string, FileIndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in replacement)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(item.FullPath))
            {
                next[item.FullPath] = item;
            }
        }

        return next;
    }

    public void Apply(IEnumerable<FileIndexChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        gate.EnterWriteLock();
        try
        {
            ApplyChangesCore(entries, changes, CancellationToken.None);
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    public SearchResponse Search(SearchQuery query, SearchProviderStatus status, string providerId)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaxResults is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxResults must be between 1 and 1000.");
        }

        var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return new SearchResponse([], 0, status, providerId);
        }

        List<FileIndexEntry> matches;
        gate.EnterReadLock();
        try
        {
            matches = entries.Values
                .Where(item => IsInScope(item.FullPath, query))
                .Where(item => terms.All(term => Matches(item, term)))
                .ToList();
        }
        finally
        {
            gate.ExitReadLock();
        }

        var total = matches.Count;
        var results = Sort(matches, query.Sort)
            .Take(query.MaxResults)
            .Select(item => item.ToSearchResult())
            .ToList();
        return new SearchResponse(results, total, status, providerId);
    }

    public IReadOnlyList<FileIndexEntry> Snapshot()
    {
        gate.EnterReadLock();
        try
        {
            return entries.Values.ToList();
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    private static void ApplyChangesCore(
        Dictionary<string, FileIndexEntry> target,
        IEnumerable<FileIndexChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (change.Kind)
            {
                case FileIndexChangeKind.Upsert when change.Entry is not null:
                    var entry = change.Entry;
                    if (change.PreserveIdentity
                        && target.TryGetValue(entry.FullPath, out var existing))
                    {
                        entry = entry with
                        {
                            VolumeId = entry.VolumeId ?? existing.VolumeId,
                            FileReferenceNumber = entry.FileReferenceNumber ?? existing.FileReferenceNumber
                        };
                    }

                    target[entry.FullPath] = entry;
                    break;
                case FileIndexChangeKind.Delete:
                    RemovePathCore(target, change.Path);
                    break;
                case FileIndexChangeKind.Rename when !string.IsNullOrWhiteSpace(change.NewPath):
                    RenamePathCore(target, change.Path, change.NewPath);
                    break;
            }
        }
    }

    private static void RemovePathCore(Dictionary<string, FileIndexEntry> target, string path)
    {
        var normalized = NormalizePath(path);
        var prefix = DescendantPrefix(normalized);
        foreach (var key in target.Keys
                     .Where(key => PathEquals(key, normalized) || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            target.Remove(key);
        }
    }

    private static void RenamePathCore(
        Dictionary<string, FileIndexEntry> target,
        string oldPath,
        string newPath)
    {
        var oldNormalized = NormalizePath(oldPath);
        var newNormalized = NormalizePath(newPath);
        var prefix = DescendantPrefix(oldNormalized);
        var affected = target.Values
            .Where(item => PathEquals(item.FullPath, oldNormalized)
                || item.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var item in affected)
        {
            target.Remove(item.FullPath);
            var suffix = item.FullPath.Length == oldNormalized.Length
                ? string.Empty
                : item.FullPath[oldNormalized.Length..];
            var movedPath = newNormalized + suffix;
            target[movedPath] = item with
            {
                FullPath = movedPath,
                Name = Path.GetFileName(movedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                ParentPath = Path.GetDirectoryName(movedPath) ?? string.Empty
            };
        }
    }

    private static bool IsInScope(string fullPath, SearchQuery query)
    {
        if (query.Scope != SearchScope.CurrentFolder)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(query.RootPath))
        {
            return false;
        }

        var root = NormalizePath(query.RootPath);
        return fullPath.StartsWith(DescendantPrefix(root), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(FileIndexEntry item, string term)
    {
        if (term.IndexOfAny(['*', '?']) >= 0)
        {
            return FileSystemName.MatchesSimpleExpression(term, item.Name, ignoreCase: true)
                || FileSystemName.MatchesSimpleExpression($"*{term}*", item.FullPath, ignoreCase: true);
        }

        return item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static IOrderedEnumerable<FileIndexEntry> Sort(IEnumerable<FileIndexEntry> source, SearchSort sort)
    {
        var foldersFirst = source.OrderBy(item => item.Kind == SearchItemKind.Folder ? 0 : 1);
        return sort switch
        {
            SearchSort.NameDescending => foldersFirst.ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase),
            SearchSort.PathAscending => foldersFirst.ThenBy(item => item.ParentPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            SearchSort.PathDescending => foldersFirst.ThenByDescending(item => item.ParentPath, StringComparer.OrdinalIgnoreCase).ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase),
            SearchSort.ModifiedAscending => foldersFirst.ThenBy(item => item.ModifiedAt).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            SearchSort.ModifiedDescending => foldersFirst.ThenByDescending(item => item.ModifiedAt).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            SearchSort.SizeAscending => foldersFirst.ThenBy(item => item.Size).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            SearchSort.SizeDescending => foldersFirst.ThenByDescending(item => item.Size).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => foldersFirst.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string DescendantPrefix(string normalizedPath) =>
        normalizedPath.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedPath
            : normalizedPath + Path.DirectorySeparatorChar;

    private static bool PathEquals(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
}
