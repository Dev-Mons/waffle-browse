using Waffle.Browse.Core.Search.Indexing.Ntfs;
using System.Runtime.ExceptionServices;

namespace Waffle.Browse.Core.Search.Indexing;

public sealed class NtfsMftIndexSource : IFileIndexSnapshotSource
{
    private const int MaximumWarnings = 100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;

    private readonly INtfsVolumeAccessorFactory accessorFactory;
    private readonly TimeProvider timeProvider;

    public NtfsMftIndexSource()
        : this(new WindowsNtfsVolumeAccessorFactory(), TimeProvider.System)
    {
    }

    internal NtfsMftIndexSource(
        INtfsVolumeAccessorFactory accessorFactory,
        TimeProvider? timeProvider = null)
    {
        this.accessorFactory = accessorFactory ?? throw new ArgumentNullException(nameof(accessorFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<FileIndexBuildResult> BuildAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Task.Run(() => BuildWithRetry(roots, cancellationToken), cancellationToken);
    }

    public Task<FileIndexBuildResult> RefreshAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(baseline);
        return Task.Run(() => Refresh(roots, baseline, cancellationToken), cancellationToken);
    }

    private FileIndexBuildResult Build(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        var entries = new List<FileIndexEntry>();
        var checkpoints = new List<FileIndexCheckpoint>();
        var warnings = new List<string>();
        long skippedPathCount = 0;

        foreach (var configuredRoot in roots
                     .Where(root => !string.IsNullOrWhiteSpace(root))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var accessor = accessorFactory.Open(configuredRoot);
            var identity = accessor.Identity;
            var startingJournal = accessor.TryReadJournalState()
                ?? throw new NtfsJournalInvalidatedException(
                    "활성 NTFS 변경 저널 시작점 없이 MFT 세대를 만들 수 없습니다.");
            var records = ReadAllRecords(accessor, cancellationToken);
            var resolution = NtfsPathResolver.Resolve(
                identity.RootPath,
                identity.RootFileReferenceNumber,
                records);

            skippedPathCount += resolution.SkippedPathCount;
            AddWarnings(warnings, identity.RootPath, resolution.Warnings);

            var (rootEntries, metadataFailureCount) = BuildEntries(
                accessor,
                identity,
                resolution.Entries,
                cancellationToken);

            if (metadataFailureCount > 0)
            {
                AddWarning(
                    warnings,
                    $"{identity.RootPath}: {metadataFailureCount:N0}개 항목의 메타데이터를 읽지 못했습니다.");
            }

            var catchUpJournal = accessor.TryReadJournalState()
                ?? throw new NtfsJournalInvalidatedException(
                    "NTFS 변경 저널이 MFT 스캔 중 비활성화되었습니다.");
            if (catchUpJournal.JournalId != startingJournal.JournalId
                || startingJournal.NextUsn < catchUpJournal.LowestValidUsn
                || startingJournal.NextUsn > catchUpJournal.NextUsn)
            {
                throw new NtfsJournalInvalidatedException(
                    "NTFS 변경 저널이 MFT 스캔 중 교체되거나 랩되었습니다.");
            }

            var caughtUpEntries = TryApplyJournalChanges(
                accessor,
                identity,
                rootEntries,
                startingJournal.NextUsn,
                catchUpJournal.NextUsn,
                startingJournal.JournalId,
                cancellationToken,
                out var caughtUpUsn,
                out var catchUpMetadataFailures);
            if (caughtUpEntries is null)
            {
                throw new InvalidDataException("MFT 스캔 중 변경 사항을 완전한 세대로 합치지 못했습니다.");
            }

            rootEntries = caughtUpEntries.ToList();
            if (catchUpMetadataFailures > 0)
            {
                AddWarning(
                    warnings,
                    $"{identity.RootPath}: {catchUpMetadataFailures:N0}개 동시 변경 항목의 메타데이터를 읽지 못했습니다.");
            }

            var journalId = startingJournal.JournalId;
            var nextUsn = caughtUpUsn;

            accessor.EnsureIdentityUnchanged();
            var endingJournal = accessor.TryReadJournalState()
                ?? throw new NtfsJournalInvalidatedException(
                    "NTFS 변경 저널이 MFT 세대 게시 전에 비활성화되었습니다.");
            if (endingJournal.JournalId != journalId
                || nextUsn < endingJournal.LowestValidUsn
                || nextUsn > endingJournal.NextUsn)
            {
                throw new NtfsJournalInvalidatedException(
                    "NTFS 변경 저널이 MFT 세대 게시 전에 무효화되었습니다.");
            }

            entries.AddRange(rootEntries);
            checkpoints.Add(new FileIndexCheckpoint(
                identity.RootPath,
                identity.VolumeId,
                identity.FileSystem,
                journalId,
                nextUsn,
                timeProvider.GetUtcNow(),
                identity.SerialNumber));
        }

        return new FileIndexBuildResult(entries, checkpoints, warnings, skippedPathCount);
    }

    private static (List<FileIndexEntry> Entries, long MetadataFailureCount) BuildEntries(
        INtfsVolumeAccessor accessor,
        NtfsVolumeIdentity identity,
        IReadOnlyList<NtfsResolvedPath> resolvedEntries,
        CancellationToken cancellationToken)
    {
        var entryGroups = new IReadOnlyList<FileIndexEntry>[resolvedEntries.Count];
        long metadataFailureCount = 0;
        try
        {
            Parallel.For(
                0,
                resolvedEntries.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
                },
                index =>
                {
                    var resolved = resolvedEntries[index];
                    var isDirectory = resolved.Record.Attributes.HasFlag(FileAttributes.Directory);
                    var currentEntries = accessor.TryReadCurrentEntries(resolved.Record.FileReferenceId);
                    if (currentEntries.Count > 0)
                    {
                        entryGroups[index] = currentEntries;
                        return;
                    }

                    var metadata = accessor.TryReadMetadata(resolved.Record.FileReferenceId, isDirectory);
                    if (metadata is null)
                    {
                        Interlocked.Increment(ref metadataFailureCount);
                    }

                    entryGroups[index] =
                    [
                        new FileIndexEntry(
                            resolved.FullPath,
                            resolved.Record.Name,
                            resolved.ParentPath,
                            isDirectory ? SearchItemKind.Folder : SearchItemKind.File,
                            isDirectory ? null : metadata?.Size,
                            metadata?.ModifiedAt,
                            identity.VolumeId,
                            resolved.Record.FileReferenceId)
                    ];
                });
        }
        catch (AggregateException aggregate)
        {
            var exception = aggregate.Flatten().InnerExceptions
                .FirstOrDefault(candidate => candidate is not OperationCanceledException)
                ?? aggregate.Flatten().InnerExceptions[0];
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }

        return (entryGroups.SelectMany(group => group).ToList(), metadataFailureCount);
    }

    private FileIndexBuildResult BuildWithRetry(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        try
        {
            return Build(roots, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or NtfsJournalInvalidatedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Build(roots, cancellationToken);
        }
    }

    private FileIndexBuildResult Refresh(
        IReadOnlyList<string> roots,
        FileIndexSnapshot baseline,
        CancellationToken cancellationToken)
    {
        try
        {
            var entries = new List<FileIndexEntry>();
            var checkpoints = new List<FileIndexCheckpoint>();
            var warnings = new List<string>();

            foreach (var configuredRoot in roots
                         .Where(root => !string.IsNullOrWhiteSpace(root))
                         .Select(Path.GetFullPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refreshed = TryRefreshRoot(configuredRoot, baseline, cancellationToken);
                if (refreshed is null)
                {
                    return BuildWithRetry(roots, cancellationToken);
                }

                entries.AddRange(refreshed.Entries);
                checkpoints.AddRange(refreshed.Checkpoints);
                foreach (var warning in refreshed.Warnings)
                {
                    AddWarning(warnings, warning);
                }
            }

            return new FileIndexBuildResult(entries, checkpoints, warnings);
        }
        catch (Exception ex) when (ex is InvalidDataException or NtfsJournalInvalidatedException)
        {
            return BuildWithRetry(roots, cancellationToken);
        }
    }

    private FileIndexBuildResult? TryRefreshRoot(
        string configuredRoot,
        FileIndexSnapshot baseline,
        CancellationToken cancellationToken)
    {
        using var accessor = accessorFactory.Open(configuredRoot);
        var identity = accessor.Identity;
        var matchingCheckpoints = baseline.State.Checkpoints
            .Where(candidate => string.Equals(
                candidate.RootPath,
                identity.RootPath,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (matchingCheckpoints.Count != 1)
        {
            return null;
        }

        var checkpoint = matchingCheckpoints[0];
        var journal = accessor.TryReadJournalState();
        if (!IsUsableCheckpoint(checkpoint, identity, journal))
        {
            return null;
        }

        var volumeEntries = baseline.Entries
            .Where(entry => string.Equals(entry.VolumeId, identity.VolumeId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var updatedEntries = TryApplyJournalChanges(
            accessor,
            identity,
            volumeEntries,
            checkpoint!.NextUsn!.Value,
            journal!.NextUsn,
            journal.JournalId,
            cancellationToken,
            out var nextUsn,
            out var metadataFailureCount);
        if (updatedEntries is null)
        {
            return null;
        }

        accessor.EnsureIdentityUnchanged();
        var endingJournal = accessor.TryReadJournalState();
        if (endingJournal is null
            || endingJournal.JournalId != journal.JournalId
            || nextUsn < endingJournal.LowestValidUsn)
        {
            return null;
        }

        var warnings = new List<string>();
        if (metadataFailureCount > 0)
        {
            AddWarning(
                warnings,
                $"{identity.RootPath}: {metadataFailureCount:N0}개 변경 항목의 메타데이터를 읽지 못했습니다.");
        }

        var updatedCheckpoint = new FileIndexCheckpoint(
            identity.RootPath,
            identity.VolumeId,
            identity.FileSystem,
            journal.JournalId,
            nextUsn,
            timeProvider.GetUtcNow(),
            identity.SerialNumber);
        return new FileIndexBuildResult(updatedEntries, [updatedCheckpoint], warnings);
    }

    private static bool IsUsableCheckpoint(
        FileIndexCheckpoint? checkpoint,
        NtfsVolumeIdentity identity,
        NtfsJournalState? journal) =>
        checkpoint is not null
        && journal is not null
        && string.Equals(checkpoint.VolumeId, identity.VolumeId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(checkpoint.FileSystem, identity.FileSystem, StringComparison.OrdinalIgnoreCase)
        && checkpoint.VolumeSerialNumber == identity.SerialNumber
        && checkpoint.JournalId == journal.JournalId
        && checkpoint.NextUsn is { } nextUsn
        && nextUsn >= journal.LowestValidUsn
        && nextUsn <= journal.NextUsn;

    private static IReadOnlyList<FileIndexEntry>? TryApplyJournalChanges(
        INtfsVolumeAccessor accessor,
        NtfsVolumeIdentity identity,
        IReadOnlyList<FileIndexEntry> startingEntries,
        long startUsn,
        long targetUsn,
        ulong journalId,
        CancellationToken cancellationToken,
        out long nextUsn,
        out long metadataFailureCount)
    {
        Dictionary<FileReferenceId, List<FileIndexEntry>> entriesById;
        try
        {
            entriesById = CreateIdMap(startingEntries);
        }
        catch (InvalidDataException)
        {
            nextUsn = startUsn;
            metadataFailureCount = 0;
            return null;
        }

        var index = new FileSearchIndex();
        index.Replace(startingEntries);
        var mutations = ReadJournalMutations(
            accessor,
            startUsn,
            targetUsn,
            journalId,
            cancellationToken,
            out nextUsn);
        metadataFailureCount = 0;

        foreach (var mutation in mutations.OrderBy(item => item.Value.LastUsn))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = mutation.Key;
            if (id == identity.RootFileReferenceNumber)
            {
                continue;
            }

            var current = accessor.TryReadCurrentEntries(id).ToList();
            entriesById.TryGetValue(id, out var existingValues);
            var existing = existingValues?.ToList() ?? [];

            if (current.Count == 0)
            {
                if ((mutation.Value.Reasons & UsnReasonFileDelete) != 0)
                {
                    foreach (var deleted in existing)
                    {
                        index.Apply([FileIndexChange.Delete(deleted.FullPath)]);
                        RemovePathFromIdMap(entriesById, deleted.FullPath);
                    }

                    continue;
                }

                if (mutation.Value.SawOldName && !mutation.Value.SawNewName)
                {
                    return null;
                }

                var reconstructed = TryCreateEntryFromJournal(
                    accessor,
                    identity,
                    mutation.Value.LastRecord,
                    entriesById,
                    ref metadataFailureCount);
                if (reconstructed is null)
                {
                    return null;
                }

                current.Add(reconstructed);
            }

            if (existing.Count == 1
                && current.Count == 1
                && existing[0].Kind == SearchItemKind.Folder
                && current[0].Kind == SearchItemKind.Folder)
            {
                if (!string.Equals(existing[0].FullPath, current[0].FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    index.Apply([FileIndexChange.Rename(existing[0].FullPath, current[0].FullPath)]);
                    RenamePathInIdMap(entriesById, existing[0].FullPath, current[0].FullPath);
                }

                index.Apply([FileIndexChange.Upsert(current[0])]);
                entriesById[id] = current;
                continue;
            }

            foreach (var removed in existing.Where(oldEntry =>
                         !current.Any(currentEntry => string.Equals(
                             currentEntry.FullPath,
                             oldEntry.FullPath,
                             StringComparison.OrdinalIgnoreCase))))
            {
                index.Apply([FileIndexChange.Delete(removed.FullPath)]);
            }

            foreach (var currentEntry in current)
            {
                index.Apply([FileIndexChange.Upsert(currentEntry)]);
            }

            entriesById[id] = current;
        }

        return index.Snapshot();
    }

    private static void RemovePathFromIdMap(
        Dictionary<FileReferenceId, List<FileIndexEntry>> entriesById,
        string path)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = normalized + Path.DirectorySeparatorChar;
        foreach (var item in entriesById.ToList())
        {
            item.Value.RemoveAll(entry =>
                string.Equals(entry.FullPath, normalized, StringComparison.OrdinalIgnoreCase)
                || entry.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (item.Value.Count == 0)
            {
                entriesById.Remove(item.Key);
            }
        }
    }

    private static void RenamePathInIdMap(
        Dictionary<FileReferenceId, List<FileIndexEntry>> entriesById,
        string oldPath,
        string newPath)
    {
        var oldNormalized = oldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var newNormalized = newPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = oldNormalized + Path.DirectorySeparatorChar;
        foreach (var item in entriesById)
        {
            for (var index = 0; index < item.Value.Count; index++)
            {
                var entry = item.Value[index];
                if (!string.Equals(entry.FullPath, oldNormalized, StringComparison.OrdinalIgnoreCase)
                    && !entry.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = entry.FullPath.Length == oldNormalized.Length
                    ? string.Empty
                    : entry.FullPath[oldNormalized.Length..];
                var movedPath = newNormalized + suffix;
                item.Value[index] = entry with
                {
                    FullPath = movedPath,
                    Name = Path.GetFileName(movedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    ParentPath = Path.GetDirectoryName(movedPath) ?? string.Empty
                };
            }
        }
    }

    private static Dictionary<FileReferenceId, JournalMutation> ReadJournalMutations(
        INtfsVolumeAccessor accessor,
        long startUsn,
        long targetUsn,
        ulong journalId,
        CancellationToken cancellationToken,
        out long nextUsn)
    {
        var mutations = new Dictionary<FileReferenceId, JournalMutation>();
        var cursor = startUsn;
        var lastRecordUsn = startUsn;

        while (cursor < targetUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = accessor.ReadUsnJournalBatch(cursor, journalId, cancellationToken);
            if (batch.NextUsn <= cursor)
            {
                throw new InvalidDataException("NTFS 변경 저널 위치가 앞으로 진행되지 않았습니다.");
            }

            foreach (var record in batch.Records)
            {
                if (record.Usn < cursor || record.Usn < lastRecordUsn || record.Usn >= batch.NextUsn)
                {
                    throw new InvalidDataException("NTFS 변경 저널 레코드 순서가 올바르지 않습니다.");
                }

                lastRecordUsn = record.Usn;
                if (!mutations.TryGetValue(record.FileReferenceId, out var mutation))
                {
                    mutation = new JournalMutation(record);
                    mutations.Add(record.FileReferenceId, mutation);
                }
                else
                {
                    mutation.Add(record);
                }
            }

            cursor = batch.NextUsn;
        }

        nextUsn = cursor;
        return mutations;
    }

    private static FileIndexEntry? TryCreateEntryFromJournal(
        INtfsVolumeAccessor accessor,
        NtfsVolumeIdentity identity,
        NtfsMftRecord record,
        IReadOnlyDictionary<FileReferenceId, List<FileIndexEntry>> entriesById,
        ref long metadataFailureCount)
    {
        string parentPath;
        if (record.ParentFileReferenceId == identity.RootFileReferenceNumber)
        {
            parentPath = identity.RootPath;
        }
        else if (entriesById.TryGetValue(record.ParentFileReferenceId, out var parents)
                 && parents.FirstOrDefault(parent => parent.Kind == SearchItemKind.Folder) is { } parent)
        {
            parentPath = parent.FullPath;
        }
        else
        {
            return null;
        }

        var isDirectory = record.Attributes.HasFlag(FileAttributes.Directory);
        var metadata = accessor.TryReadMetadata(record.FileReferenceId, isDirectory);
        if (metadata is null)
        {
            metadataFailureCount++;
        }

        var fullPath = Path.Combine(parentPath, record.Name);
        return new FileIndexEntry(
            fullPath,
            record.Name,
            parentPath,
            isDirectory ? SearchItemKind.Folder : SearchItemKind.File,
            isDirectory ? null : metadata?.Size,
            metadata?.ModifiedAt,
            identity.VolumeId,
            record.FileReferenceId);
    }

    private static Dictionary<FileReferenceId, List<FileIndexEntry>> CreateIdMap(
        IReadOnlyList<FileIndexEntry> entries)
    {
        var result = new Dictionary<FileReferenceId, List<FileIndexEntry>>();
        foreach (var entry in entries)
        {
            if (entry.FileReferenceNumber is not { } id)
            {
                throw new InvalidDataException("증분 NTFS 인덱스에 파일 ID가 없습니다.");
            }

            if (!result.TryGetValue(id, out var paths))
            {
                paths = [];
                result.Add(id, paths);
            }

            if (paths.Any(existing => string.Equals(
                    existing.FullPath,
                    entry.FullPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("증분 NTFS 인덱스에 중복 경로가 있습니다.");
            }

            paths.Add(entry);
        }

        return result;
    }

    private static IReadOnlyList<NtfsMftRecord> ReadAllRecords(
        INtfsVolumeAccessor accessor,
        CancellationToken cancellationToken)
    {
        var records = new List<NtfsMftRecord>();
        var seen = new HashSet<FileReferenceId>();
        ulong cursor = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = accessor.ReadMftBatch(cursor, cancellationToken);
            if (batch is null)
            {
                break;
            }

            if (batch.NextFileReferenceNumber <= cursor)
            {
                throw new InvalidDataException("NTFS MFT 열거 위치가 앞으로 진행되지 않았습니다.");
            }

            foreach (var record in batch.Records)
            {
                if (!seen.Add(record.FileReferenceId))
                {
                    throw new InvalidDataException(
                        $"NTFS MFT에 중복 파일 참조 번호가 있습니다: {record.FileReferenceId}");
                }

                records.Add(record);
            }

            cursor = batch.NextFileReferenceNumber;
        }

        return records;
    }

    private static void AddWarnings(
        List<string> destination,
        string rootPath,
        IReadOnlyList<string> source)
    {
        foreach (var warning in source)
        {
            AddWarning(destination, $"{rootPath}: {warning}");
        }
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaximumWarnings)
        {
            warnings.Add(warning);
        }
    }

    private sealed class JournalMutation
    {
        public JournalMutation(NtfsMftRecord record)
        {
            LastRecord = record;
            Add(record);
        }

        public NtfsMftRecord LastRecord { get; private set; }

        public long LastUsn { get; private set; }

        public uint Reasons { get; private set; }

        public bool SawOldName { get; private set; }

        public bool SawNewName { get; private set; }

        public void Add(NtfsMftRecord record)
        {
            LastRecord = record;
            LastUsn = record.Usn;
            Reasons |= record.Reason;
            SawOldName |= (record.Reason & UsnReasonRenameOldName) != 0;
            SawNewName |= (record.Reason & UsnReasonRenameNewName) != 0;
        }
    }
}
