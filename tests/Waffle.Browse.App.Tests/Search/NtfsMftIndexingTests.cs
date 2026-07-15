using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Waffle.Browse.App.Search.Indexing;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Tests.Search;

internal static class NtfsMftIndexingTests
{
    private const string VolumeRoot = @"X:\";

    public static void ParserReadsVersion2UnicodeName()
    {
        var record = CreateVersion2Record(
            fileReference: 0x1122334455667788,
            parentReference: 0x8877665544332211,
            name: "문서😀.txt",
            attributes: (uint)FileAttributes.Archive,
            usn: 12345);

        var batch = NtfsUsnRecordParser.ParseBatch(CreateBatch(99, record));
        var parsed = Single(batch.Records);

        Equal(99UL, batch.NextFileReferenceNumber, "The batch continuation should be preserved.");
        Equal(new NtfsFileReference(0x1122334455667788), parsed.FileReference, "The v2 file reference should be read as 64-bit.");
        Equal(new NtfsFileReference(0x8877665544332211), parsed.ParentFileReference, "The v2 parent reference should be read as 64-bit.");
        Equal("문서😀.txt", parsed.Name, "The UTF-16 v2 name, including a surrogate pair, should round-trip.");
        Equal(12345L, parsed.Usn, "The v2 USN should be read from its fixed offset.");
        Equal((ushort)2, parsed.MajorVersion, "The v2 major version should be retained.");
    }

    public static void ParserReadsVersion3HighFileReferenceBits()
    {
        var record = CreateVersion3Record(
            fileReferenceLow: 0x0123456789ABCDEF,
            fileReferenceHigh: 0xFEDCBA9876543210,
            parentReferenceLow: 0x1111222233334444,
            parentReferenceHigh: 0xAAAABBBBCCCCDDDD,
            name: "wide.bin",
            attributes: 0,
            usn: 987654321);

        var parsed = Single(NtfsUsnRecordParser.ParseBatch(CreateBatch(100, record)).Records);

        Equal(
            new NtfsFileReference(0x0123456789ABCDEF, 0xFEDCBA9876543210),
            parsed.FileReference,
            "The upper 64 bits of a v3 file reference must not be truncated.");
        Equal(
            new NtfsFileReference(0x1111222233334444, 0xAAAABBBBCCCCDDDD),
            parsed.ParentFileReference,
            "The upper 64 bits of a v3 parent reference must not be truncated.");
        Equal((ushort)3, parsed.MajorVersion, "The v3 major version should be retained.");
    }

    public static void ParserReadsMixedVersionBatch()
    {
        var version2 = CreateVersion2Record(10, 1, "two.txt", 0, 20);
        var version3 = CreateVersion3Record(30, 40, 1, 0, "three.txt", 0, 50);

        var batch = NtfsUsnRecordParser.ParseBatch(CreateBatch(500, version2, version3));

        Equal(2, batch.Records.Count, "Both records in a mixed batch should be parsed.");
        Equal((ushort)2, batch.Records[0].MajorVersion, "The first mixed record should use the v2 layout.");
        Equal("two.txt", batch.Records[0].Name, "The first mixed record should retain its name.");
        Equal((ushort)3, batch.Records[1].MajorVersion, "The second mixed record should use the v3 layout.");
        Equal(new NtfsFileReference(30, 40), batch.Records[1].FileReference, "The mixed v3 ID should retain both halves.");
    }

    public static void ParserAcceptsFutureMinorVersionAndExtendedNameOffset()
    {
        const int extendedNameOffset = 72;
        var record = CreateVersion2Record(
            fileReference: 5,
            parentReference: 1,
            name: "future.txt",
            attributes: 0,
            usn: 6,
            minorVersion: 7,
            nameOffset: extendedNameOffset);

        var parsed = Single(NtfsUsnRecordParser.ParseBatch(CreateBatch(6, record)).Records);

        Equal("future.txt", parsed.Name, "A future minor version should use its runtime FileNameOffset.");
        Equal((ushort)2, parsed.MajorVersion, "A future minor version should remain compatible with its known major layout.");
    }

    public static void ParserRejectsTruncatedBuffers()
    {
        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(new byte[7]),
            "A batch shorter than its continuation value should fail.");

        var complete = CreateBatch(2, CreateVersion2Record(2, 1, "cut.txt", 0, 3));
        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(complete[..^1]),
            "A record whose declared length extends past the batch should fail.");
    }

    public static void ParserRejectsZeroRecordLength()
    {
        var record = new byte[NtfsUsnRecordParser.CommonHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);

        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(2, record)),
            "A zero record length must fail instead of stalling the parser.");
    }

    public static void ParserRejectsMisalignedRecordLength()
    {
        var record = new byte[68];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);

        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(2, record)),
            "A USN record length that is not eight-byte aligned should fail.");
    }

    public static void ParserRejectsUnsupportedMajorVersion()
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 4);

        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(2, record)),
            "An unknown major layout must fail the complete batch.");
    }

    public static void ParserRejectsOddUnicodeNameLength()
    {
        var record = CreateVersion2Record(2, 1, "odd.txt", 0, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), 3);

        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(3, record)),
            "A UTF-16 name with an odd byte length should fail.");
    }

    public static void ParserRejectsInvalidUnicodeNameRanges()
    {
        var beforeHeader = CreateVersion2Record(2, 1, "a", 0, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(beforeHeader.AsSpan(58), 58);
        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(3, beforeHeader)),
            "A FileNameOffset inside the fixed header should fail.");

        var beyondRecord = CreateVersion2Record(2, 1, "a", 0, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(beyondRecord.AsSpan(58), checked((ushort)(beyondRecord.Length + 2)));
        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(3, beyondRecord)),
            "A FileNameOffset beyond RecordLength should fail.");

        var lengthPastRecord = CreateVersion2Record(2, 1, "a", 0, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(lengthPastRecord.AsSpan(56), 10);
        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(3, lengthPastRecord)),
            "A FileNameLength beyond the remaining record should fail.");

        var oddOffset = CreateVersion2Record(2, 1, "a", 0, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(oddOffset.AsSpan(58), 61);
        InvalidData(
            () => NtfsUsnRecordParser.ParseBatch(CreateBatch(3, oddOffset)),
            "An odd UTF-16 FileNameOffset should fail.");
    }

    public static void PathGraphBuildsRootFolderFileChain()
    {
        var root = new NtfsFileReference(1);
        var folder = new NtfsFileReference(2);
        var file = new NtfsFileReference(3, 4);
        var modifiedAt = new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero);
        var records = new Dictionary<NtfsFileReference, NtfsUsnRecord>
        {
            [root] = Record(root, root, "root", isDirectory: true),
            [folder] = Record(folder, root, "Folder", isDirectory: true),
            [file] = Record(file, folder, "문서.txt", isDirectory: false, majorVersion: 3)
        };

        var result = NtfsPathGraphBuilder.Build(
            VolumeRoot,
            [VolumeRoot],
            "volume-id",
            records,
            root,
            record => record.IsDirectory
                ? new NtfsFileMetadata(null, modifiedAt)
                : new NtfsFileMetadata(42, modifiedAt));

        Equal(2, result.Entries.Count, "The root should be omitted while its descendants are indexed.");
        Equal(0L, result.QuarantinedRecordCount, "A valid chain should not quarantine records.");
        var folderEntry = result.Entries.Single(entry => entry.Name == "Folder");
        Equal(SearchItemKind.Folder, folderEntry.Kind, "The directory attribute should produce a folder entry.");
        var fileEntry = result.Entries.Single(entry => entry.Name == "문서.txt");
        Equal(Path.Combine(VolumeRoot, "Folder", "문서.txt"), fileEntry.FullPath, "The graph should compose the complete path.");
        Equal(42L, fileEntry.Size, "File metadata should be retained.");
        Equal(modifiedAt, fileEntry.ModifiedAt, "The metadata timestamp should be retained.");
        Equal(new Waffle.Browse.Core.Search.Indexing.FileIndexFileReference(3, 4), fileEntry.FileReferenceNumber, "Both file reference halves should reach the index entry.");
    }

    public static void PathGraphQuarantinesMissingParent()
    {
        var root = new NtfsFileReference(1);
        var orphan = new NtfsFileReference(2);
        var records = new Dictionary<NtfsFileReference, NtfsUsnRecord>
        {
            [root] = Record(root, root, "root", isDirectory: true),
            [orphan] = Record(orphan, new NtfsFileReference(999), "orphan.txt", isDirectory: false)
        };

        var result = NtfsPathGraphBuilder.Build(
            VolumeRoot,
            [VolumeRoot],
            "volume-id",
            records,
            root,
            _ => null);

        Equal(0, result.Entries.Count, "An orphan should not be published.");
        Equal(1L, result.QuarantinedRecordCount, "The orphan should be counted once.");
        True(result.Warnings.Any(warning => warning.Contains("레코드가 없습니다", StringComparison.Ordinal)), "The warning should identify a missing parent record.");
    }

    public static void PathGraphQuarantinesSelfAndMultiNodeCycles()
    {
        var root = new NtfsFileReference(1);
        var self = new NtfsFileReference(2);
        var first = new NtfsFileReference(3);
        var second = new NtfsFileReference(4);
        var records = new Dictionary<NtfsFileReference, NtfsUsnRecord>
        {
            [root] = Record(root, root, "root", isDirectory: true),
            [self] = Record(self, self, "self", isDirectory: true),
            [first] = Record(first, second, "first", isDirectory: true),
            [second] = Record(second, first, "second", isDirectory: true)
        };

        var result = NtfsPathGraphBuilder.Build(
            VolumeRoot,
            [VolumeRoot],
            "volume-id",
            records,
            root,
            _ => null);

        Equal(0, result.Entries.Count, "Cyclic descendants should not be published.");
        Equal(3L, result.QuarantinedRecordCount, "The self-cycle and both multi-node cycle members should be counted.");
        Equal(3, result.Warnings.Count, "Each cyclic record should produce one sampled warning.");
        True(result.Warnings.All(warning => warning.Contains("순환", StringComparison.Ordinal)), "Cycle warnings should state the graph failure.");
    }

    public static void PathGraphKeepsEntryWhenMetadataReadFails()
    {
        var root = new NtfsFileReference(1);
        var file = new NtfsFileReference(2);
        var records = new Dictionary<NtfsFileReference, NtfsUsnRecord>
        {
            [root] = Record(root, root, "root", isDirectory: true),
            [file] = Record(file, root, "locked.txt", isDirectory: false)
        };

        var result = NtfsPathGraphBuilder.Build(
            VolumeRoot,
            [VolumeRoot],
            "volume-id",
            records,
            root,
            _ => throw new UnauthorizedAccessException("test denial"));

        var entry = Single(result.Entries);
        Equal("locked.txt", entry.Name, "A metadata failure must not remove the searchable entry.");
        Equal<long?>(null, entry.Size, "A metadata failure should leave size null.");
        Equal<DateTimeOffset?>(null, entry.ModifiedAt, "A metadata failure should leave modified time null.");
    }

    public static void PathGraphRejectsMissingRootAnchor()
    {
        var file = new NtfsFileReference(2);
        var records = new Dictionary<NtfsFileReference, NtfsUsnRecord>
        {
            [file] = Record(file, new NtfsFileReference(999), "orphan.txt", isDirectory: false)
        };

        InvalidData(
            () => NtfsPathGraphBuilder.Build(VolumeRoot, [VolumeRoot], "volume-id", records, null, _ => null),
            "A volume generation without a root anchor must fail instead of publishing an empty snapshot.");
    }

    public static void PathGraphQuarantinesNonDirectoryParent()
    {
        var root = new NtfsFileReference(1);
        var fileParent = new NtfsFileReference(2);
        var child = new NtfsFileReference(3);
        var records = new Dictionary<NtfsFileReference, NtfsUsnRecord>
        {
            [root] = Record(root, root, "root", isDirectory: true),
            [fileParent] = Record(fileParent, root, "parent.txt", isDirectory: false),
            [child] = Record(child, fileParent, "child.txt", isDirectory: false)
        };

        var result = NtfsPathGraphBuilder.Build(
            VolumeRoot,
            [VolumeRoot],
            "volume-id",
            records,
            root,
            _ => null);

        Equal(1, result.Entries.Count, "The valid file parent itself should remain searchable.");
        Equal("parent.txt", Single(result.Entries).Name, "Only the valid file should be published.");
        Equal(1L, result.QuarantinedRecordCount, "A child whose parent is not a directory should be quarantined.");
        True(result.Warnings.Any(warning => warning.Contains("디렉터리가 아닙니다", StringComparison.Ordinal)), "The warning should identify the invalid parent kind.");
    }

    public static void NativeInteropStructsHaveStableTwentyFourByteLayout()
    {
        Equal(24, Marshal.SizeOf<NtfsVolumeSession.MftEnumDataV0>(), "MFT_ENUM_DATA_V0 must be 24 bytes.");
        Offset<NtfsVolumeSession.MftEnumDataV0>(nameof(NtfsVolumeSession.MftEnumDataV0.StartFileReferenceNumber), 0);
        Offset<NtfsVolumeSession.MftEnumDataV0>(nameof(NtfsVolumeSession.MftEnumDataV0.LowUsn), 8);
        Offset<NtfsVolumeSession.MftEnumDataV0>(nameof(NtfsVolumeSession.MftEnumDataV0.HighUsn), 16);

        Equal(24, Marshal.SizeOf<NtfsVolumeSession.FileIdDescriptor>(), "FILE_ID_DESCRIPTOR must be 24 bytes.");
        Offset<NtfsVolumeSession.FileIdDescriptor>(nameof(NtfsVolumeSession.FileIdDescriptor.Size), 0);
        Offset<NtfsVolumeSession.FileIdDescriptor>(nameof(NtfsVolumeSession.FileIdDescriptor.Type), 4);
        Offset<NtfsVolumeSession.FileIdDescriptor>(nameof(NtfsVolumeSession.FileIdDescriptor.Low), 8);
        Offset<NtfsVolumeSession.FileIdDescriptor>(nameof(NtfsVolumeSession.FileIdDescriptor.High), 16);

        var descriptor = NtfsVolumeSession.FileIdDescriptor.Create(new NtfsFileReference(11, 22), extended: true);
        Equal(24U, descriptor.Size, "The native descriptor should report its complete size.");
        Equal(NtfsVolumeSession.FileIdType.ExtendedFileId, descriptor.Type, "A v3 descriptor should select ExtendedFileId.");
        Equal(11UL, descriptor.Low, "The descriptor low half should be retained.");
        Equal(22UL, descriptor.High, "The descriptor high half should be retained.");
    }

    private static NtfsUsnRecord Record(
        NtfsFileReference fileReference,
        NtfsFileReference parentReference,
        string name,
        bool isDirectory,
        ushort majorVersion = 2) =>
        new(
            fileReference,
            parentReference,
            name,
            isDirectory ? (uint)FileAttributes.Directory : 0,
            Usn: (long)fileReference.Low,
            majorVersion);

    private static byte[] CreateVersion2Record(
        ulong fileReference,
        ulong parentReference,
        string name,
        uint attributes,
        long usn,
        ushort minorVersion = 0,
        int nameOffset = NtfsUsnRecordParser.Version2MinimumRecordSize)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var record = new byte[AlignToEight(nameOffset + nameBytes.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), minorVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), fileReference);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), parentReference);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), checked((ushort)nameBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), checked((ushort)nameOffset));
        nameBytes.CopyTo(record.AsSpan(nameOffset));
        return record;
    }

    private static byte[] CreateVersion3Record(
        ulong fileReferenceLow,
        ulong fileReferenceHigh,
        ulong parentReferenceLow,
        ulong parentReferenceHigh,
        string name,
        uint attributes,
        long usn)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var nameOffset = NtfsUsnRecordParser.Version3MinimumRecordSize;
        var record = new byte[AlignToEight(nameOffset + nameBytes.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), fileReferenceLow);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), fileReferenceHigh);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), parentReferenceLow);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), parentReferenceHigh);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(68), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(72), checked((ushort)nameBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(74), checked((ushort)nameOffset));
        nameBytes.CopyTo(record.AsSpan(nameOffset));
        return record;
    }

    private static byte[] CreateBatch(ulong continuation, params byte[][] records)
    {
        var batch = new byte[NtfsUsnRecordParser.ContinuationSize + records.Sum(record => record.Length)];
        BinaryPrimitives.WriteUInt64LittleEndian(batch, continuation);
        var offset = NtfsUsnRecordParser.ContinuationSize;
        foreach (var record in records)
        {
            record.CopyTo(batch, offset);
            offset += record.Length;
        }

        return batch;
    }

    private static int AlignToEight(int value) => (value + 7) & ~7;

    private static T Single<T>(IReadOnlyList<T> values)
    {
        Equal(1, values.Count, "Expected exactly one item.");
        return values[0];
    }

    private static void InvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Offset<T>(string fieldName, int expected)
        where T : struct =>
        Equal(expected, Marshal.OffsetOf<T>(fieldName).ToInt32(), $"Unexpected native offset for {typeof(T).Name}.{fieldName}.");

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
}
