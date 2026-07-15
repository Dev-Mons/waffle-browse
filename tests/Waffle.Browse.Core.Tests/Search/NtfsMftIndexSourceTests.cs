using Waffle.Browse.Core.Search;
using Waffle.Browse.Core.Search.Indexing;
using Waffle.Browse.Core.Search.Indexing.Ntfs;

namespace Waffle.Browse.Core.Tests.Search;

internal static class NtfsMftIndexSourceTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-07-15T12:00:00+09:00");

    public static void BuildsCompleteVolumeWithMetadataAndCheckpoint()
    {
        var rootId = new FileReferenceId(5);
        var folderId = new FileReferenceId(10);
        var fileId = new FileReferenceId(11, 2);
        var modifiedAt = DateTimeOffset.Parse("2026-07-14T01:02:03Z");
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", @"\\?\Volume{test}\", "NTFS", 0x1234ABCD, rootId),
            start => start switch
            {
                0 => new NtfsMftBatch(
                    100,
                    [
                        new NtfsMftRecord(fileId, folderId, "한글.txt", FileAttributes.Normal),
                        new NtfsMftRecord(folderId, rootId, "자료", FileAttributes.Directory)
                    ]),
                100 => null,
                _ => throw new InvalidOperationException($"Unexpected cursor {start}.")
            },
            new Dictionary<FileReferenceId, NtfsFileMetadata?>
            {
                [folderId] = new NtfsFileMetadata(null, modifiedAt.AddDays(-1)),
                [fileId] = new NtfsFileMetadata(42, modifiedAt)
            },
            new NtfsJournalCheckpoint(77, 1234));
        var source = new NtfsMftIndexSource(
            new FakeAccessorFactory(accessor),
            new FixedTimeProvider(CapturedAt));

        var result = source.BuildAsync([@"C:\"]).GetAwaiter().GetResult();

        TestAssert.Equal(2, result.Entries.Count, "A complete MFT generation should contain both resolved records");
        var file = result.Entries.Single(entry => entry.Name == "한글.txt");
        TestAssert.Equal(@"C:\자료\한글.txt", file.FullPath, "FRN parent chains should produce a full path");
        TestAssert.Equal(42L, file.Size, "File metadata should include the end-of-file size");
        TestAssert.Equal(modifiedAt, file.ModifiedAt, "File metadata should include the last-write time");
        TestAssert.Equal(fileId, file.FileReferenceNumber, "128-bit file IDs must be preserved");
        TestAssert.Equal(@"\\?\Volume{test}\", file.VolumeId, "Entries should retain their stable volume ID");

        var checkpoint = result.Checkpoints.Single();
        TestAssert.Equal(77UL, checkpoint.JournalId, "The journal ID should be captured after the build");
        TestAssert.Equal(1234L, checkpoint.NextUsn, "The next USN should be captured after the build");
        TestAssert.Equal(0x1234ABCDU, checkpoint.VolumeSerialNumber, "The volume serial should be persisted separately");
        TestAssert.Equal(CapturedAt, checkpoint.CapturedAt, "Checkpoint time should come from the injected clock");
        TestAssert.True(accessor.IdentityWasValidated, "The volume identity should be revalidated before publishing");
        TestAssert.True(accessor.Disposed, "The volume handle should be disposed after the build");
    }

    public static void KeepsEntriesWhenMetadataCannotBeRead()
    {
        var rootId = new FileReferenceId(5);
        var fileId = new FileReferenceId(20);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", "volume", "NTFS", 1, rootId),
            start => start == 0
                ? new NtfsMftBatch(10, [new NtfsMftRecord(fileId, rootId, "locked.bin", FileAttributes.Normal)])
                : null,
            new Dictionary<FileReferenceId, NtfsFileMetadata?> { [fileId] = null },
            journalState: StableJournalState());
        var source = new NtfsMftIndexSource(new FakeAccessorFactory(accessor));

        var result = source.BuildAsync([@"C:\"]).GetAwaiter().GetResult();

        var entry = result.Entries.Single();
        TestAssert.Equal<long?>(null, entry.Size, "A metadata failure should leave size unknown");
        TestAssert.Equal<DateTimeOffset?>(null, entry.ModifiedAt, "A metadata failure should leave last-write time unknown");
        TestAssert.Equal(0L, result.SkippedPathCount, "Metadata failures should not count as skipped paths");
        TestAssert.True(result.Warnings.Count == 1, "Metadata failures should be summarized as a warning");
    }

    public static void BuildReplaysChangesThatOccurDuringMftScan()
    {
        const string volumeId = @"\\?\Volume{catch-up}\";
        var rootId = new FileReferenceId(5);
        var fileId = new FileReferenceId(21);
        var journalStateCall = 0;
        var current = IndexedEntry(@"C:\sync.txt", SearchItemKind.File, fileId, volumeId, 9);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", volumeId, "NTFS", 0x10203040, rootId),
            start => start == 0
                ? new NtfsMftBatch(
                    1000,
                    [new NtfsMftRecord(fileId, rootId, "sync.txt", FileAttributes.Normal)])
                : null,
            new Dictionary<FileReferenceId, NtfsFileMetadata?>
            {
                [fileId] = new NtfsFileMetadata(1, CapturedAt.AddMinutes(-1))
            },
            readJournal: start => start == 100
                ? new NtfsUsnJournalBatch(
                    200,
                    [new NtfsMftRecord(fileId, rootId, "sync.txt", FileAttributes.Normal, 150, 0x00000001)])
                : throw new InvalidOperationException($"Unexpected catch-up cursor {start}."),
            currentEntries: new Dictionary<FileReferenceId, FileIndexEntry?> { [fileId] = current },
            readJournalState: () =>
            {
                journalStateCall++;
                var nextUsn = journalStateCall == 1 ? 100 : 200;
                return new NtfsJournalState(88, 0, nextUsn, 0, 1000);
            });
        var source = new NtfsMftIndexSource(new FakeAccessorFactory(accessor));

        var result = source.BuildAsync([@"C:\"]).GetAwaiter().GetResult();

        TestAssert.Equal(9L, result.Entries.Single().Size, "Changes after the MFT cursor passed must be replayed before publication");
        TestAssert.Equal(88UL, result.Checkpoints.Single().JournalId, "Catch-up should retain the journal observed before scanning");
        TestAssert.Equal(200L, result.Checkpoints.Single().NextUsn, "The checkpoint must follow every replayed during-scan change");
    }

    public static void MissingJournalStateDoesNotPublishMftGeneration()
    {
        var mftReadCount = 0;
        var rootId = new FileReferenceId(5);
        var fileId = new FileReferenceId(21);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", "volume", "NTFS", 1, rootId),
            start =>
            {
                mftReadCount++;
                return start switch
                {
                    0 => new NtfsMftBatch(
                        100,
                        [new NtfsMftRecord(fileId, rootId, "first.txt", FileAttributes.Normal)]),
                    100 => new NtfsMftBatch(200, []),
                    _ => null
                };
            },
            readJournalState: () => null);
        var factory = new FakeAccessorFactory(accessor);
        var source = new NtfsMftIndexSource(factory);

        try
        {
            source.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
            throw new InvalidOperationException("A journal-less MFT generation must not be published.");
        }
        catch (NtfsJournalInvalidatedException)
        {
        }

        TestAssert.Equal(2, factory.OpenCount, "A missing journal should get one bounded retry before surfacing");
        TestAssert.Equal(0, mftReadCount, "The source should require a journal start before reading any MFT batch");
        TestAssert.False(accessor.IdentityWasValidated, "A rejected generation must not reach publication validation");
    }

    public static void MissingJournalStateRetainsLastGoodRoot()
    {
        const string volumeId = "volume";
        var rootId = new FileReferenceId(5);
        var retainedEntry = IndexedEntry(
            @"C:\retained.txt",
            SearchItemKind.File,
            new FileReferenceId(22),
            volumeId,
            10);
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                3,
                1,
                CapturedAt,
                [new FileIndexCheckpoint(@"C:\", volumeId, "NTFS", 1, 10, CapturedAt, 1)]),
            [retainedEntry]);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", volumeId, "NTFS", 1, rootId),
            _ => throw new InvalidOperationException("Missing journal state should stop before MFT enumeration."),
            readJournalState: () => null);
        var fallbackEntry = IndexedEntry(
            @"C:\fallback.txt",
            SearchItemKind.File,
            new FileReferenceId(23),
            volumeId,
            20);
        var fallback = new SnapshotStubSource(new FileIndexBuildResult([fallbackEntry], [], []));
        var source = new FallbackFileIndexSource(
            new NtfsMftIndexSource(new FakeAccessorFactory(accessor)),
            fallback);

        var result = source.RefreshAsync([@"C:\"], baseline).GetAwaiter().GetResult();

        TestAssert.Equal("retained.txt", result.Entries.Single().Name, "An inactive journal should retain the last complete root generation");
        TestAssert.Equal(0, fallback.BuildCallCount, "Journal invalidation must not switch to a recursive build");
        TestAssert.Equal(0, fallback.RefreshCallCount, "Journal invalidation must not replace the retained root through fallback refresh");
    }

    public static void JournalStateErrorsUseFallbackOrRetentionTaxonomy()
    {
        TestAssert.True(
            WindowsNtfsVolumeAccessor.CreateJournalStateException(5) is UnauthorizedAccessException,
            "Access denied should remain eligible for the recursive fallback");
        TestAssert.True(
            WindowsNtfsVolumeAccessor.CreateJournalStateException(50) is NotSupportedException,
            "Unsupported journal control should remain eligible for the recursive fallback");
        TestAssert.True(
            WindowsNtfsVolumeAccessor.CreateJournalStateException(1179) is NotSupportedException,
            "A volume without an active journal should use the supported recursive fallback");

        foreach (var error in new[] { 87, 1178, 1181 })
        {
            TestAssert.True(
                WindowsNtfsVolumeAccessor.CreateJournalStateException(error) is NtfsJournalInvalidatedException,
                $"Journal lifecycle error {error} should retain the last complete generation");
        }

        var io = WindowsNtfsVolumeAccessor.CreateJournalStateException(1117);
        TestAssert.True(
            io is IOException and not NtfsJournalInvalidatedException,
            "Other device I/O failures should be retention eligible, not fallback eligible");
    }

    public static void RejectsEnumerationWithoutForwardProgress()
    {
        var rootId = new FileReferenceId(5);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", "volume", "NTFS", 1, rootId),
            _ => new NtfsMftBatch(0, []),
            journalState: StableJournalState());
        var source = new NtfsMftIndexSource(new FakeAccessorFactory(accessor));

        try
        {
            source.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
            throw new InvalidOperationException("A repeated MFT cursor should fail the generation.");
        }
        catch (InvalidDataException)
        {
        }

        TestAssert.True(accessor.Disposed, "A failed MFT generation should still close the volume");
    }

    public static void CancellationDoesNotReturnPartialGeneration()
    {
        using var cancellation = new CancellationTokenSource();
        var rootId = new FileReferenceId(5);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", "volume", "NTFS", 1, rootId),
            start =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return new NtfsMftBatch(start + 1, []);
            },
            journalState: StableJournalState());
        var source = new NtfsMftIndexSource(new FakeAccessorFactory(accessor));

        try
        {
            source.BuildAsync([@"C:\"], cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("A canceled MFT generation should not return a partial result.");
        }
        catch (OperationCanceledException)
        {
        }

        TestAssert.True(accessor.Disposed, "A canceled MFT generation should close the volume");
    }

    public static void ReplaysJournalChangesFromPersistedCheckpoint()
    {
        const string volumeId = @"\\?\Volume{incremental}\";
        var rootId = new FileReferenceId(5);
        var folderId = new FileReferenceId(10);
        var childId = new FileReferenceId(11);
        var deletedId = new FileReferenceId(12);
        var changedId = new FileReferenceId(13);
        var baselineEntries = new[]
        {
            IndexedEntry(@"C:\Old", SearchItemKind.Folder, folderId, volumeId),
            IndexedEntry(@"C:\Old\child.txt", SearchItemKind.File, childId, volumeId, 1),
            IndexedEntry(@"C:\removed.txt", SearchItemKind.File, deletedId, volumeId, 2),
            IndexedEntry(@"C:\changed.txt", SearchItemKind.File, changedId, volumeId, 3)
        };
        var checkpoint = new FileIndexCheckpoint(
            @"C:\",
            volumeId,
            "NTFS",
            77,
            100,
            CapturedAt.AddHours(-1),
            0xAABBCCDD);
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                5,
                baselineEntries.Length,
                CapturedAt.AddHours(-1),
                [checkpoint]),
            baselineEntries);
        var currentFolder = IndexedEntry(@"C:\New", SearchItemKind.Folder, folderId, volumeId);
        var currentChanged = IndexedEntry(@"C:\changed.txt", SearchItemKind.File, changedId, volumeId, 99);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", volumeId, "NTFS", 0xAABBCCDD, rootId),
            _ => throw new InvalidOperationException("A valid journal checkpoint should not enumerate the MFT."),
            journalState: new NtfsJournalState(77, 0, 200, 50, 1000),
            readJournal: start => start == 100
                ? new NtfsUsnJournalBatch(
                    200,
                    [
                        new NtfsMftRecord(folderId, rootId, "Old", FileAttributes.Directory, 110, 0x00001000),
                        new NtfsMftRecord(folderId, rootId, "New", FileAttributes.Directory, 120, 0x00002000),
                        new NtfsMftRecord(deletedId, rootId, "removed.txt", FileAttributes.Normal, 130, 0x00000200),
                        new NtfsMftRecord(changedId, rootId, "changed.txt", FileAttributes.Normal, 140, 0x00000001)
                    ])
                : throw new InvalidOperationException($"Unexpected journal cursor {start}."),
            currentEntries: new Dictionary<FileReferenceId, FileIndexEntry?>
            {
                [folderId] = currentFolder,
                [deletedId] = null,
                [changedId] = currentChanged
            });
        var source = new NtfsMftIndexSource(
            new FakeAccessorFactory(accessor),
            new FixedTimeProvider(CapturedAt));

        var result = source.RefreshAsync([@"C:\"], baseline).GetAwaiter().GetResult();

        TestAssert.Equal(3, result.Entries.Count, "Journal replay should delete the removed entry only");
        TestAssert.True(
            result.Entries.Any(entry => entry.FullPath == @"C:\New\child.txt"),
            "A directory rename should move all indexed descendants atomically");
        TestAssert.False(
            result.Entries.Any(entry => entry.FileReferenceNumber == deletedId),
            "A delete reason should remove the indexed file ID");
        TestAssert.Equal(
            99L,
            result.Entries.Single(entry => entry.FileReferenceNumber == changedId).Size,
            "A data change should refresh current metadata by file ID");
        TestAssert.Equal(200L, result.Checkpoints.Single().NextUsn, "The published checkpoint should follow the applied batch");
        TestAssert.Equal(77UL, result.Checkpoints.Single().JournalId, "Journal replay must retain the validated journal ID");
    }

    public static void InvalidJournalCheckpointTriggersFullMftRebuild()
    {
        const string volumeId = @"\\?\Volume{rebuilt}\";
        var rootId = new FileReferenceId(5);
        var rebuiltId = new FileReferenceId(30);
        var baselineEntry = IndexedEntry(@"C:\stale.txt", SearchItemKind.File, new FileReferenceId(20), volumeId, 1);
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                2,
                1,
                CapturedAt.AddHours(-2),
                [
                    new FileIndexCheckpoint(
                        @"C:\",
                        volumeId,
                        "NTFS",
                        70,
                        40,
                        CapturedAt.AddHours(-2),
                        0x12345678)
                ]),
            [baselineEntry]);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", volumeId, "NTFS", 0x12345678, rootId),
            start => start == 0
                ? new NtfsMftBatch(
                    100,
                    [new NtfsMftRecord(rebuiltId, rootId, "rebuilt.txt", FileAttributes.Normal)])
                : null,
            new Dictionary<FileReferenceId, NtfsFileMetadata?>
            {
                [rebuiltId] = new NtfsFileMetadata(8, CapturedAt)
            },
            journalState: new NtfsJournalState(71, 0, 80, 50, 1000));
        var factory = new FakeAccessorFactory(accessor);
        var source = new NtfsMftIndexSource(factory, new FixedTimeProvider(CapturedAt));

        var result = source.RefreshAsync([@"C:\"], baseline).GetAwaiter().GetResult();

        TestAssert.Equal(2, factory.OpenCount, "An invalid journal ID or wrapped USN should reopen the volume for a full MFT rebuild");
        TestAssert.Equal("rebuilt.txt", result.Entries.Single().Name, "The invalid checkpoint must not publish stale incremental data");
        TestAssert.Equal(71UL, result.Checkpoints.Single().JournalId, "The rebuilt generation should capture the current journal ID");
        TestAssert.Equal(80L, result.Checkpoints.Single().NextUsn, "The rebuilt generation should capture the current next USN");
    }

    public static void JournalReplayPreservesEveryHardLinkPath()
    {
        const string volumeId = @"\\?\Volume{hard-links}\";
        var rootId = new FileReferenceId(5);
        var fileId = new FileReferenceId(40);
        var first = IndexedEntry(@"C:\one.txt", SearchItemKind.File, fileId, volumeId, 4);
        var removedLink = IndexedEntry(@"C:\two.txt", SearchItemKind.File, fileId, volumeId, 4);
        var addedLink = IndexedEntry(@"C:\three.txt", SearchItemKind.File, fileId, volumeId, 4);
        var checkpoint = new FileIndexCheckpoint(
            @"C:\",
            volumeId,
            "NTFS",
            91,
            300,
            CapturedAt,
            0xCAFEBABE);
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(FileIndexBuildState.Ready, 3, 2, CapturedAt, [checkpoint]),
            [first, removedLink]);
        var accessor = new FakeAccessor(
            new NtfsVolumeIdentity(@"C:\", volumeId, "NTFS", 0xCAFEBABE, rootId),
            _ => throw new InvalidOperationException("Hard-link replay should not enumerate the MFT."),
            journalState: new NtfsJournalState(91, 0, 400, 200, 1000),
            readJournal: start => start == 300
                ? new NtfsUsnJournalBatch(
                    400,
                    [new NtfsMftRecord(fileId, rootId, "three.txt", FileAttributes.Normal, 350, 0x00010000)])
                : throw new InvalidOperationException($"Unexpected hard-link cursor {start}."),
            currentEntrySets: new Dictionary<FileReferenceId, IReadOnlyList<FileIndexEntry>>
            {
                [fileId] = [first, addedLink]
            });
        var source = new NtfsMftIndexSource(new FakeAccessorFactory(accessor));

        var result = source.RefreshAsync([@"C:\"], baseline).GetAwaiter().GetResult();

        TestAssert.Equal(2, result.Entries.Count, "A file with two hard links should retain two searchable paths");
        TestAssert.True(result.Entries.Any(entry => entry.FullPath == first.FullPath), "An unchanged hard link should remain indexed");
        TestAssert.True(result.Entries.Any(entry => entry.FullPath == addedLink.FullPath), "A new hard link should become searchable");
        TestAssert.False(result.Entries.Any(entry => entry.FullPath == removedLink.FullPath), "A removed hard link path should be deleted");
    }

    public static void FallsBackOnlyForUnsupportedOrDeniedNativeAccess()
    {
        var fallbackEntry = new FileIndexEntry(
            @"C:\fallback.txt",
            "fallback.txt",
            @"C:\",
            SearchItemKind.File,
            1,
            CapturedAt);
        var fallback = new StubSource(_ => new FileIndexBuildResult([fallbackEntry], [], []));
        var denied = new StubSource(_ => throw new UnauthorizedAccessException("denied"));
        var source = new FallbackFileIndexSource(denied, fallback);

        var result = source.BuildAsync([@"C:\"]).GetAwaiter().GetResult();

        TestAssert.Equal(1, result.Entries.Count, "Access denied should use the recursive fallback for that root");
        TestAssert.Equal(1, fallback.CallCount, "The fallback should run once for the denied root");
        TestAssert.True(result.Warnings.Count == 1, "Using the fallback should be visible in build warnings");

        var incrementalFallback = new SnapshotStubSource(
            new FileIndexBuildResult([fallbackEntry], [], []));
        var incrementalSource = new FallbackFileIndexSource(denied, incrementalFallback);
        var incrementalBaseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(FileIndexBuildState.Ready, 1, 0, CapturedAt, []),
            []);

        _ = incrementalSource
            .RefreshAsync([@"C:\"], incrementalBaseline)
            .GetAwaiter()
            .GetResult();

        TestAssert.Equal(0, incrementalFallback.BuildCallCount, "A refresh fallback should not discard its persisted checkpoint");
        TestAssert.Equal(1, incrementalFallback.RefreshCallCount, "A refresh fallback should receive the persisted baseline");

        var ioFallback = new StubSource(_ => new FileIndexBuildResult([fallbackEntry], [], []));
        var ioFailure = new FallbackFileIndexSource(
            new StubSource(_ => throw new IOException("detached")),
            ioFallback);
        try
        {
            ioFailure.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
            throw new InvalidOperationException("A detached volume should preserve the old generation instead of silently falling back.");
        }
        catch (IOException)
        {
        }

        TestAssert.Equal(0, ioFallback.CallCount, "I/O corruption or detach should not trigger a recursive replacement");

        var retainedEntry = IndexedEntry(
            @"C:\retained.txt",
            SearchItemKind.File,
            new FileReferenceId(99),
            "retained-volume",
            5);
        var retainedCheckpoint = new FileIndexCheckpoint(
            @"C:\",
            "retained-volume",
            "NTFS",
            1,
            10,
            CapturedAt,
            2);
        var retainedSnapshot = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(FileIndexBuildState.Ready, 1, 1, CapturedAt, [retainedCheckpoint]),
            [retainedEntry]);
        var retainedFallback = new StubSource(_ => new FileIndexBuildResult([fallbackEntry], [], []));
        var retainingSource = new FallbackFileIndexSource(
            new StubSource(_ => throw new IOException("temporarily detached")),
            retainedFallback);

        var retained = retainingSource
            .RefreshAsync([@"C:\"], retainedSnapshot)
            .GetAwaiter()
            .GetResult();
        TestAssert.Equal("retained.txt", retained.Entries.Single().Name, "A detached volume should retain only its last good generation");
        TestAssert.Equal(0, retainedFallback.CallCount, "A transient detach should not replace cached volume data with a recursive scan");

        var retainedAgain = retainingSource.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
        TestAssert.Equal("retained.txt", retainedAgain.Entries.Single().Name, "The cached per-volume generation should survive a later full rebuild request");
    }

    public static void FallbackRefreshSlicesBaselinePerRoot()
    {
        var first = IndexedEntry(@"C:\first.txt", SearchItemKind.File, new FileReferenceId(1), "first-volume");
        var second = IndexedEntry(@"D:\second.txt", SearchItemKind.File, new FileReferenceId(2), "second-volume");
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                1,
                2,
                CapturedAt,
                [
                    new FileIndexCheckpoint(@"C:\", "first-volume", "NTFS", 1, 10, CapturedAt, 1),
                    new FileIndexCheckpoint(@"D:\", "second-volume", "NTFS", 2, 20, CapturedAt, 2)
                ]),
            [first, second]);
        var fallback = new SnapshotStubSource(new FileIndexBuildResult([], [], []));
        var source = new FallbackFileIndexSource(
            new StubSource(_ => throw new UnauthorizedAccessException("denied")),
            fallback);

        _ = source.RefreshAsync([@"C:\", @"D:\"], baseline).GetAwaiter().GetResult();

        TestAssert.Equal(2, fallback.Baselines.Count, "Each root should receive one incremental baseline");
        TestAssert.Equal("first.txt", fallback.Baselines[0].Entries.Single().Name, "The first source call should not serialize other volumes");
        TestAssert.Equal("second.txt", fallback.Baselines[1].Entries.Single().Name, "The second source call should not serialize other volumes");
        TestAssert.Equal(1, fallback.Baselines[0].State.Checkpoints.Count, "Each sliced baseline should contain one checkpoint");
    }

    public static void FallbackBuildIsolatesUnavailableRoots()
    {
        var healthyEntry = IndexedEntry(
            @"C:\healthy.txt",
            SearchItemKind.File,
            new FileReferenceId(1),
            "healthy-volume");
        var healthyCheckpoint = new FileIndexCheckpoint(
            @"C:\",
            "healthy-volume",
            "NTFS",
            1,
            10,
            CapturedAt,
            1);
        var fallback = new StubSource(_ => throw new InvalidOperationException("I/O failures must not use recursive fallback."));
        var source = new FallbackFileIndexSource(
            new StubSource(roots => string.Equals(roots.Single(), @"C:\", StringComparison.OrdinalIgnoreCase)
                ? new FileIndexBuildResult([healthyEntry], [healthyCheckpoint], [])
                : throw new IOException("detached")),
            fallback);

        var result = source.BuildAsync([@"C:\", @"D:\"]).GetAwaiter().GetResult();

        TestAssert.Equal(1, result.Entries.Count, "A failed root must not discard an already completed root generation");
        TestAssert.Equal("healthy.txt", result.Entries.Single().Name, "The healthy root generation should still be published");
        TestAssert.Equal(0, fallback.CallCount, "An I/O failure must not silently switch to recursive enumeration");
        TestAssert.True(result.Warnings.Any(warning => warning.Contains(@"D:\", StringComparison.OrdinalIgnoreCase)), "The excluded root should be visible in warnings");
    }

    public static void FallbackRejectsWarningOnlyEmptyGeneration()
    {
        var source = new FallbackFileIndexSource(
            new StubSource(_ => new FileIndexBuildResult([], [], ["share unavailable"])),
            new StubSource(_ => throw new InvalidOperationException("The primary returned a result.")));

        try
        {
            _ = source.BuildAsync([@"\\server\share"]).GetAwaiter().GetResult();
            throw new InvalidOperationException("A warning-only root without a checkpoint must not publish an empty ready generation.");
        }
        catch (IOException)
        {
        }
    }

    public static void FallbackRefreshSlicesSameVolumeRootsByPath()
    {
        const string volumeId = "shared-volume";
        var parentEntry = IndexedEntry(
            @"C:\Parent\outside.txt",
            SearchItemKind.File,
            new FileReferenceId(1),
            volumeId);
        var nestedEntry = IndexedEntry(
            @"C:\Parent\Nested\inside.txt",
            SearchItemKind.File,
            new FileReferenceId(2),
            volumeId);
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                1,
                2,
                CapturedAt,
                [new FileIndexCheckpoint(@"C:\Parent\Nested", volumeId, "NTFS", 1, 10, CapturedAt, 1)]),
            [parentEntry, nestedEntry]);
        var fallback = new SnapshotStubSource(new FileIndexBuildResult([], [], []));
        var source = new FallbackFileIndexSource(
            new StubSource(_ => throw new UnauthorizedAccessException("denied")),
            fallback);

        _ = source
            .RefreshAsync([@"C:\Parent\Nested"], baseline)
            .GetAwaiter()
            .GetResult();

        TestAssert.Equal(1, fallback.Baselines.Count, "The configured nested root should receive one baseline");
        TestAssert.Equal("inside.txt", fallback.Baselines.Single().Entries.Single().Name, "Same-volume entries outside the configured root must not leak into its baseline");
    }

    private sealed class FakeAccessorFactory(FakeAccessor accessor) : INtfsVolumeAccessorFactory
    {
        public int OpenCount { get; private set; }

        public INtfsVolumeAccessor Open(string rootPath)
        {
            TestAssert.Equal(accessor.Identity.RootPath, rootPath, "The source should open the normalized configured root");
            OpenCount++;
            return accessor;
        }
    }

    private static NtfsJournalState StableJournalState() => new(1, 0, 0, 0, 1_000);

    private static FileIndexEntry IndexedEntry(
        string path,
        SearchItemKind kind,
        FileReferenceId id,
        string volumeId,
        long? size = null) =>
        new(
            path,
            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetDirectoryName(path) ?? string.Empty,
            kind,
            size,
            CapturedAt,
            volumeId,
            id);

    private sealed class FakeAccessor(
        NtfsVolumeIdentity identity,
        Func<ulong, NtfsMftBatch?> readBatch,
        IReadOnlyDictionary<FileReferenceId, NtfsFileMetadata?>? metadata = null,
        NtfsJournalCheckpoint? journal = null,
        NtfsJournalState? journalState = null,
        Func<long, NtfsUsnJournalBatch>? readJournal = null,
        IReadOnlyDictionary<FileReferenceId, FileIndexEntry?>? currentEntries = null,
        Func<NtfsJournalState?>? readJournalState = null,
        IReadOnlyDictionary<FileReferenceId, IReadOnlyList<FileIndexEntry>>? currentEntrySets = null) : INtfsVolumeAccessor
    {
        public NtfsVolumeIdentity Identity { get; } = identity;

        public bool IdentityWasValidated { get; private set; }

        public bool Disposed { get; private set; }

        public NtfsMftBatch? ReadMftBatch(ulong startFileReferenceNumber, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return readBatch(startFileReferenceNumber);
        }

        public NtfsFileMetadata? TryReadMetadata(FileReferenceId fileReferenceNumber, bool isDirectory)
        {
            _ = isDirectory;
            return metadata is not null && metadata.TryGetValue(fileReferenceNumber, out var value)
                ? value
                : null;
        }

        public FileIndexEntry? TryReadCurrentEntry(FileReferenceId fileReferenceNumber) =>
            currentEntries is not null && currentEntries.TryGetValue(fileReferenceNumber, out var entry)
                ? entry
                : null;

        public IReadOnlyList<FileIndexEntry> TryReadCurrentEntries(FileReferenceId fileReferenceNumber) =>
            currentEntrySets is not null && currentEntrySets.TryGetValue(fileReferenceNumber, out var entries)
                ? entries
                : TryReadCurrentEntry(fileReferenceNumber) is { } entry ? [entry] : [];

        public NtfsJournalCheckpoint? TryReadJournalCheckpoint() =>
            journal ?? (journalState is null
                ? null
                : new NtfsJournalCheckpoint(journalState.JournalId, journalState.NextUsn));

        public NtfsJournalState? TryReadJournalState() =>
            readJournalState is not null
                ? readJournalState()
                : journalState ?? (journal is null
                    ? null
                    : new NtfsJournalState(journal.JournalId, 0, journal.NextUsn, 0, long.MaxValue));

        public NtfsUsnJournalBatch ReadUsnJournalBatch(
            long startUsn,
            ulong journalId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readJournal is null)
            {
                throw new InvalidOperationException("The fake journal was not configured.");
            }

            return readJournal(startUsn);
        }

        public void EnsureIdentityUnchanged() => IdentityWasValidated = true;

        public void Dispose() => Disposed = true;
    }

    private sealed class StubSource(Func<IReadOnlyList<string>, FileIndexBuildResult> build) : IFileIndexSource
    {
        public int CallCount { get; private set; }

        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(build(roots));
        }
    }

    private sealed class SnapshotStubSource(FileIndexBuildResult result) : IFileIndexSnapshotSource
    {
        public int BuildCallCount { get; private set; }

        public int RefreshCallCount { get; private set; }

        public List<FileIndexSnapshot> Baselines { get; } = [];

        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCallCount++;
            return Task.FromResult(result);
        }

        public Task<FileIndexBuildResult> RefreshAsync(
            IReadOnlyList<string> roots,
            FileIndexSnapshot baseline,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCallCount++;
            Baselines.Add(baseline);
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
