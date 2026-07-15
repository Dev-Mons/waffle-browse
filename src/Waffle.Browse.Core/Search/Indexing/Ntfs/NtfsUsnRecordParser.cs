using System.Buffers.Binary;

namespace Waffle.Browse.Core.Search.Indexing.Ntfs;

internal static class NtfsUsnRecordParser
{
    private const int ContinuationSize = sizeof(ulong);
    private const int CommonHeaderSize = sizeof(uint) + sizeof(ushort) + sizeof(ushort);
    private const int V2FixedSize = 60;
    private const int V3FixedSize = 76;

    public static NtfsMftBatch Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ContinuationSize)
        {
            throw Invalid("The MFT enumeration buffer does not contain a continuation file reference number.");
        }

        var nextFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return new NtfsMftBatch(nextFileReferenceNumber, ParseRecords(buffer));
    }

    public static NtfsUsnJournalBatch ParseJournal(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ContinuationSize)
        {
            throw Invalid("The USN journal buffer does not contain a next USN.");
        }

        var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        return new NtfsUsnJournalBatch(nextUsn, ParseRecords(buffer));
    }

    private static IReadOnlyList<NtfsMftRecord> ParseRecords(ReadOnlySpan<byte> buffer)
    {
        var records = new List<NtfsMftRecord>();
        var offset = ContinuationSize;

        while (offset < buffer.Length)
        {
            var remaining = buffer.Length - offset;
            if (remaining < CommonHeaderSize)
            {
                throw Invalid("The MFT enumeration buffer ends inside a USN record header.");
            }

            if ((offset & 7) != 0)
            {
                throw Invalid("A USN record is not aligned to an 8-byte boundary.");
            }

            var recordBuffer = buffer[offset..];
            var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(recordBuffer);
            if (recordLength == 0 || (recordLength & 7) != 0)
            {
                throw Invalid("A USN record length must be a non-zero multiple of 8 bytes.");
            }

            if (recordLength > int.MaxValue || recordLength > remaining)
            {
                throw Invalid("A USN record extends beyond the returned MFT enumeration buffer.");
            }

            var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer[4..]);
            var record = recordBuffer[..(int)recordLength];
            records.Add(majorVersion switch
            {
                2 => ParseV2(record),
                3 => ParseV3(record),
                _ => throw Invalid($"Unsupported USN record version {majorVersion}.")
            });

            offset += (int)recordLength;
        }

        return records;
    }

    private static NtfsMftRecord ParseV2(ReadOnlySpan<byte> record)
    {
        EnsureFixedFields(record, V2FixedSize, 2);
        var fileReferenceId = new FileReferenceId(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]));
        var parentFileReferenceId = new FileReferenceId(BinaryPrimitives.ReadUInt64LittleEndian(record[16..]));
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
        var reason = BinaryPrimitives.ReadUInt32LittleEndian(record[40..]);
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
        var name = ReadName(record, V2FixedSize, 56, 58);
        return new NtfsMftRecord(fileReferenceId, parentFileReferenceId, name, attributes, usn, reason);
    }

    private static NtfsMftRecord ParseV3(ReadOnlySpan<byte> record)
    {
        EnsureFixedFields(record, V3FixedSize, 3);
        var fileReferenceId = ReadFileReferenceId(record[8..]);
        var parentFileReferenceId = ReadFileReferenceId(record[24..]);
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
        var reason = BinaryPrimitives.ReadUInt32LittleEndian(record[56..]);
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
        var name = ReadName(record, V3FixedSize, 72, 74);
        return new NtfsMftRecord(fileReferenceId, parentFileReferenceId, name, attributes, usn, reason);
    }

    private static void EnsureFixedFields(ReadOnlySpan<byte> record, int fixedSize, ushort expectedMajorVersion)
    {
        if (record.Length < fixedSize)
        {
            throw Invalid($"A USN v{expectedMajorVersion} record is shorter than its fixed fields.");
        }

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        if (majorVersion != expectedMajorVersion)
        {
            throw Invalid($"Expected a USN v{expectedMajorVersion} record but found version {majorVersion}.");
        }
    }

    private static FileReferenceId ReadFileReferenceId(ReadOnlySpan<byte> value) =>
        new(
            BinaryPrimitives.ReadUInt64LittleEndian(value),
            BinaryPrimitives.ReadUInt64LittleEndian(value[8..]),
            FileReferenceIdWidth.Bits128);

    private static string ReadName(
        ReadOnlySpan<byte> record,
        int fixedSize,
        int nameLengthOffset,
        int nameOffsetOffset)
    {
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[nameLengthOffset..]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[nameOffsetOffset..]);

        if ((nameLength & 1) != 0 || (nameOffset & 1) != 0)
        {
            throw Invalid("A USN record name offset and length must be aligned to UTF-16 code units.");
        }

        if (nameOffset < fixedSize || nameOffset > record.Length || nameLength > record.Length - nameOffset)
        {
            throw Invalid("A USN record name extends beyond the record boundary.");
        }

        var nameBytes = record.Slice(nameOffset, nameLength);
        var characterCount = nameLength / sizeof(char);
        Span<char> characters = characterCount <= 512
            ? stackalloc char[characterCount]
            : new char[characterCount];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(nameBytes[(index * sizeof(char))..]);
        }

        return new string(characters);
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
