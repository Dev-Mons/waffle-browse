using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Waffle.Browse.Core.Search.Indexing;

internal static class NamedPipeFileIndexSecurity
{
    internal const string AppExecutableName = "Waffle.Browse.App.exe";
    internal const string IndexerExecutableName = "Waffle.Browse.Indexer.exe";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint LabelSecurityInformation = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumProcessImagePathChars = 32_768;

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CreateServerSecurity()
    {
        EnsureWindows();
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User
            ?? throw new UnauthorizedAccessException("현재 Windows 사용자 SID를 확인하지 못했습니다.");
        var security = new PipeSecurity();

        // Remote network tokens are denied before the same-account allow ACE.
        // Both peers also verify account and terminal-session identity
        // immediately after connecting.
        security.SetSecurityDescriptorSddlForm(
            $"D:P(D;;FA;;;NU)(A;;FA;;;{userSid.Value})");
        return security;
    }

    [SupportedOSPlatform("windows")]
    internal static void ApplyMediumIntegrityLabel(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        EnsureWindows();
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                "S:(ML;;NW;;;ME)",
                1,
                out var securityDescriptor,
                out _))
        {
            throw NativeFailure("Named-pipe medium integrity descriptor를 만들지 못했습니다.");
        }

        try
        {
            if (!GetSecurityDescriptorSacl(
                    securityDescriptor,
                    out var saclPresent,
                    out var sacl,
                    out _)
                || !saclPresent
                || sacl == IntPtr.Zero)
            {
                throw NativeFailure("Named-pipe medium integrity ACL을 읽지 못했습니다.");
            }

            var error = SetSecurityInfo(
                pipeHandle.DangerousGetHandle(),
                SecurityObjectType.KernelObject,
                LabelSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                sacl);
            if (error != 0)
            {
                var detail = new Win32Exception(checked((int)error));
                throw new IOException(
                    $"Named-pipe medium integrity label을 적용하지 못했습니다. {detail.Message} (Win32 {error})",
                    detail);
            }
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyServer(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        EnsureWindows();
        if (!pipe.IsConnected
            || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw NativeFailure("Named-pipe server process를 확인하지 못했습니다.");
        }

        VerifyPeer(
            processId,
            requireElevated: true,
            expectedExecutableName: IndexerExecutableName,
            peerName: "server");
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyClient(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        EnsureWindows();
        if (!pipe.IsConnected
            || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw NativeFailure("Named-pipe client process를 확인하지 못했습니다.");
        }

        VerifyPeer(
            processId,
            requireElevated: false,
            expectedExecutableName: AppExecutableName,
            peerName: "client");
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyCurrentProcessIsElevated()
    {
        EnsureWindows();
        using var process = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            GetCurrentProcessId());
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            throw NativeFailure("Waffle 인덱서 helper token을 열지 못했습니다.");
        }

        using (token)
        {
            if (!IsElevated(token))
            {
                throw new UnauthorizedAccessException(
                    "Waffle 인덱서 helper는 승격된 token으로 실행해야 합니다.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyRequestRoots(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        EnsureWindows();
        var drives = new List<NamedPipeDriveSecurityInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var isReady = drive.IsReady;
                drives.Add(new NamedPipeDriveSecurityInfo(
                    drive.RootDirectory.FullName,
                    drive.DriveType,
                    isReady,
                    isReady ? drive.DriveFormat : null));
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException)
            {
                // A drive whose identity cannot be read is not eligible for a
                // privileged raw-volume request.
            }
        }

        if (!AreRequestRootsAllowed(roots, drives))
        {
            throw new UnauthorizedAccessException(
                "Waffle 인덱서 helper 요청은 준비된 로컬 NTFS 고정 드라이브 루트만 허용합니다.");
        }
    }

    internal static bool AreRequestRootsAllowed(
        IReadOnlyList<string> roots,
        IReadOnlyList<NamedPipeDriveSecurityInfo> drives)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(drives);
        if (roots.Count == 0)
        {
            return false;
        }

        var allowedRoots = drives
            .Where(drive => drive.IsReady
                && drive.DriveType == DriveType.Fixed
                && string.Equals(drive.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
                && TryNormalizeDriveRoot(drive.RootPath, out _))
            .Select(drive =>
            {
                _ = TryNormalizeDriveRoot(drive.RootPath, out var normalized);
                return normalized;
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return roots.All(root =>
            TryNormalizeDriveRoot(root, out var normalized)
            && allowedRoots.Contains(normalized));
    }

    internal static bool IsExpectedPeerImagePath(
        string actualPeerImagePath,
        string currentProcessImagePath,
        string expectedPeerExecutableName)
    {
        if (string.IsNullOrWhiteSpace(actualPeerImagePath)
            || string.IsNullOrWhiteSpace(currentProcessImagePath)
            || string.IsNullOrWhiteSpace(expectedPeerExecutableName)
            || Path.GetFileName(expectedPeerExecutableName) != expectedPeerExecutableName)
        {
            return false;
        }

        try
        {
            var currentImage = Path.GetFullPath(currentProcessImagePath);
            var currentDirectory = Path.GetDirectoryName(currentImage);
            if (string.IsNullOrWhiteSpace(currentDirectory))
            {
                return false;
            }

            var expected = Path.GetFullPath(Path.Combine(
                currentDirectory,
                expectedPeerExecutableName));
            var actual = Path.GetFullPath(actualPeerImagePath);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyPeer(
        uint processId,
        bool requireElevated,
        string expectedExecutableName,
        string peerName)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            throw NativeFailure($"Named-pipe {peerName} token을 열지 못했습니다.");
        }

        using (token)
        using (var serverIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var currentIdentity = WindowsIdentity.GetCurrent())
        {
            if (serverIdentity.User is null
                || currentIdentity.User is null
                || !serverIdentity.User.Equals(currentIdentity.User)
                || !IsCurrentTerminalSession(processId))
            {
                throw new UnauthorizedAccessException(
                    $"Waffle 인덱서 {peerName}가 현재 사용자와 같은 로컬 session에서 실행되지 않았습니다.");
            }

            if (requireElevated && !IsElevated(token))
            {
                throw new UnauthorizedAccessException(
                    $"Waffle 인덱서 {peerName}가 승격된 token으로 실행되지 않았습니다.");
            }

            var currentProcessImage = Environment.ProcessPath;
            var peerProcessImage = ReadProcessImagePath(process);
            if (currentProcessImage is null
                || !IsExpectedPeerImagePath(
                    peerProcessImage,
                    currentProcessImage,
                    expectedExecutableName))
            {
                throw new UnauthorizedAccessException(
                    $"Waffle 인덱서 {peerName} 실행 파일이 허용된 배포 경로와 일치하지 않습니다.");
            }
        }
    }

    private static string ReadProcessImagePath(SafeProcessHandle process)
    {
        var capacity = 512;
        while (capacity <= MaximumProcessImagePathChars)
        {
            var path = new StringBuilder(capacity);
            var length = checked((uint)path.Capacity);
            if (QueryFullProcessImageNameW(process, 0, path, ref length))
            {
                if (length == 0)
                {
                    throw new IOException("Named-pipe peer process image 경로가 비어 있습니다.");
                }

                return path.ToString(0, checked((int)length));
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer || capacity == MaximumProcessImagePathChars)
            {
                var detail = new Win32Exception(error);
                throw new IOException(
                    $"Named-pipe peer process image 경로를 읽지 못했습니다. {detail.Message} (Win32 {error})",
                    detail);
            }

            capacity = Math.Min(capacity * 2, MaximumProcessImagePathChars);
        }

        throw new IOException("Named-pipe peer process image 경로가 최대 길이를 초과했습니다.");
    }

    private static bool TryNormalizeDriveRoot(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(pathRoot)
                || pathRoot.Length < 3
                || !char.IsAsciiLetter(pathRoot[0])
                || pathRoot[1] != ':'
                || pathRoot[2] is not ('\\' or '/')
                || !string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            normalized = $"{char.ToUpperInvariant(pathRoot[0])}:{Path.DirectorySeparatorChar}";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentTerminalSession(uint peerProcessId)
    {
        if (!ProcessIdToSessionId(peerProcessId, out var peerSessionId)
            || !ProcessIdToSessionId(GetCurrentProcessId(), out var currentSessionId))
        {
            throw NativeFailure("Named-pipe terminal session을 확인하지 못했습니다.");
        }

        return peerSessionId == currentSessionId;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(
                token,
                TokenInformationClass.TokenElevation,
                out var elevation,
                (uint)Marshal.SizeOf<TokenElevation>(),
                out _))
        {
            throw NativeFailure("Named-pipe server elevation 상태를 확인하지 못했습니다.");
        }

        return elevation.TokenIsElevated != 0;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Waffle 인덱서 named pipe 보안은 Windows에서만 사용할 수 있습니다.");
        }
    }

    private static IOException NativeFailure(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        var detail = new Win32Exception(error);
        return new IOException($"{operation} {detail.Message} (Win32 {error})", detail);
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    private enum SecurityObjectType
    {
        KernelObject = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public uint TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle processHandle,
        uint flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        out TokenElevation tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool saclPresent,
        out IntPtr sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        SecurityObjectType objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed record NamedPipeDriveSecurityInfo(
    string RootPath,
    DriveType DriveType,
    bool IsReady,
    string? FileSystem);
