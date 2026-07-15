namespace Waffle.Browse.Core.Search.Indexing.Ntfs;

internal static class NtfsPathResolver
{
    private const int MaximumWarnings = 100;

    public static NtfsPathResolutionResult Resolve(
        string rootPath,
        FileReferenceId rootFileReferenceId,
        IReadOnlyList<NtfsMftRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(records);

        var normalizedRoot = NormalizeDriveRoot(rootPath);
        var recordsById = new Dictionary<FileReferenceId, NtfsMftRecord>();
        foreach (var record in records)
        {
            if (record is null)
            {
                throw new InvalidDataException("The MFT record collection contains a null record.");
            }

            if (!recordsById.TryAdd(record.FileReferenceId, record))
            {
                throw new InvalidDataException($"The MFT contains duplicate file reference ID {record.FileReferenceId}.");
            }
        }

        var resolved = new Dictionary<FileReferenceId, ResolvedNode>
        {
            [rootFileReferenceId] = new(normalizedRoot, string.Empty)
        };
        var skippedIds = new HashSet<FileReferenceId>();
        var warnings = new List<string>();
        long skippedPathCount = 0;

        void Skip(IEnumerable<FileReferenceId> ids, string reason)
        {
            foreach (var id in ids)
            {
                if (id == rootFileReferenceId || !skippedIds.Add(id))
                {
                    continue;
                }

                skippedPathCount++;
                if (warnings.Count < MaximumWarnings)
                {
                    var name = recordsById.TryGetValue(id, out var record) ? record.Name : string.Empty;
                    warnings.Add($"{id} ({name}): {reason}");
                }
            }
        }

        var chain = new List<FileReferenceId>();
        var chainPositions = new Dictionary<FileReferenceId, int>();
        foreach (var record in records)
        {
            var startId = record.FileReferenceId;
            if (startId == rootFileReferenceId || resolved.ContainsKey(startId) || skippedIds.Contains(startId))
            {
                continue;
            }

            chain.Clear();
            chainPositions.Clear();
            var currentId = startId;
            string? failure = null;

            while (!resolved.ContainsKey(currentId) && !skippedIds.Contains(currentId))
            {
                if (!recordsById.TryGetValue(currentId, out var currentRecord))
                {
                    failure = $"parent file reference ID {currentId} is missing";
                    break;
                }

                if (chainPositions.TryGetValue(currentId, out var cycleStart))
                {
                    var cycle = string.Join(" -> ", chain.Skip(cycleStart).Append(currentId));
                    failure = $"file reference cycle detected: {cycle}";
                    break;
                }

                chainPositions.Add(currentId, chain.Count);
                chain.Add(currentId);
                currentId = currentRecord.ParentFileReferenceId;
            }

            if (failure is not null)
            {
                Skip(chain, failure);
                continue;
            }

            if (skippedIds.Contains(currentId))
            {
                Skip(chain, $"parent component rooted at {currentId} could not be resolved");
                continue;
            }

            var parent = resolved[currentId];
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var id = chain[index];
                var currentRecord = recordsById[id];
                var fullPath = Path.Combine(parent.FullPath, currentRecord.Name);
                resolved.Add(id, new ResolvedNode(fullPath, parent.FullPath));
                parent = resolved[id];
            }
        }

        var entries = new List<NtfsResolvedPath>(records.Count);
        foreach (var record in records)
        {
            if (record.FileReferenceId == rootFileReferenceId
                || !resolved.TryGetValue(record.FileReferenceId, out var path))
            {
                continue;
            }

            entries.Add(new NtfsResolvedPath(record, path.FullPath, path.ParentPath));
        }

        return new NtfsPathResolutionResult(entries, warnings, skippedPathCount);
    }

    private static string NormalizeDriveRoot(string rootPath)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot)
            || !string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An NTFS MFT path resolver root must be a drive root.", nameof(rootPath));
        }

        return pathRoot;
    }

    private sealed record ResolvedNode(string FullPath, string ParentPath);
}
