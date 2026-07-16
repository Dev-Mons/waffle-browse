using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.Core.Tests.Search;

internal static class RecursiveFileIndexSourceTests
{
    public static void IndexesAccessibleFilesWithoutFollowingReparsePoints()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "waffle-browse-tests",
            Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testDirectory, "root");
        var external = Path.Combine(testDirectory, "external");
        var link = Path.Combine(root, "external-link");
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(root, "folder", "visible.txt"), "visible");
        File.WriteAllText(Path.Combine(external, "outside-secret.txt"), "outside");

        var linkCreated = false;
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, external);
                linkCreated = true;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
            {
                // Some Windows environments do not allow unprivileged symlink creation.
            }

            var source = new RecursiveFileIndexSource();
            var progressEvents = new List<FileIndexProgressEventArgs>();
            source.ProgressChanged += (_, progress) => progressEvents.Add(progress);
            var result = source.BuildAsync([root])
                .GetAwaiter()
                .GetResult();

            TestAssert.True(
                result.Entries.Any(entry => entry.Name == "visible.txt"),
                "An accessible file below the configured root should be indexed");
            var completed = progressEvents.Last();
            TestAssert.Equal(1, completed.CompletedRootCount, "Progress should report the completed root");
            TestAssert.Equal(1, completed.TotalRootCount, "Progress should report the total root count");
            TestAssert.Equal((long)result.Entries.Count, completed.DiscoveredItemCount, "Progress should report discovered items");
            TestAssert.True(
                progressEvents.Any(progress => string.Equals(progress.CurrentRoot, root, StringComparison.OrdinalIgnoreCase)),
                "Progress should identify the root currently being scanned");
            if (linkCreated)
            {
                TestAssert.True(
                    result.Entries.All(entry => entry.Name != "outside-secret.txt"),
                    "The indexer must not traverse a directory reparse point");
            }
        }
        finally
        {
            if (linkCreated && Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
