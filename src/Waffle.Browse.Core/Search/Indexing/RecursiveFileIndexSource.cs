using System.IO.Enumeration;

namespace Waffle.Browse.Core.Search.Indexing;

public sealed class RecursiveFileIndexSource : IFileIndexSource, IFileIndexProgressSource
{
    private const int ProgressReportInterval = 250;
    private const int ProgressReportIntervalMilliseconds = 100;

    public event EventHandler<FileIndexProgressEventArgs>? ProgressChanged;

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

    private FileIndexBuildResult Build(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var entries = new List<FileIndexEntry>(4096);
        var checkpoints = new List<FileIndexCheckpoint>();
        var warnings = new List<string>();
        var configuredRoots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        long skippedPathCount = 0;
        var completedRootCount = 0;
        var lastReportedItemCount = 0;
        var lastProgressReportAt = Environment.TickCount64;

        void AddWarning(string warning)
        {
            skippedPathCount++;
            if (warnings.Count < 100)
            {
                warnings.Add(warning);
            }
        }

        ReportProgress(null);
        foreach (var configuredRoot in configuredRoots)
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
                completedRootCount++;
                ReportProgress(null);
                continue;
            }

            ReportProgress(root);
            if (!Directory.Exists(root))
            {
                AddWarning($"{root}: 볼륨 또는 폴더를 사용할 수 없습니다.");
                completedRootCount++;
                ReportProgress(null);
                continue;
            }

            try
            {
                var enumeration = new FileSystemEnumerable<FileIndexEntry>(
                    root,
                    static (ref FileSystemEntry entry) =>
                    {
                        var fullPath = entry.ToFullPath();
                        var isDirectory = entry.IsDirectory;
                        return new FileIndexEntry(
                            fullPath,
                            entry.FileName.ToString(),
                            Path.GetDirectoryName(fullPath) ?? string.Empty,
                            isDirectory ? SearchItemKind.Folder : SearchItemKind.File,
                            isDirectory ? null : entry.Length,
                            entry.LastWriteTimeUtc);
                    },
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0
                    });
                enumeration.ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                    !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

                foreach (var entry in enumeration)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(entry);
                    if (entries.Count - lastReportedItemCount >= ProgressReportInterval
                        && Environment.TickCount64 - lastProgressReportAt >= ProgressReportIntervalMilliseconds)
                    {
                        lastReportedItemCount = entries.Count;
                        lastProgressReportAt = Environment.TickCount64;
                        ReportProgress(root);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or DirectoryNotFoundException)
            {
                AddWarning($"{root}: {ex.Message}");
                completedRootCount++;
                ReportProgress(null);
                continue;
            }

            var (volumeId, fileSystem) = TryGetVolumeIdentity(root);
            checkpoints.Add(new FileIndexCheckpoint(
                root,
                volumeId,
                fileSystem,
                JournalId: null,
                NextUsn: null,
                DateTimeOffset.UtcNow));
            completedRootCount++;
            ReportProgress(null);
        }

        return new FileIndexBuildResult(entries, checkpoints, warnings, skippedPathCount);

        void ReportProgress(string? currentRoot)
        {
            ProgressChanged?.Invoke(
                this,
                new FileIndexProgressEventArgs(
                    completedRootCount,
                    configuredRoots.Count,
                    currentRoot,
                    entries.Count,
                    skippedPathCount));
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
