using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.App.Search.Indexing;

public sealed class NtfsMftIndexSource : IFileIndexSource
{
    public Task<FileIndexBuildResult> BuildAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Task.Run(() => Build(roots, cancellationToken), cancellationToken);
    }

    private static FileIndexBuildResult Build(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NTFS MFT 인덱싱은 Windows에서만 사용할 수 있습니다.");
        }

        var normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var volumeGroups = normalizedRoots.GroupBy(
            root => Path.GetPathRoot(root)
                ?? throw new NtfsMftUnavailableException(root, "경로의 볼륨 루트를 확인할 수 없습니다."),
            StringComparer.OrdinalIgnoreCase);

        var entries = new List<FileIndexEntry>();
        var checkpoints = new List<FileIndexCheckpoint>();
        var warnings = new List<string>();
        long quarantinedRecordCount = 0;
        foreach (var volumeGroup in volumeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volumeResult = BuildVolume(
                volumeGroup.Key,
                volumeGroup.ToList(),
                cancellationToken);
            entries.AddRange(volumeResult.Entries);
            checkpoints.AddRange(volumeResult.Checkpoints);
            quarantinedRecordCount += volumeResult.SkippedPathCount;
            AddWarnings(warnings, volumeResult.Warnings);
        }

        return new FileIndexBuildResult(entries, checkpoints, warnings, quarantinedRecordCount);
    }

    private static FileIndexBuildResult BuildVolume(
        string volumeRoot,
        IReadOnlyList<string> configuredRoots,
        CancellationToken cancellationToken)
    {
        foreach (var configuredRoot in configuredRoots)
        {
            if (!Directory.Exists(configuredRoot))
            {
                throw new NtfsMftUnavailableException(
                    configuredRoot,
                    "볼륨 또는 인덱스 루트를 사용할 수 없습니다.");
            }
        }

        using var volume = NtfsVolumeSession.Open(volumeRoot);
        var enumeration = volume.Enumerate(cancellationToken);
        var graph = NtfsPathGraphBuilder.Build(
            volume.Identity.RootPath,
            configuredRoots,
            volume.Identity.VolumeId,
            enumeration.Records,
            volume.RootFileReference,
            volume.TryReadMetadata,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var finalIdentity = NtfsVolumeSession.ReadIdentity(volumeRoot);
        if (finalIdentity != volume.Identity)
        {
            throw new IOException("MFT 열거 중 볼륨 신원이 변경되어 새 인덱스 세대를 폐기했습니다.");
        }

        var warnings = new List<string>();
        AddWarnings(warnings, enumeration.Warnings);
        AddWarnings(warnings, graph.Warnings);
        var checkpoint = new FileIndexCheckpoint(
            volume.Identity.RootPath,
            volume.Identity.VolumeId,
            volume.Identity.FileSystem,
            JournalId: null,
            NextUsn: null,
            DateTimeOffset.UtcNow,
            volume.Identity.SerialNumber);
        return new FileIndexBuildResult(
            graph.Entries,
            [checkpoint],
            warnings,
            enumeration.QuarantinedRecordCount + graph.QuarantinedRecordCount);
    }

    private static void AddWarnings(List<string> destination, IEnumerable<string> source)
    {
        foreach (var warning in source)
        {
            if (destination.Count >= 100)
            {
                break;
            }

            destination.Add(warning);
        }
    }
}

internal sealed class NtfsMftUnavailableException : IOException
{
    public NtfsMftUnavailableException(string path, string message, Exception? innerException = null)
        : base($"{path}: {message}", innerException)
    {
    }
}

internal readonly record struct NtfsVolumeIdentity(
    string RootPath,
    string VolumeId,
    string FileSystem,
    uint SerialNumber);

internal sealed record NtfsMftEnumerationResult(
    IReadOnlyDictionary<NtfsFileReference, NtfsUsnRecord> Records,
    IReadOnlyList<string> Warnings,
    long QuarantinedRecordCount);

internal sealed class NtfsVolumeSession : IDisposable
{
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const int ErrorHandleEof = 38;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorPrivilegeNotHeld = 1314;
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int EnumerationBufferSize = 256 * 1024;

    private readonly SafeFileHandle volumeHandle;

    private NtfsVolumeSession(
        SafeFileHandle volumeHandle,
        NtfsVolumeIdentity identity,
        NtfsFileReference? rootFileReference)
    {
        this.volumeHandle = volumeHandle;
        Identity = identity;
        RootFileReference = rootFileReference;
    }

    public NtfsVolumeIdentity Identity { get; }

    public NtfsFileReference? RootFileReference { get; }

    public static NtfsVolumeSession Open(string volumeRoot)
    {
        var identity = ReadIdentity(volumeRoot);
        if (!string.Equals(identity.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new NtfsMftUnavailableException(
                volumeRoot,
                $"파일 시스템 {identity.FileSystem}은 MFT 열거를 지원하지 않습니다.");
        }

        var handle = NativeMethods.CreateFile(
            GetVolumeDevicePath(identity),
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw CreateNativeException(volumeRoot, "볼륨 핸들을 열지 못했습니다", error);
        }

        var rootReference = TryReadRootReference(identity.RootPath);
        return new NtfsVolumeSession(handle, identity, rootReference);
    }

    private static string GetVolumeDevicePath(NtfsVolumeIdentity identity)
    {
        var root = identity.RootPath;
        return root.Length >= 2 && root[1] == Path.VolumeSeparatorChar
            ? $@"\\.\{root[..2]}"
            : identity.VolumeId.TrimEnd(Path.DirectorySeparatorChar);
    }

    public NtfsMftEnumerationResult Enumerate(CancellationToken cancellationToken)
    {
        var records = new Dictionary<NtfsFileReference, NtfsUsnRecord>();
        var ambiguousReferences = new HashSet<NtfsFileReference>();
        var warnings = new List<string>();
        var buffer = new byte[EnumerationBufferSize];
        var input = new MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.DeviceIoControl(
                    volumeHandle,
                    FsctlEnumUsnData,
                    ref input,
                    (uint)Marshal.SizeOf<MftEnumDataV0>(),
                    buffer,
                    (uint)buffer.Length,
                    out var bytesReturned,
                    IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorHandleEof)
                {
                    break;
                }

                throw CreateNativeException(Identity.RootPath, "MFT 레코드를 열거하지 못했습니다", error);
            }

            if (bytesReturned > buffer.Length)
            {
                throw new InvalidDataException("NTFS가 출력 버퍼보다 큰 MFT 응답 길이를 반환했습니다.");
            }

            var batch = NtfsUsnRecordParser.ParseBatch(buffer.AsSpan(0, checked((int)bytesReturned)));
            if (batch.NextFileReferenceNumber <= input.StartFileReferenceNumber)
            {
                throw new InvalidDataException("NTFS MFT 열거 커서가 진행하지 않았습니다.");
            }

            foreach (var record in batch.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ambiguousReferences.Contains(record.FileReference))
                {
                    continue;
                }

                if (records.TryGetValue(record.FileReference, out var existing)
                    && existing != record)
                {
                    records.Remove(record.FileReference);
                    ambiguousReferences.Add(record.FileReference);
                    if (warnings.Count < 100)
                    {
                        warnings.Add($"중복된 FRN 0x{record.FileReference.High:X16}{record.FileReference.Low:X16} 레코드를 격리했습니다.");
                    }

                    continue;
                }

                records[record.FileReference] = record;
            }

            input.StartFileReferenceNumber = batch.NextFileReferenceNumber;
        }

        return new NtfsMftEnumerationResult(records, warnings, ambiguousReferences.Count);
    }

    public NtfsFileMetadata? TryReadMetadata(NtfsUsnRecord record)
    {
        var descriptor = FileIdDescriptor.Create(record.FileReference, record.MajorVersion == 3);
        using var fileHandle = NativeMethods.OpenFileById(
            volumeHandle,
            ref descriptor,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (fileHandle.IsInvalid
            || !NativeMethods.GetFileInformationByHandle(fileHandle, out var information))
        {
            return null;
        }

        long? size = null;
        if (!record.IsDirectory)
        {
            var unsignedSize = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
            if (unsignedSize <= long.MaxValue)
            {
                size = (long)unsignedSize;
            }
        }

        return new NtfsFileMetadata(size, TryConvertFileTime(information.LastWriteTimeHigh, information.LastWriteTimeLow));
    }

    public static NtfsVolumeIdentity ReadIdentity(string volumeRoot)
    {
        var normalizedRoot = Path.GetFullPath(volumeRoot);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedRoot += Path.DirectorySeparatorChar;
        }

        var volumeName = new StringBuilder(64);
        if (!NativeMethods.GetVolumeNameForVolumeMountPoint(normalizedRoot, volumeName, volumeName.Capacity))
        {
            var error = Marshal.GetLastWin32Error();
            throw CreateNativeException(normalizedRoot, "볼륨 GUID를 확인하지 못했습니다", error);
        }

        var label = new StringBuilder(261);
        var fileSystem = new StringBuilder(32);
        if (!NativeMethods.GetVolumeInformation(
                normalizedRoot,
                label,
                label.Capacity,
                out var serialNumber,
                out _,
                out _,
                fileSystem,
                fileSystem.Capacity))
        {
            var error = Marshal.GetLastWin32Error();
            throw CreateNativeException(normalizedRoot, "볼륨 정보를 확인하지 못했습니다", error);
        }

        return new NtfsVolumeIdentity(
            normalizedRoot,
            volumeName.ToString().TrimEnd(Path.DirectorySeparatorChar),
            fileSystem.ToString(),
            serialNumber);
    }

    public void Dispose() => volumeHandle.Dispose();

    private static NtfsFileReference? TryReadRootReference(string rootPath)
    {
        using var rootHandle = NativeMethods.CreateFile(
            rootPath,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (rootHandle.IsInvalid
            || !NativeMethods.GetFileInformationByHandle(rootHandle, out var information))
        {
            return null;
        }

        return new NtfsFileReference(
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static DateTimeOffset? TryConvertFileTime(uint high, uint low)
    {
        var value = ((long)high << 32) | low;
        if (value <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(value));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static Exception CreateNativeException(string path, string operation, int error)
    {
        var win32 = new Win32Exception(error);
        var message = $"{operation}: {win32.Message} (Win32 {error}).";
        return error is ErrorAccessDenied
            or ErrorInvalidFunction
            or ErrorNotSupported
            or ErrorJournalNotActive
            or ErrorPrivilegeNotHeld
            ? new NtfsMftUnavailableException(path, message, win32)
            : new IOException($"{path}: {message}", win32);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct MftEnumDataV0
    {
        [FieldOffset(0)]
        public ulong StartFileReferenceNumber;

        [FieldOffset(8)]
        public long LowUsn;

        [FieldOffset(16)]
        public long HighUsn;
    }

    internal enum FileIdType : uint
    {
        FileId = 0,
        ExtendedFileId = 2
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public FileIdType Type;

        [FieldOffset(8)]
        public ulong Low;

        [FieldOffset(16)]
        public ulong High;

        public static FileIdDescriptor Create(NtfsFileReference reference, bool extended) =>
            new()
            {
                Size = 24,
                Type = extended ? FileIdType.ExtendedFileId : FileIdType.FileId,
                Low = reference.Low,
                High = reference.High
            };
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct ByHandleFileInformation
    {
        [FieldOffset(20)]
        public uint LastWriteTimeLow;

        [FieldOffset(24)]
        public uint LastWriteTimeHigh;

        [FieldOffset(32)]
        public uint FileSizeHigh;

        [FieldOffset(36)]
        public uint FileSizeLow;

        [FieldOffset(44)]
        public uint FileIndexHigh;

        [FieldOffset(48)]
        public uint FileIndexLow;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            ref MftEnumDataV0 input,
            uint inputSize,
            [In, Out] byte[] output,
            uint outputSize,
            out uint bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeFileHandle OpenFileById(
            SafeFileHandle volumeHint,
            ref FileIdDescriptor fileId,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint flagsAndAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeNameForVolumeMountPoint(
            string volumeMountPoint,
            StringBuilder volumeName,
            int bufferLength);

        [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeName,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemName,
            int fileSystemNameSize);
    }
}
