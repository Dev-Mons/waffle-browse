using System.IO;
using Waffle.Browse.Core.Search;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.App.Search.Indexing;

internal readonly record struct NtfsFileMetadata(long? Size, DateTimeOffset? ModifiedAt);

internal sealed record NtfsPathGraphResult(
    IReadOnlyList<FileIndexEntry> Entries,
    IReadOnlyList<string> Warnings,
    long QuarantinedRecordCount);

internal static class NtfsPathGraphBuilder
{
    private const int MaxWarnings = 100;

    public static NtfsPathGraphResult Build(
        string volumeRoot,
        IReadOnlyList<string> configuredRoots,
        string volumeId,
        IReadOnlyDictionary<NtfsFileReference, NtfsUsnRecord> records,
        NtfsFileReference? rootFileReference,
        Func<NtfsUsnRecord, NtfsFileMetadata?> metadataReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        ArgumentNullException.ThrowIfNull(configuredRoots);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(metadataReader);

        var normalizedVolumeRoot = NormalizeRoot(volumeRoot);
        var scopes = configuredRoots
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolvedPaths = new Dictionary<NtfsFileReference, string>();
        var invalidRecords = new Dictionary<NtfsFileReference, string>();

        var rootReference = rootFileReference is { } suppliedRoot
            && (records.ContainsKey(suppliedRoot)
                || records.Values.Any(record => record.ParentFileReference == suppliedRoot))
            ? suppliedRoot
            : records.Values
                .Where(record => record.IsDirectory && record.FileReference == record.ParentFileReference)
                .Select(record => (NtfsFileReference?)record.FileReference)
                .FirstOrDefault();
        if (rootReference is null)
        {
            throw new InvalidDataException("NTFS MFT 레코드에서 볼륨 루트 FRN을 확인할 수 없습니다.");
        }

        if (rootReference is { } root)
        {
            resolvedPaths[root] = normalizedVolumeRoot;
        }

        foreach (var fileReference in records.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvePath(fileReference, normalizedVolumeRoot, records, resolvedPaths, invalidRecords, rootReference);
        }

        var entries = new List<FileIndexEntry>(Math.Max(0, records.Count - 1));
        foreach (var (fileReference, record) in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolvedPaths.TryGetValue(fileReference, out var fullPath)
                || (rootReference is { } graphRoot && fileReference == graphRoot)
                || !scopes.Any(scope => IsDescendantOf(fullPath, scope)))
            {
                continue;
            }

            NtfsFileMetadata? metadata = null;
            try
            {
                metadata = metadataReader(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            entries.Add(new FileIndexEntry(
                fullPath,
                record.Name,
                Path.GetDirectoryName(fullPath) ?? string.Empty,
                record.IsDirectory ? SearchItemKind.Folder : SearchItemKind.File,
                record.IsDirectory ? null : metadata?.Size,
                metadata?.ModifiedAt,
                volumeId,
                new FileIndexFileReference(fileReference.Low, fileReference.High)));
        }

        var warnings = invalidRecords
            .Take(MaxWarnings)
            .Select(item => $"FRN {FormatReference(item.Key)} 격리: {item.Value}")
            .ToList();
        return new NtfsPathGraphResult(entries, warnings, invalidRecords.Count);
    }

    private static string? ResolvePath(
        NtfsFileReference requestedReference,
        string volumeRoot,
        IReadOnlyDictionary<NtfsFileReference, NtfsUsnRecord> records,
        Dictionary<NtfsFileReference, string> resolvedPaths,
        Dictionary<NtfsFileReference, string> invalidRecords,
        NtfsFileReference? rootReference)
    {
        if (resolvedPaths.TryGetValue(requestedReference, out var cachedPath))
        {
            return cachedPath;
        }

        if (invalidRecords.ContainsKey(requestedReference))
        {
            return null;
        }

        var chain = new List<NtfsFileReference>();
        var positions = new Dictionary<NtfsFileReference, int>();
        var current = requestedReference;
        string? basePath = null;
        while (true)
        {
            if (resolvedPaths.TryGetValue(current, out basePath))
            {
                break;
            }

            if (invalidRecords.TryGetValue(current, out var parentFailure))
            {
                MarkInvalid(chain, invalidRecords, $"부모 경로를 해석할 수 없습니다 ({parentFailure}).");
                return null;
            }

            if (positions.TryGetValue(current, out var cycleStart))
            {
                for (var index = cycleStart; index < chain.Count; index++)
                {
                    invalidRecords.TryAdd(chain[index], "부모 FRN 그래프에 순환이 있습니다.");
                }

                for (var index = 0; index < cycleStart; index++)
                {
                    invalidRecords.TryAdd(chain[index], "순환하는 부모 경로를 참조합니다.");
                }

                return null;
            }

            if (!records.TryGetValue(current, out var record))
            {
                MarkInvalid(chain, invalidRecords, $"부모 FRN {FormatReference(current)} 레코드가 없습니다.");
                return null;
            }

            if (rootReference is null && record.IsDirectory && record.FileReference == record.ParentFileReference)
            {
                resolvedPaths[record.FileReference] = volumeRoot;
                basePath = volumeRoot;
                break;
            }

            if (!IsValidName(record.Name))
            {
                invalidRecords.TryAdd(current, "파일 이름이 비어 있거나 경로 구분자를 포함합니다.");
                MarkInvalid(chain, invalidRecords, "잘못된 이름을 가진 부모 경로를 참조합니다.");
                return null;
            }

            if (record.ParentFileReference != record.FileReference
                && records.TryGetValue(record.ParentFileReference, out var parentRecord)
                && !parentRecord.IsDirectory)
            {
                invalidRecords.TryAdd(current, "부모 FRN이 디렉터리가 아닙니다.");
                MarkInvalid(chain, invalidRecords, "디렉터리가 아닌 부모 경로를 참조합니다.");
                return null;
            }

            positions[current] = chain.Count;
            chain.Add(current);
            current = record.ParentFileReference;
        }

        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var fileReference = chain[index];
            var record = records[fileReference];
            try
            {
                basePath = Path.Combine(basePath!, record.Name);
                resolvedPaths[fileReference] = basePath;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                for (var unresolved = index; unresolved >= 0; unresolved--)
                {
                    invalidRecords.TryAdd(chain[unresolved], $"전체 경로를 만들 수 없습니다: {ex.Message}");
                }

                return null;
            }
        }

        return resolvedPaths.GetValueOrDefault(requestedReference);
    }

    private static void MarkInvalid(
        IEnumerable<NtfsFileReference> references,
        IDictionary<NtfsFileReference, string> invalidRecords,
        string reason)
    {
        foreach (var reference in references)
        {
            invalidRecords.TryAdd(reference, reason);
        }
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrEmpty(name)
        && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsDescendantOf(string path, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string FormatReference(NtfsFileReference reference) =>
        reference.High == 0
            ? $"0x{reference.Low:X16}"
            : $"0x{reference.High:X16}{reference.Low:X16}";
}
