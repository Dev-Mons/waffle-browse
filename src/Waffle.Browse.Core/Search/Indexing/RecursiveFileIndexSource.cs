namespace Waffle.Browse.Core.Search.Indexing;

public sealed class RecursiveFileIndexSource : IFileIndexSource
{
    public Task<FileIndexBuildResult> BuildAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Task.Run(() => Build(roots, cancellationToken), cancellationToken);
    }

    public static FileIndexEntry? TryReadEntry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var normalized = Path.GetFullPath(path);
            var name = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (isDirectory)
            {
                var directory = new DirectoryInfo(normalized);
                return new FileIndexEntry(
                    normalized,
                    name,
                    directory.Parent?.FullName ?? string.Empty,
                    SearchItemKind.Folder,
                    null,
                    directory.LastWriteTimeUtc);
            }

            var file = new FileInfo(normalized);
            return new FileIndexEntry(
                normalized,
                name,
                file.DirectoryName ?? string.Empty,
                SearchItemKind.File,
                file.Length,
                file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileIndexBuildResult Build(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var entries = new List<FileIndexEntry>();
        var checkpoints = new List<FileIndexCheckpoint>();
        var warnings = new List<string>();
        long skippedPathCount = 0;

        void AddWarning(string warning)
        {
            skippedPathCount++;
            if (warnings.Count < 100)
            {
                warnings.Add(warning);
            }
        }

        foreach (var configuredRoot in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root;
            try
            {
                root = Path.GetFullPath(configuredRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                AddWarning($"{configuredRoot}: {ex.Message}");
                continue;
            }

            if (!Directory.Exists(root))
            {
                AddWarning($"{root}: 볼륨 또는 폴더를 사용할 수 없습니다.");
                continue;
            }

            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = stack.Pop();
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(directory).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                    AddWarning($"{directory}: {ex.Message}");
                    continue;
                }

                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = TryReadEntry(child);
                    if (entry is null)
                    {
                        continue;
                    }

                    entries.Add(entry);
                    if (entry.Kind == SearchItemKind.Folder && !IsReparsePoint(entry.FullPath))
                    {
                        stack.Push(entry.FullPath);
                    }
                }
            }

            var (volumeId, fileSystem) = TryGetVolumeIdentity(root);
            checkpoints.Add(new FileIndexCheckpoint(
                root,
                volumeId,
                fileSystem,
                JournalId: null,
                NextUsn: null,
                DateTimeOffset.UtcNow));
        }

        return new FileIndexBuildResult(entries, checkpoints, warnings, skippedPathCount);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static (string? VolumeId, string? FileSystem) TryGetVolumeIdentity(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return (null, null);
            }

            var drive = new DriveInfo(root);
            return (drive.Name, drive.IsReady ? drive.DriveFormat : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }
}
