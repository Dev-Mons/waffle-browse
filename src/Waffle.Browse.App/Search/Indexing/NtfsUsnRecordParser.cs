using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace Waffle.Browse.App.Search.Indexing;

internal readonly record struct NtfsFileReference(ulong Low, ulong High = 0);

internal readonly record struct NtfsUsnRecord(
    NtfsFileReference FileReference,
    NtfsFileReference ParentFileReference,
    string Name,
    uint FileAttributes,
    long Usn,
    ushort MajorVersion)
{
    public bool IsDirectory => (FileAttributes & (uint)System.IO.FileAttributes.Directory) != 0;
}

internal sealed record NtfsUsnRecordBatch(
    ulong NextFileReferenceNumber,
    IReadOnlyList<NtfsUsnRecord> Records);

internal static class NtfsUsnRecordParser
{
    internal const int ContinuationSize = sizeof(ulong);
    internal const int CommonHeaderSize = sizeof(uint) + sizeof(ushort) + sizeof(ushort);
    internal const int Version2MinimumRecordSize = 60;
    internal const int Version3MinimumRecordSize = 76;

    public static NtfsUsnRecordBatch ParseBatch(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ContinuationSize)
        {
            throw new InvalidDataException("NTFS MFT 응답에 다음 파일 참조 번호가 없습니다.");
        }

        var nextFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        var records = new List<NtfsUsnRecord>();
        var offset = ContinuationSize;
        while (offset < buffer.Length)
        {
            var remaining = buffer.Length - offset;
            if (remaining < CommonHeaderSize)
            {
                throw new InvalidDataException("NTFS USN 레코드 헤더가 응답 경계를 벗어났습니다.");
            }

            var recordBuffer = buffer[offset..];
            var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(recordBuffer);
            if (recordLength == 0 || recordLength > int.MaxValue)
            {
                throw new InvalidDataException("NTFS USN 레코드 길이가 올바르지 않습니다.");
            }

            var length = (int)recordLength;
            if (length > remaining || (length & 7) != 0)
            {
                throw new InvalidDataException("NTFS USN 레코드가 응답 경계 또는 8바이트 정렬을 위반했습니다.");
            }

            var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer[4..]);
            var record = majorVersion switch
            {
                2 => ParseVersion2(recordBuffer[..length]),
                3 => ParseVersion3(recordBuffer[..length]),
                _ => throw new InvalidDataException($"지원하지 않는 NTFS USN 레코드 버전입니다: {majorVersion}.")
            };
            records.Add(record);
            offset += length;
        }

        return new NtfsUsnRecordBatch(nextFileReferenceNumber, records);
    }

    private static NtfsUsnRecord ParseVersion2(ReadOnlySpan<byte> record)
    {
        EnsureMinimumLength(record, Version2MinimumRecordSize, 2);
        var fileReference = new NtfsFileReference(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]));
        var parentReference = new NtfsFileReference(BinaryPrimitives.ReadUInt64LittleEndian(record[16..]));
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
        var name = ReadName(record, 56, 58, Version2MinimumRecordSize);
        return new NtfsUsnRecord(fileReference, parentReference, name, attributes, usn, 2);
    }

    private static NtfsUsnRecord ParseVersion3(ReadOnlySpan<byte> record)
    {
        EnsureMinimumLength(record, Version3MinimumRecordSize, 3);
        var fileReference = new NtfsFileReference(
            BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[16..]));
        var parentReference = new NtfsFileReference(
            BinaryPrimitives.ReadUInt64LittleEndian(record[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[32..]));
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
        var name = ReadName(record, 72, 74, Version3MinimumRecordSize);
        return new NtfsUsnRecord(fileReference, parentReference, name, attributes, usn, 3);
    }

    private static void EnsureMinimumLength(ReadOnlySpan<byte> record, int minimumLength, ushort majorVersion)
    {
        if (record.Length < minimumLength)
        {
            throw new InvalidDataException($"NTFS USN v{majorVersion} 레코드가 최소 헤더보다 짧습니다.");
        }
    }

    private static string ReadName(
        ReadOnlySpan<byte> record,
        int nameLengthOffset,
        int nameOffsetOffset,
        int minimumNameOffset)
    {
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[nameLengthOffset..]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[nameOffsetOffset..]);
        if ((nameLength & 1) != 0
            || (nameOffset & 1) != 0
            || nameOffset < minimumNameOffset
            || nameOffset > record.Length
            || nameLength > record.Length - nameOffset)
        {
            throw new InvalidDataException("NTFS USN 레코드의 Unicode 이름 범위가 올바르지 않습니다.");
        }

        var nameBytes = record.Slice(nameOffset, nameLength);
        if (BitConverter.IsLittleEndian)
        {
            return new string(MemoryMarshal.Cast<byte, char>(nameBytes));
        }

        var characters = new char[nameLength / sizeof(char)];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(nameBytes[(index * sizeof(char))..]);
        }

        return new string(characters);
    }
}
