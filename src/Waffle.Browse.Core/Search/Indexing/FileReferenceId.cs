using System.Text.Json.Serialization;

namespace Waffle.Browse.Core.Search.Indexing;

public enum FileReferenceIdWidth
{
    Bits64,
    Bits128
}

public readonly struct FileReferenceId : IEquatable<FileReferenceId>
{
    [JsonConstructor]
    public FileReferenceId(
        ulong low,
        ulong high = 0,
        FileReferenceIdWidth width = FileReferenceIdWidth.Bits64)
    {
        Low = low;
        High = high;
        Width = high == 0 ? width : FileReferenceIdWidth.Bits128;
    }

    public ulong Low { get; }

    public ulong High { get; }

    public FileReferenceIdWidth Width { get; }

    [JsonIgnore]
    public bool Is128Bit => Width == FileReferenceIdWidth.Bits128;

    // V2 and V3 USN records can describe the same NTFS file ID. Width is
    // representation metadata; the numeric 128-bit value is the identity.
    public bool Equals(FileReferenceId other) => Low == other.Low && High == other.High;

    public override bool Equals(object? obj) => obj is FileReferenceId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Low, High);

    public override string ToString() =>
        Is128Bit ? $"{High:X16}{Low:X16}" : $"{Low:X16}";

    public static bool operator ==(FileReferenceId left, FileReferenceId right) => left.Equals(right);

    public static bool operator !=(FileReferenceId left, FileReferenceId right) => !left.Equals(right);
}
