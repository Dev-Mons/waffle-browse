using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Waffle.Browse.Core.Search.Indexing;

internal enum IndexerLaunchStatus
{
    Started,
    Declined,
    Unavailable,
    Failed
}

internal sealed record IndexerLaunchResult(
    IndexerLaunchStatus Status,
    string Message,
    Exception? Exception = null)
{
    internal bool Started => Status == IndexerLaunchStatus.Started;
}

internal interface IIndexerProcessLauncher
{
    IndexerLaunchResult Launch();
}

internal sealed class IndexerLaunchCoordinator
{
    internal SemaphoreSlim OperationGate { get; } = new(1, 1);

    internal bool SuppressLaunchPrompt { get; set; }
}

internal sealed class ElevatedIndexerProcessLauncher : IIndexerProcessLauncher
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;
    private const int ErrorPrivilegeNotHeld = 1314;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileWriteData = 0x00000002;
    private const uint FileAppendData = 0x00000004;
    private const uint FileWriteExtendedAttributes = 0x00000010;
    private const uint FileDeleteChild = 0x00000040;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint Delete = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private readonly Func<string, IDisposable?> acquireNativeAotImage;

    private static readonly uint[] DangerousDirectoryAccess =
    [
        FileAddFile,
        FileAddSubdirectory,
        FileWriteExtendedAttributes,
        FileDeleteChild,
        FileWriteAttributes,
        Delete,
        WriteDac,
        WriteOwner
    ];

    private static readonly uint[] DangerousFileAccess =
    [
        FileWriteData,
        FileAppendData,
        FileWriteExtendedAttributes,
        FileWriteAttributes,
        Delete,
        WriteDac,
        WriteOwner
    ];

    internal ElevatedIndexerProcessLauncher(Func<string, IDisposable?> acquireNativeAotImage)
    {
        ArgumentNullException.ThrowIfNull(acquireNativeAotImage);
        this.acquireNativeAotImage = acquireNativeAotImage;
    }

    public IndexerLaunchResult Launch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new IndexerLaunchResult(
                IndexerLaunchStatus.Unavailable,
                "Waffle 인덱서 helper 승격 실행은 Windows에서만 사용할 수 있습니다.");
        }

        IDisposable? imageLease = null;
        try
        {
            if (!TryCreateStartInfo(
                    Environment.ProcessPath,
                    File.Exists,
                    IsProtectedDeployment,
                    TryAcquireImage,
                    out var startInfo))
            {
                return new IndexerLaunchResult(
                    IndexerLaunchStatus.Unavailable,
                    $"보호된 설치 디렉터리에서 app과 같은 위치의 NativeAOT {NamedPipeFileIndexSecurity.IndexerExecutableName}를 확인하지 못했습니다.");
            }

            try
            {
                using var process = Process.Start(startInfo);
                return process is null
                    ? new IndexerLaunchResult(
                        IndexerLaunchStatus.Failed,
                        "Waffle 인덱서 helper 프로세스를 시작하지 못했습니다.")
                    : new IndexerLaunchResult(
                        IndexerLaunchStatus.Started,
                        "Waffle 인덱서 helper 승격 실행을 시작했습니다.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new IndexerLaunchResult(
                    IndexerLaunchStatus.Declined,
                    "사용자가 Waffle 인덱서 helper의 UAC 승격을 취소했습니다.",
                    ex);
            }
            catch (Exception ex) when (ex is Win32Exception
                                       or InvalidOperationException
                                       or FileNotFoundException
                                       or UnauthorizedAccessException)
            {
                return new IndexerLaunchResult(
                    IndexerLaunchStatus.Failed,
                    $"Waffle 인덱서 helper를 승격 실행하지 못했습니다: {ex.Message}",
                    ex);
            }
        }
        finally
        {
            imageLease?.Dispose();
        }

        bool TryAcquireImage(string helperPath)
        {
            imageLease?.Dispose();
            imageLease = acquireNativeAotImage(helperPath);
            return imageLease is not null;
        }
    }

    internal static bool TryCreateStartInfo(
        string? currentProcessPath,
        Func<string, bool> fileExists,
        Func<string, string, bool> isProtectedDeployment,
        Func<string, bool> isNativeAotImage,
        out ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(isProtectedDeployment);
        ArgumentNullException.ThrowIfNull(isNativeAotImage);
        startInfo = new ProcessStartInfo();
        if (string.IsNullOrWhiteSpace(currentProcessPath))
        {
            return false;
        }

        try
        {
            var normalizedAppPath = Path.GetFullPath(currentProcessPath);
            if (!string.Equals(
                    Path.GetFileName(normalizedAppPath),
                    NamedPipeFileIndexSecurity.AppExecutableName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var applicationDirectory = Path.GetDirectoryName(normalizedAppPath);
            if (string.IsNullOrWhiteSpace(applicationDirectory))
            {
                return false;
            }

            var helperPath = Path.Combine(
                applicationDirectory,
                NamedPipeFileIndexSecurity.IndexerExecutableName);
            if (!NamedPipeFileIndexSecurity.IsExpectedPeerImagePath(
                    helperPath,
                    normalizedAppPath,
                    NamedPipeFileIndexSecurity.IndexerExecutableName)
                || !fileExists(helperPath))
            {
                return false;
            }

            // Never turn a user-replaceable portable build into an elevation boundary.
            // The installer must place both peers under a machine-protected location.
            if (!isProtectedDeployment(applicationDirectory, helperPath)
                || !isNativeAotImage(helperPath))
            {
                return false;
            }

            startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = applicationDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Empty
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsProtectedDeployment(
        string directoryPath,
        string helperPath)
    {
        if (!OperatingSystem.IsWindows()
            || !TryGetProgramFilesRoot(directoryPath, out var protectedRoot))
        {
            return false;
        }

        return IsDeploymentReadOnlyForCurrentUser(
            protectedRoot,
            directoryPath,
            helperPath);
    }

    internal static bool IsProgramFilesInstallationDirectory(string directoryPath) =>
        OperatingSystem.IsWindows()
        && TryGetProgramFilesRoot(directoryPath, out _);

    internal static bool IsDeploymentReadOnlyForCurrentUser(
        string protectedRoot,
        string directoryPath,
        string helperPath)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(protectedRoot)
            || string.IsNullOrWhiteSpace(directoryPath)
            || string.IsNullOrWhiteSpace(helperPath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(protectedRoot));
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directoryPath));
            var normalizedHelper = Path.GetFullPath(helperPath);
            if (!IsSameOrDescendant(normalizedDirectory, normalizedRoot)
                || !string.Equals(
                    Path.GetDirectoryName(normalizedHelper),
                    normalizedDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || ContainsReparsePoint(
                    normalizedRoot,
                    normalizedDirectory,
                    normalizedHelper))
            {
                return false;
            }

            return EnumerateDirectoryChain(normalizedRoot, normalizedDirectory)
                    .All(path => IsEveryAccessDenied(
                        path,
                        DangerousDirectoryAccess,
                        FileFlagBackupSemantics))
                && IsDeploymentTreeProtected(normalizedDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryGetProgramFilesRoot(
        string directoryPath,
        out string protectedRoot)
    {
        protectedRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directoryPath));
            string[] protectedRoots =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            ];

            foreach (var candidate in protectedRoots)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var normalizedRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(candidate));
                if (IsSameOrDescendant(normalizedDirectory, normalizedRoot))
                {
                    protectedRoot = normalizedRoot;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }

        return false;
    }

    private static bool ContainsReparsePoint(
        string protectedRoot,
        string directoryPath,
        string helperPath)
    {
        foreach (var current in EnumerateDirectoryChain(protectedRoot, directoryPath))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return (File.GetAttributes(helperPath) & FileAttributes.ReparsePoint) != 0;
    }

    private static IEnumerable<string> EnumerateDirectoryChain(
        string protectedRoot,
        string directoryPath)
    {
        var current = protectedRoot;
        yield return current;

        var relativeDirectory = Path.GetRelativePath(protectedRoot, directoryPath);
        if (string.Equals(relativeDirectory, ".", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var segment in relativeDirectory.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static bool IsDeploymentTreeProtected(string deploymentDirectory)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(deploymentDirectory);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || !IsEveryAccessDenied(
                    directory,
                    DangerousDirectoryAccess,
                    FileFlagBackupSemantics))
            {
                return false;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
                else if (!IsEveryAccessDenied(
                             entry,
                             DangerousFileAccess,
                             flagsAndAttributes: 0))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsEveryAccessDenied(
        string path,
        IReadOnlyList<uint> accessMasks,
        uint flagsAndAttributes)
    {
        foreach (var accessMask in accessMasks)
        {
            using var handle = CreateFileW(
                path,
                accessMask,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                flagsAndAttributes,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return false;
            }

            var error = Marshal.GetLastWin32Error();
            if (error is not ErrorAccessDenied and not ErrorPrivilegeNotHeld)
            {
                // Sharing violations and lookup failures do not prove that the
                // deployment is immutable, so fail closed.
                return false;
            }
        }

        return true;
    }

    private static bool IsSameOrDescendant(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
