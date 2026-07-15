using Waffle.Browse.Core.Search;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.Core.Tests.Search;

internal static class JsonFileIndexStoreTests
{
    public static void RoundTripsVersionTwoNativeIdentifiersAndNullMetadata()
    {
        WithTemporaryStore((store, _) =>
        {
            const string volumeId = @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\";
            const uint volumeSerialNumber = 0xFEDCBA98;
            const ulong journalId = 0xFFEEDDCCBBAA9988;
            var fileReference = new FileIndexFileReference(
                Low: 0xFEDCBA9876543210,
                High: 0x0123456789ABCDEF);
            var capturedAt = new DateTimeOffset(2026, 7, 15, 14, 25, 30, TimeSpan.FromHours(9)).AddTicks(1234);
            var completedAt = capturedAt.AddMinutes(-5);
            var checkpoint = new FileIndexCheckpoint(
                @"C:\",
                volumeId,
                "NTFS",
                journalId,
                long.MaxValue - 42,
                capturedAt,
                volumeSerialNumber);
            var entry = new FileIndexEntry(
                @"C:\Archive\record.bin",
                "record.bin",
                @"C:\Archive",
                SearchItemKind.File,
                Size: null,
                ModifiedAt: null,
                volumeId,
                fileReference);
            var snapshot = new FileIndexSnapshot(
                FileIndexSnapshot.CurrentFormatVersion,
                new FileIndexState(FileIndexBuildState.Ready, 17, 1, completedAt, [checkpoint]),
                [entry]);

            store.SaveAsync(snapshot).GetAwaiter().GetResult();
            var load = store.LoadAsync().GetAwaiter().GetResult();

            TestAssert.Equal(2, FileIndexSnapshot.CurrentFormatVersion, "Native identifier snapshot format should be version 2");
            TestAssert.Equal(FileIndexLoadKind.Loaded, load.Kind, "Version 2 snapshot should load successfully");
            TestAssert.NotNull(load.Snapshot, "Loaded result should include the snapshot");

            var loaded = load.Snapshot!;
            TestAssert.Equal(2, loaded.FormatVersion, "Snapshot format version should round-trip");
            TestAssert.Equal(FileIndexBuildState.Ready, loaded.State.BuildState, "Build state should round-trip");
            TestAssert.Equal(17L, loaded.State.Generation, "Generation should round-trip");
            TestAssert.Equal(1L, loaded.State.ItemCount, "Item count should round-trip");
            TestAssert.Equal<DateTimeOffset?>(completedAt, loaded.State.LastCompletedAt, "Completion time should round-trip");
            TestAssert.Equal(1, loaded.State.Checkpoints.Count, "Checkpoint count should round-trip");

            var loadedCheckpoint = loaded.State.Checkpoints.Single();
            TestAssert.Equal(@"C:\", loadedCheckpoint.RootPath, "Checkpoint root should round-trip");
            TestAssert.Equal(volumeId, loadedCheckpoint.VolumeId, "Volume GUID should round-trip");
            TestAssert.Equal("NTFS", loadedCheckpoint.FileSystem, "Filesystem should round-trip");
            TestAssert.Equal<ulong?>(journalId, loadedCheckpoint.JournalId, "Journal ID should round-trip");
            TestAssert.Equal<long?>(long.MaxValue - 42, loadedCheckpoint.NextUsn, "Next USN should round-trip");
            TestAssert.Equal(capturedAt, loadedCheckpoint.CapturedAt, "Checkpoint time should round-trip");
            TestAssert.Equal<uint?>(volumeSerialNumber, loadedCheckpoint.VolumeSerialNumber, "Volume serial number should round-trip");

            var loadedEntry = loaded.Entries.Single();
            TestAssert.Equal(entry.FullPath, loadedEntry.FullPath, "Entry path should round-trip");
            TestAssert.Equal(entry.Name, loadedEntry.Name, "Entry name should round-trip");
            TestAssert.Equal(entry.ParentPath, loadedEntry.ParentPath, "Entry parent path should round-trip");
            TestAssert.Equal(entry.Kind, loadedEntry.Kind, "Entry kind should round-trip");
            TestAssert.Equal<long?>(null, loadedEntry.Size, "Null size metadata should round-trip");
            TestAssert.Equal<DateTimeOffset?>(null, loadedEntry.ModifiedAt, "Null modified metadata should round-trip");
            TestAssert.Equal(volumeId, loadedEntry.VolumeId, "Entry volume GUID should round-trip");
            TestAssert.NotNull(loadedEntry.FileReferenceNumber, "128-bit file reference should round-trip");
            TestAssert.Equal(fileReference.Low, loadedEntry.FileReferenceNumber!.Value.Low, "File reference low bits should round-trip");
            TestAssert.Equal(fileReference.High, loadedEntry.FileReferenceNumber!.Value.High, "File reference high bits should round-trip");
        });
    }

    public static void RejectsVersionOneSnapshotAsCorrupt()
    {
        WithTemporaryStore((store, _) =>
        {
            var versionOne = new FileIndexSnapshot(
                FormatVersion: 1,
                new FileIndexState(FileIndexBuildState.Ready, 3, 0, DateTimeOffset.UtcNow, []),
                []);
            store.SaveAsync(versionOne).GetAwaiter().GetResult();

            var load = store.LoadAsync().GetAwaiter().GetResult();

            TestAssert.Equal(FileIndexLoadKind.Corrupt, load.Kind, "Version 1 snapshot should be rejected as corrupt");
            TestAssert.Equal<FileIndexSnapshot?>(null, load.Snapshot, "Rejected version 1 snapshot should not be returned");
            TestAssert.True(
                load.ErrorMessage?.Contains("지원하지 않는 Waffle 인덱스 형식", StringComparison.Ordinal) == true,
                "Version mismatch should report an unsupported format");
        });
    }

    public static void RejectsSemanticallyInvalidVersionTwoSnapshots()
    {
        WithTemporaryStore((store, filePath) =>
        {
            var invalidDocuments = new[]
            {
                """{"FormatVersion":2,"State":null,"Entries":[]}""",
                """{"FormatVersion":2,"State":{"BuildState":2,"Generation":1,"ItemCount":1,"LastCompletedAt":null,"Checkpoints":[]},"Entries":[null]}""",
                """{"FormatVersion":2,"State":{"BuildState":2,"Generation":1,"ItemCount":2,"LastCompletedAt":null,"Checkpoints":[]},"Entries":[]}"""
            };

            foreach (var document in invalidDocuments)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, document);
                var load = store.LoadAsync().GetAwaiter().GetResult();
                TestAssert.Equal(FileIndexLoadKind.Corrupt, load.Kind, "A structurally valid but semantically invalid v2 snapshot should be rejected");
                TestAssert.Equal<FileIndexSnapshot?>(null, load.Snapshot, "An invalid v2 snapshot should not be returned");
            }
        });
    }

    private static void WithTemporaryStore(Action<JsonFileIndexStore, string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"waffle-json-index-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "index.json");
        try
        {
            test(new JsonFileIndexStore(filePath), filePath);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
