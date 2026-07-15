using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Waffle.Browse.Core.Search.Indexing.Ntfs;

internal sealed class WindowsNtfsVolumeAccessorFactory : INtfsVolumeAccessorFactory
{
    public INtfsVolumeAccessor Open(string rootPath) => new WindowsNtfsVolumeAccessor(rootPath);
}

internal sealed class WindowsNtfsVolumeAccessor : INtfsVolumeAccessor
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileSupportsOpenByFileId = 0x01000000;
    private const uint FileSupportsUsnJournal = 0x02000000;
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorHandleEof = 38;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorMoreData = 234;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalEntryDeleted = 1181;
    private const int ErrorNotFound = 1168;
    private const int MftBufferSize = 1024 * 1024;

    private readonly SafeFileHandle volumeHandle;
    private readonly byte[] mftBuffer = new byte[MftBufferSize];

    public WindowsNtfsVolumeAccessor(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NTFS MFT 인덱싱은 Windows에서만 사용할 수 있습니다.");
        }

        var normalizedRoot = NormalizeDriveRoot(rootPath);
        var volumeDevicePath = $@"\\.\{normalizedRoot[..2]}";
        volumeHandle = CreateFileW(
            volumeDevicePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (volumeHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            volumeHandle.Dispose();
            throw CreateNativeException($"볼륨 핸들을 열지 못했습니다: {volumeDevicePath}", error);
        }

        try
        {
            var volumeInformation = ReadVolumeInformation(volumeHandle);
            if (!string.Equals(volumeInformation.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"{normalizedRoot} 볼륨은 NTFS가 아닙니다.");
            }

            var requiredFeatures = FileSupportsOpenByFileId | FileSupportsUsnJournal;
            if ((volumeInformation.FileSystemFlags & requiredFeatures) != requiredFeatures)
            {
                throw new NotSupportedException($"{normalizedRoot} 볼륨은 MFT/파일 ID 인덱싱을 지원하지 않습니다.");
            }

            var volumeId = ReadVolumeName(normalizedRoot);
            var rootFileReferenceNumber = ReadRootFileReferenceNumber(normalizedRoot);
            Identity = new NtfsVolumeIdentity(
                normalizedRoot,
                volumeId,
                volumeInformation.FileSystem,
                volumeInformation.SerialNumber,
                rootFileReferenceNumber);
        }
        catch
        {
            volumeHandle.Dispose();
            throw;
        }
    }

    public NtfsVolumeIdentity Identity { get; }

    public NtfsMftBatch? ReadMftBatch(
        ulong startFileReferenceNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = new MftEnumDataV0
        {
            StartFileReferenceNumber = startFileReferenceNumber,
            LowUsn = 0,
            HighUsn = long.MaxValue
        };

        var succeeded = DeviceIoControlMft(
            volumeHandle,
            FsctlEnumUsnData,
            ref input,
            (uint)Marshal.SizeOf<MftEnumDataV0>(),
            mftBuffer,
            (uint)mftBuffer.Length,
            out var bytesReturned,
            IntPtr.Zero);
        var error = succeeded ? 0 : Marshal.GetLastWin32Error();
        cancellationToken.ThrowIfCancellationRequested();

        if (!succeeded && error == ErrorHandleEof)
        {
            return null;
        }

        if (!succeeded && error != ErrorMoreData)
        {
            throw CreateNativeException("NTFS MFT 레코드를 열거하지 못했습니다.", error);
        }

        if (bytesReturned > mftBuffer.Length)
        {
            throw new InvalidDataException("NTFS MFT 반환 길이가 출력 버퍼를 초과했습니다.");
        }

        if (bytesReturned == 0)
        {
            throw new InvalidDataException("NTFS MFT 열거가 데이터 없이 중단되었습니다.");
        }

        return NtfsUsnRecordParser.Parse(mftBuffer.AsSpan(0, checked((int)bytesReturned)));
    }

    public NtfsFileMetadata? TryReadMetadata(
        FileReferenceId fileReferenceNumber,
        bool isDirectory)
    {
        _ = isDirectory;
        var descriptor = FileIdDescriptor.Create(fileReferenceNumber);
        using var handle = OpenFileById(
            volumeHandle,
            ref descriptor,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return IsPerFileMetadataFailure(error)
                ? null
                : throw CreateNativeException(
                    $"파일 ID {fileReferenceNumber}의 메타데이터를 읽지 못했습니다.",
                    error);
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            return IsPerFileMetadataFailure(error)
                ? null
                : throw CreateNativeException(
                    $"파일 ID {fileReferenceNumber}의 메타데이터를 읽지 못했습니다.",
                    error);
        }

        var unsignedSize = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        long? size = unsignedSize <= long.MaxValue ? (long)unsignedSize : null;
        var fileTime = ((ulong)information.LastWriteTime.High << 32) | information.LastWriteTime.Low;
        DateTimeOffset? modifiedAt = null;
        if (fileTime <= long.MaxValue)
        {
            try
            {
                modifiedAt = DateTimeOffset.FromFileTime((long)fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return new NtfsFileMetadata(size, modifiedAt);
    }

    public NtfsJournalCheckpoint? TryReadJournalCheckpoint()
    {
        var state = TryReadJournalState();
        return state is null ? null : new NtfsJournalCheckpoint(state.JournalId, state.NextUsn);
    }

    public NtfsJournalState? TryReadJournalState()
    {
        var buffer = new byte[128];
        var succeeded = DeviceIoControlNoInput(
            volumeHandle,
            FsctlQueryUsnJournal,
            IntPtr.Zero,
            0,
            buffer,
            (uint)buffer.Length,
            out var bytesReturned,
            IntPtr.Zero);
        var error = succeeded ? 0 : Marshal.GetLastWin32Error();
        if (!succeeded)
        {
            throw CreateJournalStateException(error);
        }

        if (bytesReturned < 56 || bytesReturned > buffer.Length)
        {
            throw new InvalidDataException("NTFS 변경 저널 체크포인트 길이가 올바르지 않습니다.");
        }

        return new NtfsJournalState(
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(32, 8)));
    }

    internal static Exception CreateJournalStateException(int error)
    {
        if (error == ErrorJournalNotActive)
        {
            var detail = new Win32Exception(error);
            return new NotSupportedException(
                $"NTFS 변경 저널이 활성화되어 있지 않습니다. {detail.Message} (Win32 {error})",
                detail);
        }

        if (error is ErrorInvalidParameter
            or ErrorJournalDeleteInProgress
            or ErrorJournalEntryDeleted)
        {
            var detail = new Win32Exception(error);
            return new NtfsJournalInvalidatedException(
                $"NTFS 변경 저널 시작점을 사용할 수 없습니다. {detail.Message} (Win32 {error})",
                detail);
        }

        return CreateNativeException("NTFS 변경 저널 체크포인트를 읽지 못했습니다.", error);
    }

    public NtfsUsnJournalBatch ReadUsnJournalBatch(
        long startUsn,
        ulong journalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = new ReadUsnJournalDataV0
        {
            StartUsn = startUsn,
            ReasonMask = uint.MaxValue,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            JournalId = journalId
        };
        var succeeded = DeviceIoControlReadJournal(
            volumeHandle,
            FsctlReadUsnJournal,
            ref input,
            (uint)Marshal.SizeOf<ReadUsnJournalDataV0>(),
            mftBuffer,
            (uint)mftBuffer.Length,
            out var bytesReturned,
            IntPtr.Zero);
        var error = succeeded ? 0 : Marshal.GetLastWin32Error();
        cancellationToken.ThrowIfCancellationRequested();
        if (!succeeded && error != ErrorMoreData)
        {
            if (error is ErrorInvalidParameter or ErrorJournalDeleteInProgress or ErrorJournalNotActive or ErrorJournalEntryDeleted)
            {
                var detail = new Win32Exception(error);
                throw new NtfsJournalInvalidatedException(
                    $"NTFS 변경 저널 체크포인트가 무효화되었습니다. {detail.Message} (Win32 {error})",
                    detail);
            }

            throw CreateNativeException("NTFS 변경 저널을 읽지 못했습니다.", error);
        }

        if (bytesReturned < sizeof(long) || bytesReturned > mftBuffer.Length)
        {
            throw new InvalidDataException("NTFS 변경 저널 반환 길이가 올바르지 않습니다.");
        }

        return NtfsUsnRecordParser.ParseJournal(
            mftBuffer.AsSpan(0, checked((int)bytesReturned)));
    }

    public FileIndexEntry? TryReadCurrentEntry(FileReferenceId fileReferenceNumber) =>
        TryReadCurrentEntries(fileReferenceNumber).FirstOrDefault();

    public IReadOnlyList<FileIndexEntry> TryReadCurrentEntries(FileReferenceId fileReferenceNumber)
    {
        var descriptor = FileIdDescriptor.Create(fileReferenceNumber);
        using var handle = OpenFileById(
            volumeHandle,
            ref descriptor,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return IsPerFileMetadataFailure(error)
                ? []
                : throw CreateNativeException(
                    $"파일 ID {fileReferenceNumber}의 현재 경로를 열지 못했습니다.",
                    error);
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            return IsPerFileMetadataFailure(error)
                ? []
                : throw CreateNativeException(
                    $"파일 ID {fileReferenceNumber}의 현재 정보를 읽지 못했습니다.",
                    error);
        }

        var fullPath = ReadFinalPath(handle);
        if (fullPath is null || !IsWithinRoot(fullPath, Identity.RootPath))
        {
            return [];
        }

        var isDirectory = ((FileAttributes)information.FileAttributes).HasFlag(FileAttributes.Directory);
        var metadata = ReadMetadata(information);
        var paths = !isDirectory && information.NumberOfLinks > 1
            ? ReadHardLinkPaths(fullPath)
            : [fullPath];
        return paths
            .Where(path => IsWithinRoot(path, Identity.RootPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileIndexEntry(
                path,
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Path.GetDirectoryName(path) ?? string.Empty,
                isDirectory ? SearchItemKind.Folder : SearchItemKind.File,
                isDirectory ? null : metadata.Size,
                metadata.ModifiedAt,
                Identity.VolumeId,
                fileReferenceNumber))
            .ToList();
    }

    public void EnsureIdentityUnchanged()
    {
        var current = ReadVolumeInformation(volumeHandle);
        if (current.SerialNumber != Identity.SerialNumber
            || !string.Equals(current.FileSystem, Identity.FileSystem, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ReadVolumeName(Identity.RootPath), Identity.VolumeId, StringComparison.OrdinalIgnoreCase)
            || ReadRootFileReferenceNumber(Identity.RootPath) != Identity.RootFileReferenceNumber)
        {
            throw new IOException($"{Identity.RootPath} 볼륨 ID가 MFT 인덱싱 중 변경되었습니다.");
        }
    }

    public void Dispose() => volumeHandle.Dispose();

    private static string NormalizeDriveRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullPath = Path.GetFullPath(rootPath);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot)
            || pathRoot.Length < 2
            || pathRoot[1] != ':'
            || !string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("NTFS MFT 소스는 드라이브 루트만 직접 인덱싱할 수 있습니다.");
        }

        return pathRoot;
    }

    private static NativeVolumeInformation ReadVolumeInformation(SafeFileHandle handle)
    {
        var fileSystemName = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(
                handle,
                null,
                0,
                out var serialNumber,
                out _,
                out var fileSystemFlags,
                fileSystemName,
                (uint)fileSystemName.Capacity))
        {
            throw CreateNativeException("볼륨 정보를 읽지 못했습니다.", Marshal.GetLastWin32Error());
        }

        return new NativeVolumeInformation(serialNumber, fileSystemFlags, fileSystemName.ToString());
    }

    private static string ReadVolumeName(string rootPath)
    {
        var volumeName = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(rootPath, volumeName, (uint)volumeName.Capacity))
        {
            throw CreateNativeException(
                $"{rootPath} 볼륨 이름을 읽지 못했습니다.",
                Marshal.GetLastWin32Error());
        }

        return volumeName.ToString();
    }

    private static FileReferenceId ReadRootFileReferenceNumber(string rootPath)
    {
        using var rootHandle = CreateFileW(
            rootPath,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (rootHandle.IsInvalid)
        {
            throw CreateNativeException(
                $"{rootPath} 루트 파일 ID를 열지 못했습니다.",
                Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(rootHandle, out var information))
        {
            throw CreateNativeException(
                $"{rootPath} 루트 파일 ID를 읽지 못했습니다.",
                Marshal.GetLastWin32Error());
        }

        return new FileReferenceId(
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static bool IsPerFileMetadataFailure(int error) =>
        error is ErrorFileNotFound
            or ErrorPathNotFound
            or ErrorAccessDenied
            or ErrorNotSupported
            or ErrorInvalidParameter
            or ErrorNotFound;

    private static NtfsFileMetadata ReadMetadata(ByHandleFileInformation information)
    {
        var unsignedSize = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        long? size = unsignedSize <= long.MaxValue ? (long)unsignedSize : null;
        var fileTime = ((ulong)information.LastWriteTime.High << 32) | information.LastWriteTime.Low;
        DateTimeOffset? modifiedAt = null;
        if (fileTime <= long.MaxValue)
        {
            try
            {
                modifiedAt = DateTimeOffset.FromFileTime((long)fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return new NtfsFileMetadata(size, modifiedAt);
    }

    private static string? ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                var error = Marshal.GetLastWin32Error();
                return IsPerFileMetadataFailure(error)
                    ? null
                    : throw CreateNativeException("파일 ID의 최종 경로를 읽지 못했습니다.", error);
            }

            if (length < buffer.Capacity)
            {
                var path = buffer.ToString();
                return path.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? path[4..]
                    : path;
            }

            if (length > 32768)
            {
                return null;
            }

            capacity = checked((int)length);
        }

        return null;
    }

    private IReadOnlyList<string> ReadHardLinkPaths(string path)
    {
        uint length = 512;
        SafeFindHandle? findHandle = null;
        StringBuilder? buffer = null;
        while (findHandle is null)
        {
            buffer = new StringBuilder(checked((int)length));
            var requestedLength = length;
            var candidate = FindFirstFileNameW(path, 0, ref requestedLength, buffer);
            if (!candidate.IsInvalid)
            {
                findHandle = candidate;
                break;
            }

            var error = Marshal.GetLastWin32Error();
            candidate.Dispose();
            if (error != ErrorMoreData || requestedLength <= length || requestedLength > 32768)
            {
                throw CreateNativeException($"{path} 하드 링크 이름을 열거하지 못했습니다.", error);
            }

            length = requestedLength;
        }

        using (findHandle)
        {
            var paths = new List<string> { ToDrivePath(buffer!.ToString()) };
            while (true)
            {
                length = 512;
                buffer = new StringBuilder(checked((int)length));
                while (!FindNextFileNameW(findHandle, ref length, buffer))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorHandleEof)
                    {
                        return paths;
                    }

                    if (error != ErrorMoreData || length > 32768)
                    {
                        throw CreateNativeException($"{path} 하드 링크 이름을 열거하지 못했습니다.", error);
                    }

                    buffer = new StringBuilder(checked((int)length));
                }

                paths.Add(ToDrivePath(buffer.ToString()));
            }
        }
    }

    private string ToDrivePath(string volumeRelativePath) =>
        Path.Combine(
            Identity.RootPath,
            volumeRelativePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool IsWithinRoot(string path, string rootPath) =>
        path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);

    private static Exception CreateNativeException(string operation, int error)
    {
        var detail = new Win32Exception(error);
        var message = $"{operation} {detail.Message} (Win32 {error})";
        return error == ErrorAccessDenied
            ? new UnauthorizedAccessException(message, detail)
            : error is ErrorInvalidFunction or ErrorNotSupported
                ? new NotSupportedException(message, detail)
            : new IOException(message, detail);
    }

    private sealed record NativeVolumeInformation(
        uint SerialNumber,
        uint FileSystemFlags,
        string FileSystem);

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    private enum FileIdType : uint
    {
        FileId = 0,
        ExtendedFileId = 2
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct MftEnumDataV0
    {
        [FieldOffset(0)]
        public ulong StartFileReferenceNumber;

        [FieldOffset(8)]
        public long LowUsn;

        [FieldOffset(16)]
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct ReadUsnJournalDataV0
    {
        [FieldOffset(0)]
        public long StartUsn;

        [FieldOffset(8)]
        public uint ReasonMask;

        [FieldOffset(12)]
        public uint ReturnOnlyOnClose;

        [FieldOffset(16)]
        public ulong Timeout;

        [FieldOffset(24)]
        public ulong BytesToWaitFor;

        [FieldOffset(32)]
        public ulong JournalId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public FileIdType Type;

        [FieldOffset(8)]
        public ulong Low;

        [FieldOffset(16)]
        public ulong High;

        public static FileIdDescriptor Create(FileReferenceId fileReferenceNumber) =>
            new()
            {
                Size = 24,
                Type = fileReferenceNumber.Is128Bit ? FileIdType.ExtendedFileId : FileIdType.FileId,
                Low = fileReferenceNumber.Low,
                High = fileReferenceNumber.High
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint,
        ref FileIdDescriptor lpFileId,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileNameW(
        string lpFileName,
        uint dwFlags,
        ref uint stringLength,
        StringBuilder linkName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileNameW(
        SafeFindHandle hFindStream,
        ref uint stringLength,
        StringBuilder linkName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle hFile,
        StringBuilder? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlMft(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MftEnumDataV0 lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlReadJournal(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref ReadUsnJournalDataV0 lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlNoInput(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
