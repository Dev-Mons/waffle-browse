using Waffle.Browse.Core.Search.Indexing;
using Waffle.Browse.Core.Search.Indexing.Ntfs;

namespace Waffle.Browse.Core.Tests.Search;

internal static class NtfsPathResolverTests
{
    private static readonly FileReferenceId RootId = new(5);

    public static void ResolvesPathsWithoutDependingOnRecordOrder()
    {
        var folderId = new FileReferenceId(20, 1);
        var fileId = new FileReferenceId(30, 2);
        var records = new[]
        {
            Record(fileId, folderId, "보고서.txt", FileAttributes.Archive),
            Record(RootId, RootId, ".", FileAttributes.Directory),
            Record(folderId, RootId, "자료", FileAttributes.Directory)
        };

        var result = NtfsPathResolver.Resolve(@"C:\", RootId, records);

        TestAssert.Equal(2, result.Entries.Count, "The volume root should seed paths but should not become an indexed entry");
        TestAssert.Equal(0L, result.SkippedPathCount, "A complete graph should not skip paths");
        var folder = result.Entries.Single(entry => entry.Record.FileReferenceId == folderId);
        var file = result.Entries.Single(entry => entry.Record.FileReferenceId == fileId);
        TestAssert.Equal(@"C:\자료", folder.FullPath, "Resolver should attach a child directory to the seeded drive root");
        TestAssert.Equal(@"C:\", folder.ParentPath, "Resolver should preserve the drive root as the directory parent");
        TestAssert.Equal(@"C:\자료\보고서.txt", file.FullPath, "Resolver should resolve a child that appeared before its parent in the MFT input");
        TestAssert.Equal(@"C:\자료", file.ParentPath, "Resolver should preserve the resolved parent path");
        TestAssert.True(file.Record.Attributes.HasFlag(FileAttributes.Archive), "Resolver should preserve the original MFT record");
    }

    public static void QuarantinesOnlyOrphanAndCycleComponents()
    {
        var records = new[]
        {
            Record(10, 5, "healthy.txt"),
            Record(20, 999, "orphan.txt"),
            Record(30, 30, "self-cycle"),
            Record(40, 41, "cycle-a"),
            Record(41, 40, "cycle-b"),
            Record(42, 40, "cycle-child")
        };

        var result = NtfsPathResolver.Resolve(@"D:\", RootId, records);

        TestAssert.Equal(1, result.Entries.Count, "Unrelated valid records should survive orphan and cycle quarantine");
        TestAssert.Equal(@"D:\healthy.txt", result.Entries.Single().FullPath, "Valid sibling path should still resolve");
        TestAssert.Equal(5L, result.SkippedPathCount, "Every record in orphan and cycle components should be counted once");
        TestAssert.Equal(5, result.Warnings.Count, "Each quarantined record should produce a warning below the cap");
    }

    public static void RejectsDuplicateFileReferenceIds()
    {
        var duplicate = new FileReferenceId(10);
        var records = new[]
        {
            Record(duplicate, RootId, "one.txt"),
            Record(duplicate, RootId, "two.txt")
        };

        try
        {
            _ = NtfsPathResolver.Resolve(@"C:\", RootId, records);
            throw new InvalidOperationException("Duplicate MFT file reference IDs should be rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }

    public static void ResolvesDeepGraphsIteratively()
    {
        const int depth = 2048;
        var records = new List<NtfsMftRecord>(depth);
        var parentId = RootId;
        var expectedPath = @"C:\";
        for (var index = 0; index < depth; index++)
        {
            var id = new FileReferenceId((ulong)(index + 10));
            var name = $"d{index}";
            records.Add(Record(id, parentId, name, FileAttributes.Directory));
            parentId = id;
            expectedPath = Path.Combine(expectedPath, name);
        }

        records.Reverse();
        var result = NtfsPathResolver.Resolve(@"C:\", RootId, records);

        TestAssert.Equal(depth, result.Entries.Count, "Every node in a deep graph should resolve without recursive stack use");
        TestAssert.Equal(0L, result.SkippedPathCount, "A deep valid graph should not be quarantined");
        var deepest = result.Entries.Single(entry => entry.Record.FileReferenceId == parentId);
        TestAssert.Equal(expectedPath, deepest.FullPath, "Iterative resolution should build the complete deep path");
    }

    public static void CapsWarningsWithoutLosingSkippedCount()
    {
        var records = Enumerable.Range(0, 105)
            .Select(index => Record((ulong)(1000 + index), (ulong)(5000 + index), $"orphan-{index}"))
            .ToArray();

        var result = NtfsPathResolver.Resolve(@"C:\", RootId, records);

        TestAssert.Equal(0, result.Entries.Count, "Every orphan should be quarantined");
        TestAssert.Equal(105L, result.SkippedPathCount, "Skipped count should include records beyond the warning cap");
        TestAssert.Equal(100, result.Warnings.Count, "Resolver warnings should be capped at one hundred");
    }

    private static NtfsMftRecord Record(
        ulong id,
        ulong parentId,
        string name,
        FileAttributes attributes = FileAttributes.Archive) =>
        Record(new FileReferenceId(id), new FileReferenceId(parentId), name, attributes);

    private static NtfsMftRecord Record(
        FileReferenceId id,
        FileReferenceId parentId,
        string name,
        FileAttributes attributes = FileAttributes.Archive) =>
        new(id, parentId, name, attributes);
}
