using System.IO;
using Waffle.Browse.App.Search.Indexing;
using Waffle.Browse.Core.Search;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.App.Tests.Search;

internal static class WindowsFileIndexSourceTests
{
    public static void SelectsNtfsSourceForEligibleRoot()
    {
        var root = TestRoot("ntfs-selected");
        var probe = new FakeVolumeProbe(_ => true);
        var ntfs = new RecordingFileIndexSource((requestedRoot, _) =>
            Result(requestedRoot, "mft.txt", ["mft warning"], skippedPathCount: 1));
        var recursive = NeverSource("Recursive source should not run for an eligible NTFS root.");
        var source = new WindowsFileIndexSource(probe, ntfs, recursive);

        var result = Build(source, [root]);

        Equal(1, probe.Roots.Count, "The root should be probed once.");
        Equal(root, Single(ntfs.Roots), "The eligible root should be sent to the NTFS source.");
        Equal(0, recursive.Roots.Count, "The recursive source should not run after an NTFS success.");
        Equal("mft.txt", Single(result.Entries).Name, "The NTFS result should be published.");
        Equal(1L, result.SkippedPathCount, "The NTFS skipped count should be retained.");
    }

    public static void SelectsRecursiveSourceForNonNtfsRoot()
    {
        var root = TestRoot("recursive-selected");
        var probe = new FakeVolumeProbe(_ => false);
        var ntfs = NeverSource("NTFS source should not run for a non-NTFS root.");
        var recursive = new RecordingFileIndexSource((requestedRoot, _) =>
            Result(requestedRoot, "recursive.txt", ["recursive warning"]));
        var source = new WindowsFileIndexSource(probe, ntfs, recursive);

        var result = Build(source, [root]);

        Equal(0, ntfs.Roots.Count, "The NTFS source should not run for a non-NTFS root.");
        Equal(root, Single(recursive.Roots), "The non-NTFS root should be sent to the recursive source.");
        Equal("recursive.txt", Single(result.Entries).Name, "The recursive result should be published.");
        Equal(root, Single(result.Checkpoints).RootPath, "The recursive checkpoint should be retained.");
    }

    public static void FallsBackAfterNtfsUnavailableAndReportsWarning()
    {
        var root = TestRoot("fallback");
        var probe = new FakeVolumeProbe(_ => true);
        var ntfs = new RecordingFileIndexSource((requestedRoot, _) =>
            throw new NtfsMftUnavailableException(requestedRoot, "test access denied"));
        var recursive = new RecordingFileIndexSource((requestedRoot, _) =>
            Result(requestedRoot, "fallback.txt", ["recursive detail"], skippedPathCount: 2));
        var source = new WindowsFileIndexSource(probe, ntfs, recursive);

        var result = Build(source, [root]);

        Equal(1, ntfs.Roots.Count, "The NTFS source should be attempted once.");
        Equal(1, recursive.Roots.Count, "The recursive source should run once after NTFS is unavailable.");
        Equal("fallback.txt", Single(result.Entries).Name, "The fallback result should be published.");
        Equal(2L, result.SkippedPathCount, "The fallback skipped count should be retained.");
        True(
            result.Warnings.Any(warning => warning.Contains("재귀 인덱싱으로 전환", StringComparison.Ordinal)
                                           && warning.Contains("test access denied", StringComparison.Ordinal)),
            "The aggregate should explain the NTFS-to-recursive fallback.");
        True(
            result.Warnings.Contains("recursive detail", StringComparer.Ordinal),
            "Warnings returned by the recursive fallback should be retained.");
    }

    public static void CancellationDoesNotFallBack()
    {
        var root = TestRoot("canceled");
        var ntfs = new RecordingFileIndexSource((_, _) => throw new OperationCanceledException("test cancellation"));
        var recursive = NeverSource("Cancellation must not invoke the recursive fallback.");
        var source = new WindowsFileIndexSource(new FakeVolumeProbe(_ => true), ntfs, recursive);

        Throws<OperationCanceledException>(
            () => source.BuildAsync([root]),
            "NTFS cancellation should propagate.");

        Equal(1, ntfs.Roots.Count, "The NTFS source should be attempted before cancellation.");
        Equal(0, recursive.Roots.Count, "Cancellation should not be converted into recursive fallback.");
    }

    public static void MissingFallbackCheckpointFailsWholeBuild()
    {
        var root = TestRoot("missing-checkpoint");
        var ntfs = new RecordingFileIndexSource((requestedRoot, _) =>
            throw new NtfsMftUnavailableException(requestedRoot, "test unavailable"));
        var recursive = new RecordingFileIndexSource((requestedRoot, _) =>
            Result(requestedRoot, "partial.txt", ["partial result"], includeCheckpoint: false));
        var source = new WindowsFileIndexSource(new FakeVolumeProbe(_ => true), ntfs, recursive);

        var error = Throws<IOException>(
            () => source.BuildAsync([root]),
            "A fallback without a completed root checkpoint should fail the aggregate build.");

        True(error.Message.Contains("모두 루트를 완성하지 못했습니다", StringComparison.Ordinal), "The error should identify incomplete NTFS and recursive sources.");
        Equal(1, ntfs.Roots.Count, "The NTFS source should be attempted once.");
        Equal(1, recursive.Roots.Count, "The recursive fallback should be attempted once.");
    }

    public static void AggregatesMultipleRootResultsWarningsAndCheckpoints()
    {
        var ntfsRoot = TestRoot("aggregate-ntfs");
        var recursiveRoot = TestRoot("aggregate-recursive");
        var fallbackRoot = TestRoot("aggregate-fallback");
        var probe = new FakeVolumeProbe(root => !PathEquals(root, recursiveRoot));
        var ntfs = new RecordingFileIndexSource((root, _) =>
        {
            if (PathEquals(root, fallbackRoot))
            {
                throw new NtfsMftUnavailableException(root, "aggregate unavailable");
            }

            return Result(root, "native.txt", ["native warning"], skippedPathCount: 1);
        });
        var recursive = new RecordingFileIndexSource((root, _) =>
            PathEquals(root, recursiveRoot)
                ? Result(root, "recursive.txt", ["recursive warning"], skippedPathCount: 2)
                : Result(root, "fallback.txt", ["fallback warning"], skippedPathCount: 3));
        var source = new WindowsFileIndexSource(probe, ntfs, recursive);

        var result = Build(source, [ntfsRoot, recursiveRoot, fallbackRoot]);

        Equal(3, probe.Roots.Count, "Every configured root should be probed.");
        Equal(2, ntfs.Roots.Count, "The two eligible roots should reach the NTFS source.");
        Equal(2, recursive.Roots.Count, "The non-NTFS root and failed NTFS root should reach the recursive source.");
        Equal(3, result.Entries.Count, "Entries from every completed root should be aggregated.");
        Equal(3, result.Checkpoints.Count, "Checkpoints from every completed root should be aggregated.");
        Equal(6L, result.SkippedPathCount, "Skipped counts from every root should be summed.");
        Equal(4, result.Warnings.Count, "Source warnings plus the fallback warning should be aggregated.");
        True(result.Entries.Any(entry => entry.Name == "native.txt"), "The NTFS entry should be present.");
        True(result.Entries.Any(entry => entry.Name == "recursive.txt"), "The direct recursive entry should be present.");
        True(result.Entries.Any(entry => entry.Name == "fallback.txt"), "The fallback entry should be present.");
        True(result.Warnings.Contains("native warning", StringComparer.Ordinal), "The NTFS warning should be present.");
        True(result.Warnings.Contains("recursive warning", StringComparer.Ordinal), "The recursive warning should be present.");
        True(result.Warnings.Contains("fallback warning", StringComparer.Ordinal), "The fallback source warning should be present.");
        True(result.Warnings.Any(warning => warning.Contains("aggregate unavailable", StringComparison.Ordinal)), "The transition warning should retain the NTFS failure.");

        var checkpointRoots = result.Checkpoints.Select(checkpoint => checkpoint.RootPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        True(checkpointRoots.SetEquals([ntfsRoot, recursiveRoot, fallbackRoot]), "The aggregate should retain one completed checkpoint for each root.");
    }

    private static FileIndexBuildResult Build(WindowsFileIndexSource source, IReadOnlyList<string> roots) =>
        source.BuildAsync(roots).GetAwaiter().GetResult();

    private static FileIndexBuildResult Result(
        string root,
        string entryName,
        IReadOnlyList<string> warnings,
        long skippedPathCount = 0,
        bool includeCheckpoint = true)
    {
        var entry = new FileIndexEntry(
            Path.Combine(root, entryName),
            entryName,
            root,
            SearchItemKind.File,
            1,
            DateTimeOffset.UnixEpoch);
        IReadOnlyList<FileIndexCheckpoint> checkpoints = includeCheckpoint
            ? [new FileIndexCheckpoint(root, $"volume:{root}", "TEST", null, null, DateTimeOffset.UnixEpoch)]
            : [];
        return new FileIndexBuildResult([entry], checkpoints, warnings, skippedPathCount);
    }

    private static string TestRoot(string name) => Path.Combine(Path.GetTempPath(), "waffle-source-tests", name);

    private static RecordingFileIndexSource NeverSource(string message) =>
        new((_, _) => throw new InvalidOperationException(message));

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static T Single<T>(IReadOnlyList<T> values)
    {
        Equal(1, values.Count, "Expected exactly one item.");
        return values[0];
    }

    private static TException Throws<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{message} Expected {typeof(TException).Name}, got {exception.GetType().Name}.", exception);
        }

        throw new InvalidOperationException($"{message} Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class FakeVolumeProbe(Func<string, bool> selector) : IWindowsVolumeProbe
    {
        public List<string> Roots { get; } = [];

        public bool ShouldUseNtfsMft(string path)
        {
            Roots.Add(path);
            return selector(path);
        }
    }

    private sealed class RecordingFileIndexSource(
        Func<string, CancellationToken, FileIndexBuildResult> build) : IFileIndexSource
    {
        public List<string> Roots { get; } = [];

        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default)
        {
            Equal(1, roots.Count, "The composite source should invoke child sources one root at a time.");
            var root = roots[0];
            Roots.Add(root);
            return Task.FromResult(build(root, cancellationToken));
        }
    }
}
