using System.Buffers.Binary;
using System.Text;
using Waffle.Browse.Core.Search.Indexing;
using Waffle.Browse.Core.Search.Indexing.Ntfs;

namespace Waffle.Browse.Core.Tests.Search;

internal static class NtfsUsnRecordParserTests
{
    public static void ParsesContinuationAndV2Records()
    {
        const long usn = 4567;
        const uint reason = 0x0000_0100;
        var folder = BuildV2Record(
            new FileReferenceId(20),
            new FileReferenceId(5),
            "Reports",
            FileAttributes.Directory,
            nameOffset: 64,
            usn: usn,
            reason: reason);
        var file = BuildV2Record(
            new FileReferenceId(21),
            new FileReferenceId(20),
            "report.txt",
            FileAttributes.Archive);

        var batch = NtfsUsnRecordParser.Parse(BuildBuffer(1234, folder, file));

        TestAssert.Equal(1234UL, batch.NextFileReferenceNumber, "Parser should preserve the MFT continuation file reference number");
        TestAssert.Equal(2, batch.Records.Count, "Parser should return every record after the continuation value");
        TestAssert.Equal(new FileReferenceId(20), batch.Records[0].FileReferenceId, "Parser should read a v2 file reference number");
        TestAssert.Equal(new FileReferenceId(5), batch.Records[0].ParentFileReferenceId, "Parser should read a v2 parent reference number");
        TestAssert.Equal("Reports", batch.Records[0].Name, "Parser should honor the runtime v2 file-name offset");
        TestAssert.True(batch.Records[0].Attributes.HasFlag(FileAttributes.Directory), "Parser should preserve directory attributes");
        TestAssert.Equal(usn, batch.Records[0].Usn, "Parser should read a v2 USN");
        TestAssert.Equal(reason, batch.Records[0].Reason, "Parser should read a v2 reason mask");
        TestAssert.Equal("report.txt", batch.Records[1].Name, "Parser should advance by each variable record length");

        var journal = NtfsUsnRecordParser.ParseJournal(BuildJournalBuffer(-123, folder));

        TestAssert.Equal(-123L, journal.NextUsn, "Journal parser should read the signed next USN");
        TestAssert.Equal(1, journal.Records.Count, "Journal parser should return v2 records after the next USN");
        TestAssert.Equal(usn, journal.Records[0].Usn, "Journal parser should preserve a v2 record USN");
        TestAssert.Equal(reason, journal.Records[0].Reason, "Journal parser should preserve a v2 reason mask");
    }

    public static void ParsesUnicodeAndV3FileIds()
    {
        var fileId = new FileReferenceId(0x0123456789ABCDEF, 0xFEDCBA9876543210);
        var parentId = new FileReferenceId(0x1111222233334444, 0xAAAABBBBCCCCDDDD);
        const string name = "한국어-보고서-😀.txt";
        const long usn = 9_876_543_210;
        const uint reason = 0x8000_2000;
        var record = BuildV3Record(
            fileId,
            parentId,
            name,
            FileAttributes.ReadOnly | FileAttributes.Archive,
            nameOffset: 80,
            usn: usn,
            reason: reason);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 1);

        var parsed = NtfsUsnRecordParser.Parse(BuildBuffer(999, record)).Records.Single();

        TestAssert.Equal(fileId, parsed.FileReferenceId, "Parser should preserve all 128 bits of a v3 file ID");
        TestAssert.Equal(parentId, parsed.ParentFileReferenceId, "Parser should preserve all 128 bits of a v3 parent ID");
        TestAssert.Equal(name, parsed.Name, "Parser should decode the exact UTF-16 file name without requiring a terminator");
        TestAssert.Equal(FileAttributes.ReadOnly | FileAttributes.Archive, parsed.Attributes, "Parser should preserve v3 file attributes");
        TestAssert.Equal(usn, parsed.Usn, "Parser should read a v3 USN");
        TestAssert.Equal(reason, parsed.Reason, "Parser should read a v3 reason mask");

        var journal = NtfsUsnRecordParser.ParseJournal(BuildJournalBuffer(long.MaxValue, record));
        var journalRecord = journal.Records.Single();

        TestAssert.Equal(long.MaxValue, journal.NextUsn, "Journal parser should preserve the signed v3 next USN");
        TestAssert.Equal(fileId, journalRecord.FileReferenceId, "Journal parser should preserve a v3 file ID");
        TestAssert.Equal(usn, journalRecord.Usn, "Journal parser should preserve a v3 record USN");
        TestAssert.Equal(reason, journalRecord.Reason, "Journal parser should preserve a v3 reason mask");

        var zeroHighId = new FileReferenceId(42, 0, FileReferenceIdWidth.Bits128);
        var zeroHighRecord = BuildV3Record(
            zeroHighId,
            new FileReferenceId(5, 0, FileReferenceIdWidth.Bits128),
            "wide-id.txt",
            FileAttributes.Normal);
        var parsedZeroHigh = NtfsUsnRecordParser.Parse(BuildBuffer(1000, zeroHighRecord)).Records.Single();
        TestAssert.True(
            parsedZeroHigh.FileReferenceId.Is128Bit,
            "A v3 FILE_ID_128 must retain its width even when the high 64 bits are zero");
    }

    public static void RejectsMalformedRecordBuffers()
    {
        var valid = BuildBuffer(
            10,
            BuildV2Record(new FileReferenceId(10), new FileReferenceId(5), "valid.txt", FileAttributes.Archive));

        var tooShortForContinuation = new byte[7];
        var truncatedHeader = new byte[12];

        var zeroRecordLength = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(zeroRecordLength.AsSpan(8), 0);

        var misalignedRecordLength = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            misalignedRecordLength.AsSpan(8),
            BinaryPrimitives.ReadUInt32LittleEndian(misalignedRecordLength.AsSpan(8)) - 2);

        var truncatedRecord = valid[..^8];

        var oddNameLength = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(oddNameLength.AsSpan(8 + 56), 1);

        var oddNameOffset = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(oddNameOffset.AsSpan(8 + 58), 61);

        var nameBeforeFixedFields = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(nameBeforeFixedFields.AsSpan(8 + 58), 58);

        var namePastRecord = (byte[])valid.Clone();
        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(namePastRecord.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(namePastRecord.AsSpan(8 + 58), (ushort)recordLength);

        var unsupportedVersion = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(unsupportedVersion.AsSpan(8 + 4), 4);

        foreach (var malformed in new[]
                 {
                     tooShortForContinuation,
                     truncatedHeader,
                     zeroRecordLength,
                     misalignedRecordLength,
                     truncatedRecord,
                     oddNameLength,
                     oddNameOffset,
                     nameBeforeFixedFields,
                     namePastRecord,
                     unsupportedVersion
                 })
        {
            ExpectInvalidData(malformed);
            ExpectInvalidJournalData(malformed);
        }
    }

    private static byte[] BuildBuffer(ulong continuation, params byte[][] records)
    {
        var buffer = new byte[sizeof(ulong) + records.Sum(record => record.Length)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, continuation);
        var offset = sizeof(ulong);
        foreach (var record in records)
        {
            record.CopyTo(buffer, offset);
            offset += record.Length;
        }

        return buffer;
    }

    private static byte[] BuildJournalBuffer(long nextUsn, params byte[][] records)
    {
        var buffer = new byte[sizeof(long) + records.Sum(record => record.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, nextUsn);
        var offset = sizeof(long);
        foreach (var record in records)
        {
            record.CopyTo(buffer, offset);
            offset += record.Length;
        }

        return buffer;
    }

    private static byte[] BuildV2Record(
        FileReferenceId fileId,
        FileReferenceId parentId,
        string name,
        FileAttributes attributes,
        ushort nameOffset = 60,
        long usn = 0,
        uint reason = 0)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var record = new byte[AlignToEight(nameOffset + nameBytes.Length)];
        WriteCommonHeader(record, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), fileId.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), parentId.Low);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), (uint)attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), nameOffset);
        nameBytes.CopyTo(record, nameOffset);
        return record;
    }

    private static byte[] BuildV3Record(
        FileReferenceId fileId,
        FileReferenceId parentId,
        string name,
        FileAttributes attributes,
        ushort nameOffset = 76,
        long usn = 0,
        uint reason = 0)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var record = new byte[AlignToEight(nameOffset + nameBytes.Length)];
        WriteCommonHeader(record, 3);
        WriteFileId(record.AsSpan(8), fileId);
        WriteFileId(record.AsSpan(24), parentId);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(68), (uint)attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(72), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(74), nameOffset);
        nameBytes.CopyTo(record, nameOffset);
        return record;
    }

    private static void WriteCommonHeader(Span<byte> record, ushort majorVersion)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], majorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(record[6..], 0);
    }

    private static void WriteFileId(Span<byte> destination, FileReferenceId id)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, id.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], id.High);
    }

    private static int AlignToEight(int value) => (value + 7) & ~7;

    private static void ExpectInvalidData(byte[] buffer)
    {
        try
        {
            _ = NtfsUsnRecordParser.Parse(buffer);
            throw new InvalidOperationException("A malformed USN record buffer should be rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void ExpectInvalidJournalData(byte[] buffer)
    {
        try
        {
            _ = NtfsUsnRecordParser.ParseJournal(buffer);
            throw new InvalidOperationException("A malformed USN journal buffer should be rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }
}
