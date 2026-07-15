using System.Buffers.Binary;
using System.IO;
using System.Text;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.App.Search;

internal static class NativeAotIndexerImagePolicy
{
    private const int MaximumImageSize = 512 * 1024 * 1024;
    private const int PeSignatureSize = 4;
    private const int CoffHeaderSize = 20;
    private const int SectionHeaderSize = 40;
    private const int ExportDirectorySize = 40;
    private const int MaximumExportNameCount = 65_536;
    private const ushort Amd64Machine = 0x8664;
    private const ushort Pe32PlusMagic = 0x020b;
    private const ushort WinCertificateRevision20 = 0x0200;
    private const ushort WinCertificatePkcsSignedData = 0x0002;
    private const int ExportDirectoryIndex = 0;
    private const int SecurityDirectoryIndex = 4;
    private const int CorHeaderDirectoryIndex = 14;
    private const int Pe32PlusDataDirectoryOffset = 112;
    private const int Pe32PlusNumberOfRvaAndSizesOffset = 108;
    private const int Pe32PlusSizeOfHeadersOffset = 60;
    private const string NativeAotImageMarkerResourceName =
        "Waffle.Browse.App.Search.NativeAotIndexerImage.marker";

    private static readonly byte[] RequiredExportName =
        "DotNetRuntimeDebugHeader"u8.ToArray();

    // Microsoft.NET.HostModel.Bundle.Bundler.BundleHeaderSignature. A managed
    // single-file apphost has no COR header either, so this signature is an
    // independent and mandatory exclusion.
    private static readonly byte[] BundleHeaderSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    private static readonly string NativeAotImageMarkerValue = LoadNativeAotImageMarker();
    private static readonly byte[] NativeAotBuildStamp =
        string.IsNullOrEmpty(NativeAotImageMarkerValue)
            ? []
            : Encoding.ASCII.GetBytes($"{NativeAotImageMarkerValue}\r\n");

    internal static string NativeAotImageMarker => NativeAotImageMarkerValue;

    internal static bool IsNativeAotIndexerImage(string helperPath)
    {
        using var imageLease = AcquireNativeAotIndexerImage(helperPath);
        return imageLease is not null;
    }

    internal static IDisposable? AcquireNativeAotIndexerImage(string helperPath)
    {
        if (string.IsNullOrWhiteSpace(helperPath))
        {
            return null;
        }

        FileStream? stream = null;
        try
        {
            var normalizedHelperPath = Path.GetFullPath(helperPath);
            if (!string.Equals(
                    Path.GetFileName(normalizedHelperPath),
                    NamedPipeFileIndexSecurity.IndexerExecutableName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var helperDirectory = Path.GetDirectoryName(normalizedHelperPath);
            var helperBaseName = Path.GetFileNameWithoutExtension(normalizedHelperPath);
            if (string.IsNullOrWhiteSpace(helperDirectory)
                || NativeAotBuildStamp.Length == 0
                || EntryExists(Path.Combine(helperDirectory, $"{helperBaseName}.dll"))
                || EntryExists(Path.Combine(helperDirectory, $"{helperBaseName}.deps.json"))
                || EntryExists(Path.Combine(helperDirectory, $"{helperBaseName}.runtimeconfig.json")))
            {
                return null;
            }

            stream = new FileStream(
                normalizedHelperPath,
                FileMode.Open,
                FileAccess.Read,
                // Hold this handle through ShellExecute/runas. Denying write
                // and delete sharing closes the verified-image replacement
                // window and also detects a pre-existing mutating handle.
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length <= 0
                || stream.Length > MaximumImageSize
                || stream.Length > int.MaxValue)
            {
                stream.Dispose();
                return null;
            }

            var image = new byte[(int)stream.Length];
            stream.ReadExactly(image);
            if (!IsNativeAotIndexerImageContents(image))
            {
                stream.Dispose();
                return null;
            }

            return stream;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            stream?.Dispose();
            return null;
        }
    }

    internal static bool IsNativeAotIndexerImageContents(ReadOnlySpan<byte> image)
    {
        if (NativeAotBuildStamp.Length == 0
            || image.IndexOf(BundleHeaderSignature) >= 0
            || !TryReadPeLayout(image, out var layout)
            || !HasRequiredExport(image, layout)
            || !HasBuildStampAtTrustedBoundary(image, layout))
        {
            return false;
        }

        return true;
    }

    private static string LoadNativeAotImageMarker()
    {
        try
        {
            using var stream = typeof(NativeAotIndexerImagePolicy).Assembly
                .GetManifestResourceStream(NativeAotImageMarkerResourceName);
            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false);
            var marker = reader.ReadToEnd().TrimEnd('\r', '\n');
            return marker.Length is >= 32 and <= 256
                   && marker.All(static character =>
                       character is >= 'A' and <= 'Z'
                       or >= '0' and <= '9'
                       or '_')
                ? marker
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadPeLayout(
        ReadOnlySpan<byte> image,
        out PeLayout layout)
    {
        layout = default;
        if (!TryReadUInt16(image, 0, out var dosSignature)
            || dosSignature != 0x5a4d
            || !TryReadUInt32(image, 0x3c, out var peHeaderOffsetValue)
            || peHeaderOffsetValue > int.MaxValue)
        {
            return false;
        }

        var peHeaderOffset = (int)peHeaderOffsetValue;
        var coffHeaderOffset = peHeaderOffset + PeSignatureSize;
        if (!IsRangeWithin(image, peHeaderOffset, PeSignatureSize + CoffHeaderSize)
            || !image.Slice(peHeaderOffset, PeSignatureSize).SequenceEqual("PE\0\0"u8)
            || !TryReadUInt16(image, coffHeaderOffset, out var machine)
            || machine != Amd64Machine
            || !TryReadUInt16(image, coffHeaderOffset + 2, out var sectionCount)
            || sectionCount == 0
            || !TryReadUInt16(image, coffHeaderOffset + 16, out var optionalHeaderSize))
        {
            return false;
        }

        var optionalHeaderOffset = coffHeaderOffset + CoffHeaderSize;
        var minimumOptionalHeaderSize = Pe32PlusDataDirectoryOffset
                                        + ((CorHeaderDirectoryIndex + 1) * 8);
        if (optionalHeaderSize < minimumOptionalHeaderSize
            || !IsRangeWithin(image, optionalHeaderOffset, optionalHeaderSize)
            || !TryReadUInt16(image, optionalHeaderOffset, out var optionalMagic)
            || optionalMagic != Pe32PlusMagic
            || !TryReadUInt32(
                image,
                optionalHeaderOffset + Pe32PlusNumberOfRvaAndSizesOffset,
                out var directoryCount)
            || directoryCount <= CorHeaderDirectoryIndex
            || !TryReadUInt32(
                image,
                optionalHeaderOffset + Pe32PlusSizeOfHeadersOffset,
                out var sizeOfHeaders)
            || sizeOfHeaders == 0
            || sizeOfHeaders > image.Length)
        {
            return false;
        }

        if (!TryReadDataDirectory(
                image,
                optionalHeaderOffset,
                ExportDirectoryIndex,
                out var exportRva,
                out var exportSize)
            || exportRva == 0
            || exportSize < ExportDirectorySize
            || !TryReadDataDirectory(
                image,
                optionalHeaderOffset,
                SecurityDirectoryIndex,
                out var certificateOffset,
                out var certificateSize)
            || !TryReadDataDirectory(
                image,
                optionalHeaderOffset,
                CorHeaderDirectoryIndex,
                out var corHeaderRva,
                out var corHeaderSize)
            || corHeaderRva != 0
            || corHeaderSize != 0)
        {
            return false;
        }

        var sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
        var sectionTableSize = (long)sectionCount * SectionHeaderSize;
        if (!IsRangeWithin(image, sectionTableOffset, sectionTableSize))
        {
            return false;
        }

        uint lastSectionFileEnd = sizeOfHeaders;
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var sectionOffset = sectionTableOffset + (sectionIndex * SectionHeaderSize);
            if (!TryReadUInt32(image, sectionOffset + 16, out var rawSize)
                || !TryReadUInt32(image, sectionOffset + 20, out var rawOffset))
            {
                return false;
            }

            if (rawSize == 0)
            {
                continue;
            }

            var rawEnd = (ulong)rawOffset + rawSize;
            if (rawEnd > (ulong)image.Length)
            {
                return false;
            }

            lastSectionFileEnd = Math.Max(lastSectionFileEnd, (uint)rawEnd);
        }

        if ((certificateOffset == 0) != (certificateSize == 0)
            || certificateOffset != 0
               && (!IsValidCertificateTable(
                       image,
                       certificateOffset,
                       certificateSize,
                       lastSectionFileEnd)
                   || certificateOffset < lastSectionFileEnd))
        {
            return false;
        }

        layout = new PeLayout(
            sectionTableOffset,
            sectionCount,
            sizeOfHeaders,
            exportRva,
            exportSize,
            certificateOffset,
            certificateSize,
            lastSectionFileEnd);
        return true;
    }

    private static bool HasRequiredExport(
        ReadOnlySpan<byte> image,
        PeLayout layout)
    {
        if (!TryMapRva(image, layout, layout.ExportRva, ExportDirectorySize, out var exportOffset)
            || !TryReadUInt32(image, exportOffset + 20, out var functionCount)
            || functionCount == 0
            || !TryReadUInt32(image, exportOffset + 24, out var nameCount)
            || nameCount == 0
            || nameCount > MaximumExportNameCount
            || !TryReadUInt32(image, exportOffset + 28, out var functionTableRva)
            || !TryReadUInt32(image, exportOffset + 32, out var nameTableRva)
            || !TryReadUInt32(image, exportOffset + 36, out var ordinalTableRva)
            || functionCount > int.MaxValue / sizeof(uint)
            || !TryMapRva(
                image,
                layout,
                functionTableRva,
                checked((int)functionCount * sizeof(uint)),
                out var functionTableOffset)
            || !TryMapRva(
                image,
                layout,
                nameTableRva,
                checked((int)nameCount * sizeof(uint)),
                out var nameTableOffset)
            || !TryMapRva(
                image,
                layout,
                ordinalTableRva,
                checked((int)nameCount * sizeof(ushort)),
                out var ordinalTableOffset))
        {
            return false;
        }

        for (var nameIndex = 0; nameIndex < (int)nameCount; nameIndex++)
        {
            if (!TryReadUInt32(
                    image,
                    nameTableOffset + (nameIndex * sizeof(uint)),
                    out var nameRva)
                || !TryMapRva(
                    image,
                    layout,
                    nameRva,
                    RequiredExportName.Length + 1,
                    out var nameOffset)
                || !image.Slice(nameOffset, RequiredExportName.Length)
                    .SequenceEqual(RequiredExportName)
                || image[nameOffset + RequiredExportName.Length] != 0)
            {
                continue;
            }

            if (!TryReadUInt16(
                    image,
                    ordinalTableOffset + (nameIndex * sizeof(ushort)),
                    out var ordinal)
                || ordinal >= functionCount
                || !TryReadUInt32(
                    image,
                    functionTableOffset + (ordinal * sizeof(uint)),
                    out var targetRva)
                || targetRva == 0)
            {
                return false;
            }

            var exportEnd = (ulong)layout.ExportRva + layout.ExportSize;
            if (targetRva >= layout.ExportRva && (ulong)targetRva < exportEnd)
            {
                return false;
            }

            return TryMapRva(image, layout, targetRva, 1, out _);
        }

        return false;
    }

    private static bool HasBuildStampAtTrustedBoundary(
        ReadOnlySpan<byte> image,
        PeLayout layout)
    {
        if (layout.CertificateOffset == 0)
        {
            var stampOffset = image.Length - NativeAotBuildStamp.Length;
            return stampOffset >= layout.LastSectionFileEnd
                   && stampOffset >= 0
                   && image[stampOffset..].SequenceEqual(NativeAotBuildStamp);
        }

        var certificateOffset = checked((int)layout.CertificateOffset);
        for (var paddingLength = 0; paddingLength < 8; paddingLength++)
        {
            var stampOffset = certificateOffset
                              - paddingLength
                              - NativeAotBuildStamp.Length;
            if (stampOffset < layout.LastSectionFileEnd
                || stampOffset < 0
                || !image.Slice(stampOffset, NativeAotBuildStamp.Length)
                    .SequenceEqual(NativeAotBuildStamp))
            {
                continue;
            }

            var stampEnd = stampOffset + NativeAotBuildStamp.Length;
            var expectedPadding = (8 - (stampEnd & 7)) & 7;
            if (expectedPadding == paddingLength
                && IsAllZero(image.Slice(stampEnd, paddingLength)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidCertificateTable(
        ReadOnlySpan<byte> image,
        uint certificateOffset,
        uint certificateSize,
        uint lastSectionFileEnd)
    {
        if ((certificateOffset & 7) != 0
            || certificateOffset < lastSectionFileEnd
            || certificateSize < 8
            || (ulong)certificateOffset + certificateSize != (ulong)image.Length)
        {
            return false;
        }

        var cursor = (ulong)certificateOffset;
        var tableEnd = cursor + certificateSize;
        while (cursor < tableEnd)
        {
            if (cursor > int.MaxValue
                || !TryReadUInt32(image, (int)cursor, out var certificateLength)
                || certificateLength < 8
                || !TryReadUInt16(image, (int)cursor + 4, out var revision)
                || revision != WinCertificateRevision20
                || !TryReadUInt16(image, (int)cursor + 6, out var certificateType)
                || certificateType != WinCertificatePkcsSignedData)
            {
                return false;
            }

            var alignedCertificateLength = ((ulong)certificateLength + 7) & ~7UL;
            if (alignedCertificateLength > tableEnd - cursor)
            {
                return false;
            }

            var paddingOffset = cursor + certificateLength;
            var paddingLength = alignedCertificateLength - certificateLength;
            if (paddingOffset > int.MaxValue
                || paddingLength > int.MaxValue
                || !IsAllZero(image.Slice((int)paddingOffset, (int)paddingLength)))
            {
                return false;
            }

            cursor += alignedCertificateLength;
        }

        return cursor == tableEnd;
    }

    private static bool TryMapRva(
        ReadOnlySpan<byte> image,
        PeLayout layout,
        uint rva,
        int requiredSize,
        out int fileOffset)
    {
        fileOffset = 0;
        if (requiredSize < 0)
        {
            return false;
        }

        if ((ulong)rva + (uint)requiredSize <= layout.SizeOfHeaders
            && IsRangeWithin(image, rva, requiredSize))
        {
            fileOffset = (int)rva;
            return true;
        }

        for (var sectionIndex = 0; sectionIndex < layout.SectionCount; sectionIndex++)
        {
            var sectionOffset = layout.SectionTableOffset + (sectionIndex * SectionHeaderSize);
            if (!TryReadUInt32(image, sectionOffset + 8, out var virtualSize)
                || !TryReadUInt32(image, sectionOffset + 12, out var virtualAddress)
                || !TryReadUInt32(image, sectionOffset + 16, out var rawSize)
                || !TryReadUInt32(image, sectionOffset + 20, out var rawOffset))
            {
                return false;
            }

            if (rva < virtualAddress)
            {
                continue;
            }

            var delta = (ulong)rva - virtualAddress;
            var mappedSize = Math.Max(virtualSize, rawSize);
            if (delta + (uint)requiredSize > mappedSize)
            {
                continue;
            }

            if (delta + (uint)requiredSize > rawSize
                || (ulong)rawOffset + delta > int.MaxValue
                || !IsRangeWithin(
                    image,
                    (long)rawOffset + (long)delta,
                    requiredSize))
            {
                return false;
            }

            fileOffset = checked((int)((ulong)rawOffset + delta));
            return true;
        }

        return false;
    }

    private static bool TryReadDataDirectory(
        ReadOnlySpan<byte> image,
        int optionalHeaderOffset,
        int directoryIndex,
        out uint address,
        out uint size)
    {
        address = 0;
        size = 0;
        var directoryOffset = optionalHeaderOffset
                              + Pe32PlusDataDirectoryOffset
                              + (directoryIndex * 8);
        return TryReadUInt32(image, directoryOffset, out address)
               && TryReadUInt32(image, directoryOffset + 4, out size);
    }

    private static bool TryReadUInt16(
        ReadOnlySpan<byte> image,
        int offset,
        out ushort value)
    {
        if (!IsRangeWithin(image, offset, sizeof(ushort)))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset, sizeof(ushort)));
        return true;
    }

    private static bool TryReadUInt32(
        ReadOnlySpan<byte> image,
        int offset,
        out uint value)
    {
        if (!IsRangeWithin(image, offset, sizeof(uint)))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset, sizeof(uint)));
        return true;
    }

    private static bool IsRangeWithin(
        ReadOnlySpan<byte> image,
        long offset,
        long length) =>
        offset >= 0
        && length >= 0
        && offset <= image.Length
        && length <= image.Length - offset;

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct PeLayout(
        int SectionTableOffset,
        ushort SectionCount,
        uint SizeOfHeaders,
        uint ExportRva,
        uint ExportSize,
        uint CertificateOffset,
        uint CertificateSize,
        uint LastSectionFileEnd);
}
