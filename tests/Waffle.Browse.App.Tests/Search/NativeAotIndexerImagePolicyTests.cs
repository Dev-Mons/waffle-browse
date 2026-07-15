using System.Buffers.Binary;
using System.IO;
using System.Text;
using Waffle.Browse.App.Search;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.App.Tests.Search;

internal static class NativeAotIndexerImagePolicyTests
{
    private const int PeHeaderOffset = 0x80;
    private const int OptionalHeaderOffset = PeHeaderOffset + 4 + 20;
    private const int DataDirectoryOffset = OptionalHeaderOffset + 112;
    private const int SectionTableOffset = OptionalHeaderOffset + 0xf0;
    private const int SectionRawOffset = 0x200;
    private const int SectionRawSize = 0x400;
    private const int SectionRawEnd = SectionRawOffset + SectionRawSize;
    private const int ExportDirectoryOffset = SectionRawOffset;
    private const int ExportNameOffset = SectionRawOffset + 0x50;
    private const int BundleSignaturePlacementOffset = SectionRawOffset + 0x100;
    private const int CertificateDirectoryIndex = 4;
    private const int CorHeaderDirectoryIndex = 14;

    private static readonly byte[] RequiredExportName =
        "DotNetRuntimeDebugHeader"u8.ToArray();

    private static readonly byte[] BundleHeaderSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    public static void RequiresStructuredPeAndRejectsManagedSidecars()
    {
        var deploymentDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Waffle Browse NativeAOT Policy {Guid.NewGuid():N}");
        var helperPath = Path.Combine(
            deploymentDirectory,
            "Waffle.Browse.Indexer.exe");
        var managedSidecarPath = Path.ChangeExtension(helperPath, ".dll");
        Directory.CreateDirectory(deploymentDirectory);
        try
        {
            File.WriteAllBytes(helperPath, BuildStamp());
            if (NativeAotIndexerImagePolicy.IsNativeAotIndexerImage(helperPath))
            {
                throw new InvalidOperationException(
                    "The public build stamp alone must never satisfy the NativeAOT image policy.");
            }

            File.WriteAllBytes(helperPath, CreateNativePeImage());
            if (!NativeAotIndexerImagePolicy.IsNativeAotIndexerImage(helperPath))
            {
                throw new InvalidOperationException(
                    "A structurally valid AMD64 NativeAOT image contract should be accepted.");
            }

            File.WriteAllText(managedSidecarPath, "managed sidecar");
            if (NativeAotIndexerImagePolicy.IsNativeAotIndexerImage(helperPath))
            {
                throw new InvalidOperationException(
                    "A managed apphost sidecar must override the PE contract and fail closed.");
            }
        }
        finally
        {
            Directory.Delete(deploymentDirectory, recursive: true);
        }
    }

    public static void AcceptsOnlyExactStampBoundaries()
    {
        var unsignedImage = CreateNativePeImage();
        AssertValid(unsignedImage, "The stamp may end exactly at an unsigned image EOF.");

        var dataAfterStamp = new byte[unsignedImage.Length + 1];
        unsignedImage.CopyTo(dataAfterStamp, 0);
        AssertInvalid(
            dataAfterStamp,
            "Any overlay after an unsigned EOF stamp must invalidate the image.");

        var signedImage = CreateNativePeImage(withCertificateTable: true);
        AssertValid(
            signedImage,
            "The stamp may directly precede an aligned Authenticode certificate table.");

        var certificateOffset = ReadUInt32(
            signedImage,
            DataDirectoryOffset + (CertificateDirectoryIndex * 8));
        var stampEnd = SectionRawEnd + BuildStamp().Length;
        if (certificateOffset > stampEnd)
        {
            var nonZeroAlignmentPadding = signedImage.ToArray();
            nonZeroAlignmentPadding[stampEnd] = 0x7f;
            AssertInvalid(
                nonZeroAlignmentPadding,
                "Only zero alignment padding may separate the stamp and certificate table.");
        }

        var misplacedStamp = signedImage.ToArray();
        misplacedStamp[SectionRawEnd] ^= 0x01;
        AssertInvalid(
            misplacedStamp,
            "A certificate table cannot rescue a stamp at any other file position.");
    }

    public static void VerifiedImageLeaseBlocksMutationUntilLaunchCompletes()
    {
        var deploymentDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Waffle Browse NativeAOT Lease {Guid.NewGuid():N}");
        var helperPath = Path.Combine(deploymentDirectory, "Waffle.Browse.Indexer.exe");
        Directory.CreateDirectory(deploymentDirectory);
        File.WriteAllBytes(helperPath, CreateNativePeImage());
        try
        {
            var lease = NativeAotIndexerImagePolicy.AcquireNativeAotIndexerImage(helperPath)
                ?? throw new InvalidOperationException(
                    "A valid helper image should produce a launch lease.");
            using (lease)
            {
                try
                {
                    using var writer = new FileStream(
                        helperPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    throw new InvalidOperationException(
                        "The verified helper must not be writable while its launch lease is held.");
                }
                catch (IOException)
                {
                }
            }

            using var afterRelease = new FileStream(
                helperPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        }
        finally
        {
            Directory.Delete(deploymentDirectory, recursive: true);
        }
    }

    public static void RejectsManagedBundlesAndManagedPeHeaders()
    {
        var bundledImage = CreateNativePeImage();
        BundleHeaderSignature.CopyTo(
            bundledImage.AsSpan(BundleSignaturePlacementOffset));
        AssertInvalid(
            bundledImage,
            "The official Microsoft.NET.HostModel bundle signature must be rejected.");

        var managedPeImage = CreateNativePeImage();
        WriteUInt32(
            managedPeImage,
            DataDirectoryOffset + (CorHeaderDirectoryIndex * 8),
            0x1100);
        WriteUInt32(
            managedPeImage,
            DataDirectoryOffset + (CorHeaderDirectoryIndex * 8) + 4,
            0x48);
        AssertInvalid(managedPeImage, "A PE image with a COR header must be rejected.");

        var wrongMachineImage = CreateNativePeImage();
        WriteUInt16(wrongMachineImage, PeHeaderOffset + 4, 0x014c);
        AssertInvalid(wrongMachineImage, "A PE32 x86 image must not pass the win-x64 contract.");

        var missingExportImage = CreateNativePeImage();
        missingExportImage[ExportNameOffset] ^= 0x20;
        AssertInvalid(
            missingExportImage,
            "The NativeAOT debug-header export name must match exactly.");
    }

    public static void PeParserFailsClosedAtEveryTruncation()
    {
        var validImage = CreateNativePeImage();
        for (var length = 0; length < validImage.Length; length++)
        {
            if (NativeAotIndexerImagePolicy.IsNativeAotIndexerImageContents(
                    validImage.AsSpan(0, length)))
            {
                throw new InvalidOperationException(
                    $"The PE parser accepted an image truncated to {length} bytes.");
            }
        }

        AssertValid(validImage, "The complete control image must remain valid.");
    }

    public static void MarkerIsExcludedFromCoreAssembly()
    {
        var coreAssemblyPath = typeof(NamedPipeFileIndexSource).Assembly.Location;
        var coreAssemblyBytes = File.ReadAllBytes(coreAssemblyPath);
        var marker = NativeAotIndexerImagePolicy.NativeAotImageMarker;
        if (coreAssemblyBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0)
        {
            throw new InvalidOperationException(
                "The helper-referenced Core assembly must not carry the UTF-8 NativeAOT stamp.");
        }

        if (coreAssemblyBytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(marker)) >= 0)
        {
            throw new InvalidOperationException(
                "The helper-referenced Core assembly must not carry the UTF-16 NativeAOT stamp.");
        }
    }

    private static byte[] CreateNativePeImage(bool withCertificateTable = false)
    {
        var stamp = BuildStamp();
        var stampEnd = SectionRawEnd + stamp.Length;
        var certificateOffset = withCertificateTable
            ? (stampEnd + 7) & ~7
            : 0;
        const int certificateSize = 16;
        var imageLength = withCertificateTable
            ? certificateOffset + certificateSize
            : stampEnd;
        var image = new byte[imageLength];

        WriteUInt16(image, 0, 0x5a4d);
        WriteUInt32(image, 0x3c, PeHeaderOffset);
        "PE\0\0"u8.CopyTo(image.AsSpan(PeHeaderOffset));

        var coffHeaderOffset = PeHeaderOffset + 4;
        WriteUInt16(image, coffHeaderOffset, 0x8664);
        WriteUInt16(image, coffHeaderOffset + 2, 1);
        WriteUInt16(image, coffHeaderOffset + 16, 0xf0);

        WriteUInt16(image, OptionalHeaderOffset, 0x020b);
        WriteUInt32(image, OptionalHeaderOffset + 60, SectionRawOffset);
        WriteUInt32(image, OptionalHeaderOffset + 108, 16);
        WriteUInt32(image, DataDirectoryOffset, 0x1000);
        WriteUInt32(image, DataDirectoryOffset + 4, 0x100);

        if (withCertificateTable)
        {
            WriteUInt32(
                image,
                DataDirectoryOffset + (CertificateDirectoryIndex * 8),
                certificateOffset);
            WriteUInt32(
                image,
                DataDirectoryOffset + (CertificateDirectoryIndex * 8) + 4,
                certificateSize);
        }

        WriteUInt32(image, SectionTableOffset + 8, SectionRawSize);
        WriteUInt32(image, SectionTableOffset + 12, 0x1000);
        WriteUInt32(image, SectionTableOffset + 16, SectionRawSize);
        WriteUInt32(image, SectionTableOffset + 20, SectionRawOffset);

        WriteUInt32(image, ExportDirectoryOffset + 20, 1);
        WriteUInt32(image, ExportDirectoryOffset + 24, 1);
        WriteUInt32(image, ExportDirectoryOffset + 28, 0x1040);
        WriteUInt32(image, ExportDirectoryOffset + 32, 0x1044);
        WriteUInt32(image, ExportDirectoryOffset + 36, 0x1048);
        WriteUInt32(image, SectionRawOffset + 0x40, 0x1180);
        WriteUInt32(image, SectionRawOffset + 0x44, 0x1050);
        WriteUInt16(image, SectionRawOffset + 0x48, 0);
        RequiredExportName.CopyTo(image.AsSpan(ExportNameOffset));
        image[ExportNameOffset + RequiredExportName.Length] = 0;
        image[SectionRawOffset + 0x180] = 0xc3;

        stamp.CopyTo(image.AsSpan(SectionRawEnd));
        if (withCertificateTable)
        {
            WriteUInt32(image, certificateOffset, certificateSize);
            WriteUInt16(image, certificateOffset + 4, 0x0200);
            WriteUInt16(image, certificateOffset + 6, 0x0002);
            image[certificateOffset + 8] = 0x30;
        }

        return image;
    }

    private static byte[] BuildStamp() =>
        Encoding.ASCII.GetBytes(
            $"{NativeAotIndexerImagePolicy.NativeAotImageMarker}\r\n");

    private static void AssertValid(byte[] image, string message)
    {
        if (!NativeAotIndexerImagePolicy.IsNativeAotIndexerImageContents(image))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertInvalid(byte[] image, string message)
    {
        if (NativeAotIndexerImagePolicy.IsNativeAotIndexerImageContents(image))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static uint ReadUInt32(byte[] image, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, sizeof(uint)));

    private static void WriteUInt16(byte[] image, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan(offset, sizeof(ushort)),
            checked((ushort)value));

    private static void WriteUInt32(byte[] image, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(offset, sizeof(uint)),
            checked((uint)value));
}
