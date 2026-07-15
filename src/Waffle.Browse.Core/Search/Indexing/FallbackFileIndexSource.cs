using System.Runtime.ExceptionServices;

namespace Waffle.Browse.Core.Search.Indexing;

public sealed class FallbackFileIndexSource : IFileIndexSnapshotSource
{
    private const int MaximumWarnings = 100;

    private readonly IFileIndexSource primary;
    private readonly IFileIndexSource fallback;
    private readonly object cacheGate = new();
    private readonly Dictionary<string, FileIndexBuildResult> lastGoodByRoot =
        new(StringComparer.OrdinalIgnoreCase);

    public FallbackFileIndexSource(IFileIndexSource primary, IFileIndexSource fallback)
    {
        this.primary = primary ?? throw new ArgumentNullException(nameof(primary));
        this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async Task<FileIndexBuildResult> BuildAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return await RunPerRootAsync(roots, baseline: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileIndexBuildResult> RefreshAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(baseline);
        return await RunPerRootAsync(roots, baseline, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileIndexBuildResult> RunPerRootAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot? baseline,
        CancellationToken cancellationToken)
    {
        var entries = new List<FileIndexEntry>();
        var checkpoints = new List<FileIndexCheckpoint>();
        var warnings = new List<string>();
        long skippedPathCount = 0;
        ExceptionDispatchInfo? firstUnavailableRootFailure = null;
        var completedRootCount = 0;

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootBaseline = baseline is null ? null : SliceBaseline(root, baseline);
            if (rootBaseline is not null)
            {
                SeedBaseline(root, rootBaseline);
            }

            FileIndexBuildResult result;
            var rootCompleted = true;
            try
            {
                result = await RunSourceAsync(primary, root, rootBaseline, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFallbackEligible(ex))
            {
                AddWarning(
                    warnings,
                    $"{root}: 기본 인덱스 소스를 사용할 수 없어 폴백 소스로 전환했습니다. ({ex.Message})");
                try
                {
                    result = await RunSourceAsync(fallback, root, rootBaseline, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fallbackException) when (IsRetentionEligible(fallbackException))
                {
                    if (!TryGetLastGood(root, out result))
                    {
                        firstUnavailableRootFailure ??= ExceptionDispatchInfo.Capture(fallbackException);
                        rootCompleted = false;
                        result = EmptyResult();
                        AddWarning(
                            warnings,
                            $"{root}: 폴백 소스도 사용할 수 없어 이 루트를 현재 세대에서 제외합니다. ({fallbackException.Message})");
                    }
                    else
                    {
                        AddWarning(
                            warnings,
                            $"{root}: 폴백 소스도 사용할 수 없어 마지막 정상 세대를 유지합니다. ({fallbackException.Message})");
                    }
                }
            }
            catch (Exception ex) when (IsRetentionEligible(ex))
            {
                if (!TryGetLastGood(root, out result))
                {
                    firstUnavailableRootFailure ??= ExceptionDispatchInfo.Capture(ex);
                    rootCompleted = false;
                    result = EmptyResult();
                    AddWarning(
                        warnings,
                        $"{root}: 볼륨을 갱신하지 못해 이 루트를 현재 세대에서 제외합니다. ({ex.Message})");
                }
                else
                {
                    AddWarning(
                        warnings,
                        $"{root}: 볼륨을 갱신하지 못해 마지막 정상 세대를 유지합니다. ({ex.Message})");
                }
            }

            if (result.Checkpoints.Count == 0
                && result.Warnings.Count > 0)
            {
                if (TryGetLastGood(root, out var retained))
                {
                    AddWarning(
                        warnings,
                        $"{root}: 볼륨을 사용할 수 없어 마지막 정상 세대를 유지합니다.");
                    result = retained;
                }
                else
                {
                    foreach (var warning in result.Warnings)
                    {
                        AddWarning(warnings, ScopeWarning(root, warning));
                    }

                    var detail = result.Warnings[0];
                    firstUnavailableRootFailure ??= ExceptionDispatchInfo.Capture(
                        new IOException($"{root}: 완전한 인덱스 세대를 만들지 못했습니다. {detail}"));
                    rootCompleted = false;
                    result = EmptyResult();
                }
            }
            else if (result.Checkpoints.Count > 0)
            {
                lock (cacheGate)
                {
                    lastGoodByRoot[NormalizeRoot(root)] = result;
                }
            }

            entries.AddRange(result.Entries);
            checkpoints.AddRange(result.Checkpoints);
            skippedPathCount += result.SkippedPathCount;
            foreach (var warning in result.Warnings)
            {
                AddWarning(warnings, ScopeWarning(root, warning));
            }

            if (result.SkippedPathCount > 0)
            {
                AddWarning(
                    warnings,
                    $"{root}: 일부 경로를 건너뜀: {result.SkippedPathCount:N0}개");
            }

            if (rootCompleted)
            {
                completedRootCount++;
            }
        }

        if (completedRootCount == 0 && firstUnavailableRootFailure is not null)
        {
            firstUnavailableRootFailure.Throw();
        }

        return new FileIndexBuildResult(entries, checkpoints, warnings, skippedPathCount);
    }

    private static FileIndexSnapshot SliceBaseline(string root, FileIndexSnapshot baseline)
    {
        var normalizedRoot = NormalizeRoot(root);
        var checkpoints = baseline.State.Checkpoints
            .Where(checkpoint => string.Equals(
                NormalizeRoot(checkpoint.RootPath),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var entries = baseline.Entries
            .Where(entry => IsWithinRoot(entry.FullPath, normalizedRoot))
            .ToList();
        return new FileIndexSnapshot(
            baseline.FormatVersion,
            baseline.State with
            {
                ItemCount = entries.Count,
                Checkpoints = checkpoints
            },
            entries);
    }

    private static Task<FileIndexBuildResult> RunSourceAsync(
        IFileIndexSource source,
        string root,
        FileIndexSnapshot? baseline,
        CancellationToken cancellationToken) =>
        baseline is not null && source is IFileIndexSnapshotSource snapshotSource
            ? snapshotSource.RefreshAsync([root], baseline, cancellationToken)
            : source.BuildAsync([root], cancellationToken);

    private void SeedBaseline(string root, FileIndexSnapshot baseline)
    {
        var normalizedRoot = NormalizeRoot(root);
        var checkpoints = baseline.State.Checkpoints
            .Where(checkpoint => string.Equals(
                NormalizeRoot(checkpoint.RootPath),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (checkpoints.Count == 0)
        {
            return;
        }

        var baselineEntries = baseline.Entries
            .Where(entry => IsWithinRoot(entry.FullPath, normalizedRoot))
            .ToList();
        lock (cacheGate)
        {
            lastGoodByRoot[normalizedRoot] = new FileIndexBuildResult(
                baselineEntries,
                checkpoints,
                []);
        }
    }

    private bool TryGetLastGood(string root, out FileIndexBuildResult result)
    {
        lock (cacheGate)
        {
            return lastGoodByRoot.TryGetValue(NormalizeRoot(root), out result!);
        }
    }

    private static string NormalizeRoot(string root) =>
        Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsWithinRoot(string path, string normalizedRoot)
    {
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFallbackEligible(Exception exception) =>
        exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException;

    private static bool IsRetentionEligible(Exception exception) =>
        IsFallbackEligible(exception)
            || exception is IOException
            or InvalidDataException;

    private static FileIndexBuildResult EmptyResult() => new([], [], []);

    private static string ScopeWarning(string root, string warning) =>
        warning.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase)
            ? warning
            : $"{root}: {warning}";

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaximumWarnings)
        {
            warnings.Add(warning);
        }
    }
}
