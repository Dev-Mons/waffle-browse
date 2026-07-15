using System.IO;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.App.Search.Indexing;

public sealed class WindowsFileIndexSource : IFileIndexSource
{
    private readonly IWindowsVolumeProbe volumeProbe;
    private readonly IFileIndexSource ntfsSource;
    private readonly IFileIndexSource recursiveSource;

    public WindowsFileIndexSource()
        : this(new WindowsVolumeProbe(), new NtfsMftIndexSource(), new RecursiveFileIndexSource())
    {
    }

    internal WindowsFileIndexSource(
        IWindowsVolumeProbe volumeProbe,
        IFileIndexSource ntfsSource,
        IFileIndexSource recursiveSource)
    {
        this.volumeProbe = volumeProbe ?? throw new ArgumentNullException(nameof(volumeProbe));
        this.ntfsSource = ntfsSource ?? throw new ArgumentNullException(nameof(ntfsSource));
        this.recursiveSource = recursiveSource ?? throw new ArgumentNullException(nameof(recursiveSource));
    }

    public async Task<FileIndexBuildResult> BuildAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var entries = new List<FileIndexEntry>();
        var checkpoints = new List<FileIndexCheckpoint>();
        var warnings = new List<string>();
        long skippedPathCount = 0;

        foreach (var root in roots
                     .Where(root => !string.IsNullOrWhiteSpace(root))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileIndexBuildResult result;
            if (volumeProbe.ShouldUseNtfsMft(root))
            {
                try
                {
                    result = await ntfsSource.BuildAsync([root], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is NtfsMftUnavailableException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
                {
                    result = await recursiveSource.BuildAsync([root], cancellationToken).ConfigureAwait(false);
                    EnsureRootCompleted(root, result, ex);
                    AddWarning(
                        warnings,
                        $"{root}: NTFS MFT를 사용할 수 없어 재귀 인덱싱으로 전환했습니다 ({ex.Message}).");
                }
            }
            else
            {
                result = await recursiveSource.BuildAsync([root], cancellationToken).ConfigureAwait(false);
                EnsureRootCompleted(root, result, nativeFailure: null);
            }

            entries.AddRange(result.Entries);
            checkpoints.AddRange(result.Checkpoints);
            skippedPathCount += result.SkippedPathCount;
            foreach (var warning in result.Warnings)
            {
                AddWarning(warnings, warning);
            }
        }

        return new FileIndexBuildResult(entries, checkpoints, warnings, skippedPathCount);
    }

    private static void EnsureRootCompleted(
        string root,
        FileIndexBuildResult result,
        Exception? nativeFailure)
    {
        var normalizedRoot = TryNormalize(root);
        if (result.Checkpoints.Any(checkpoint =>
                string.Equals(TryNormalize(checkpoint.RootPath), normalizedRoot, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var reason = nativeFailure is null
            ? "재귀 인덱싱이 루트의 완성된 체크포인트를 만들지 못했습니다."
            : $"MFT와 재귀 인덱싱이 모두 루트를 완성하지 못했습니다. MFT 오류: {nativeFailure.Message}";
        throw new IOException($"{root}: {reason}", nativeFailure);
    }

    private static string TryNormalize(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 100)
        {
            warnings.Add(warning);
        }
    }
}

internal interface IWindowsVolumeProbe
{
    bool ShouldUseNtfsMft(string path);
}

internal sealed class WindowsVolumeProbe : IWindowsVolumeProbe
{
    public bool ShouldUseNtfsMft(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady
                && drive.DriveType == DriveType.Fixed
                && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            return false;
        }
    }
}
