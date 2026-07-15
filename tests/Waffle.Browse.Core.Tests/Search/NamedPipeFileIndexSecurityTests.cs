using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.Core.Tests.Search;

internal static class NamedPipeFileIndexSecurityTests
{
    public static void ElevatedLauncherUsesExactSiblingWithoutArguments()
    {
        var deploymentDirectory = Path.Combine(
            Path.GetTempPath(),
            "Waffle Browse Launcher",
            "bin");
        var appImage = Path.Combine(
            deploymentDirectory,
            NamedPipeFileIndexSecurity.AppExecutableName);
        var expectedIndexer = Path.Combine(
            deploymentDirectory,
            NamedPipeFileIndexSecurity.IndexerExecutableName);

        TestAssert.True(
            ElevatedIndexerProcessLauncher.TryCreateStartInfo(
                appImage,
                path => string.Equals(path, expectedIndexer, StringComparison.OrdinalIgnoreCase),
                (_, _) => true,
                _ => true,
                out var startInfo),
            "The launcher should resolve the exact sibling helper executable");
        TestAssert.Equal(expectedIndexer, startInfo.FileName, "The launcher should execute only the sibling indexer");
        TestAssert.Equal(deploymentDirectory, startInfo.WorkingDirectory, "The launcher should use the protected deployment directory");
        TestAssert.True(startInfo.UseShellExecute, "UAC elevation requires shell execution");
        TestAssert.Equal("runas", startInfo.Verb, "The launcher should request explicit UAC consent");
        TestAssert.Equal(string.Empty, startInfo.Arguments, "The helper must not receive command-line arguments");
        TestAssert.Equal(0, startInfo.ArgumentList.Count, "The helper argument list must stay empty");
        TestAssert.Equal(
            System.Diagnostics.ProcessWindowStyle.Hidden,
            startInfo.WindowStyle,
            "The elevated background helper should not open a console window");
    }

    public static void ElevatedLauncherRejectsUnprotectedDeployment()
    {
        var deploymentDirectory = Path.Combine(
            Path.GetTempPath(),
            "Waffle Browse Launcher",
            "portable");
        var appImage = Path.Combine(
            deploymentDirectory,
            NamedPipeFileIndexSecurity.AppExecutableName);

        TestAssert.False(
            ElevatedIndexerProcessLauncher.TryCreateStartInfo(
                appImage,
                _ => true,
                (_, _) => false,
                _ => true,
                out _),
            "A user-replaceable portable helper must never be used as an elevation boundary");
    }

    public static void ElevatedLauncherRejectsManagedHelperImage()
    {
        var deploymentDirectory = Path.Combine(
            Path.GetTempPath(),
            "Waffle Browse Launcher",
            "managed");
        var appImage = Path.Combine(
            deploymentDirectory,
            NamedPipeFileIndexSecurity.AppExecutableName);

        TestAssert.False(
            ElevatedIndexerProcessLauncher.TryCreateStartInfo(
                appImage,
                _ => true,
                (_, _) => true,
                _ => false,
                out _),
            "A managed helper image must never cross the elevation boundary");
    }

    public static void ElevatedLauncherRequiresProgramFilesBoundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return;
        }

        TestAssert.True(
            ElevatedIndexerProcessLauncher.IsProgramFilesInstallationDirectory(
                Path.Combine(programFiles, "Waffle Browse")),
            "A product installation below Program Files should be eligible for explicit UAC launch");
        TestAssert.False(
            ElevatedIndexerProcessLauncher.IsProgramFilesInstallationDirectory(
                Path.TrimEndingDirectorySeparator(programFiles) + "-portable"),
            "A path that merely shares the Program Files prefix must not be treated as protected");
    }

    public static void ElevatedLauncherRejectsWritableDeploymentAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var deploymentDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Waffle Browse Writable Launcher {Guid.NewGuid():N}");
        var helperPath = Path.Combine(
            deploymentDirectory,
            NamedPipeFileIndexSecurity.IndexerExecutableName);
        Directory.CreateDirectory(deploymentDirectory);
        File.WriteAllText(helperPath, "test");
        try
        {
            TestAssert.False(
                ElevatedIndexerProcessLauncher.IsDeploymentReadOnlyForCurrentUser(
                    deploymentDirectory,
                    deploymentDirectory,
                    helperPath),
                "A helper or directory writable by the current user must not cross the UAC boundary");
        }
        finally
        {
            Directory.Delete(deploymentDirectory, recursive: true);
        }
    }

    public static void CrossProcessOperationLockSerializesIndependentOwners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Waffle Browse Operation Lock {Guid.NewGuid():N}");
        var lockPath = Path.Combine(testDirectory, "indexer-operation.lock");
        IndexerCrossProcessOperationLock? first = null;
        IndexerCrossProcessOperationLock? second = null;
        IndexerCrossProcessOperationLock? afterRelease = null;
        try
        {
            first = IndexerCrossProcessOperationLock
                .AcquireAsync(1_000, CancellationToken.None, lockPath)
                .GetAwaiter()
                .GetResult();

            var secondWasBlocked = false;
            try
            {
                second = IndexerCrossProcessOperationLock
                    .AcquireAsync(150, CancellationToken.None, lockPath)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (NotSupportedException)
            {
                secondWasBlocked = true;
            }

            TestAssert.True(
                secondWasBlocked,
                "Independent owners must not use the single-instance helper concurrently");

            var waitingOwner = IndexerCrossProcessOperationLock.AcquireAsync(
                1_000,
                CancellationToken.None,
                lockPath);
            first.DisposeAsync().AsTask().GetAwaiter().GetResult();
            first = null;
            second = waitingOwner.GetAwaiter().GetResult();
            TestAssert.True(
                second.WasContended,
                "An owner that waited for another process must suppress a duplicate UAC prompt");
            second.DisposeAsync().AsTask().GetAwaiter().GetResult();
            second = null;

            afterRelease = IndexerCrossProcessOperationLock
                .AcquireAsync(1_000, CancellationToken.None, lockPath)
                .GetAwaiter()
                .GetResult();
            TestAssert.False(
                afterRelease.WasContended,
                "An uncontended owner should remain eligible to launch a missing helper");
        }
        finally
        {
            first?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            second?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            afterRelease?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    public static void DefaultPipeIdentitySeparatesSessionsAndDeployments()
    {
        var firstDirectory = Path.Combine(
            Path.GetTempPath(),
            "Waffle Browse Pipe Identity",
            "first");
        var secondDirectory = Path.Combine(
            Path.GetTempPath(),
            "Waffle Browse Pipe Identity",
            "second");
        var appPath = Path.Combine(firstDirectory, NamedPipeFileIndexSecurity.AppExecutableName);
        var helperPath = Path.Combine(firstDirectory, NamedPipeFileIndexSecurity.IndexerExecutableName);

        var appPipe = NamedPipeFileIndexSource.CreateDefaultPipeName(appPath, sessionId: 7);
        var helperPipe = NamedPipeFileIndexSource.CreateDefaultPipeName(helperPath, sessionId: 7);
        var otherSessionPipe = NamedPipeFileIndexSource.CreateDefaultPipeName(appPath, sessionId: 8);
        var otherDeploymentPipe = NamedPipeFileIndexSource.CreateDefaultPipeName(
            Path.Combine(secondDirectory, NamedPipeFileIndexSecurity.AppExecutableName),
            sessionId: 7);

        TestAssert.Equal(
            appPipe,
            helperPipe,
            "Sibling App and Indexer images must derive the same pipe identity");
        TestAssert.False(
            string.Equals(appPipe, otherSessionPipe, StringComparison.Ordinal),
            "Different terminal sessions must not compete for one helper pipe");
        TestAssert.False(
            string.Equals(appPipe, otherDeploymentPipe, StringComparison.Ordinal),
            "Side-by-side deployments must not compete for one helper pipe");
    }

    public static void PeerImagePolicyRequiresExactSiblingExecutable()
    {
        var deploymentDirectory = Path.Combine(
            Path.GetTempPath(),
            "Waffle Browse Security",
            "bin");
        var appImage = Path.Combine(
            deploymentDirectory,
            NamedPipeFileIndexSecurity.AppExecutableName);
        var indexerImage = Path.Combine(
            deploymentDirectory,
            NamedPipeFileIndexSecurity.IndexerExecutableName);

        TestAssert.True(
            NamedPipeFileIndexSecurity.IsExpectedPeerImagePath(
                appImage,
                indexerImage,
                NamedPipeFileIndexSecurity.AppExecutableName),
            "The helper should accept only the sibling Waffle Browse app executable");
        TestAssert.True(
            NamedPipeFileIndexSecurity.IsExpectedPeerImagePath(
                indexerImage,
                appImage,
                NamedPipeFileIndexSecurity.IndexerExecutableName),
            "The app should accept only the sibling Waffle Browse indexer executable");
        TestAssert.False(
            NamedPipeFileIndexSecurity.IsExpectedPeerImagePath(
                Path.Combine(deploymentDirectory, "Waffle.Browse.App-copy.exe"),
                indexerImage,
                NamedPipeFileIndexSecurity.AppExecutableName),
            "A renamed executable in the deployment directory must be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.IsExpectedPeerImagePath(
                Path.Combine(
                    Path.GetDirectoryName(deploymentDirectory)!,
                    NamedPipeFileIndexSecurity.AppExecutableName),
                indexerImage,
                NamedPipeFileIndexSecurity.AppExecutableName),
            "The expected filename in a sibling directory must be rejected");
    }

    public static void RootPolicyAllowsOnlyReadyFixedNtfsDriveRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        NamedPipeDriveSecurityInfo[] drives =
        [
            new(@"C:\", DriveType.Fixed, IsReady: true, "NTFS"),
            new(@"D:\", DriveType.Fixed, IsReady: true, "ReFS"),
            new(@"E:\", DriveType.Removable, IsReady: true, "NTFS"),
            new(@"F:\", DriveType.Fixed, IsReady: false, "NTFS")
        ];

        TestAssert.True(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"C:\"], drives),
            "A ready fixed local NTFS drive root should be allowed");
        TestAssert.True(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"c:\"], drives),
            "Drive-root comparison should follow Windows case-insensitive semantics");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([], drives),
            "An empty privileged indexing request should be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"C:\Users"], drives),
            "A directory below an allowed volume root should be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"C:"], drives),
            "A drive-relative path should be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"\\server\share\"], drives),
            "A network share should be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"D:\"], drives),
            "A non-NTFS fixed drive should be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"E:\"], drives),
            "A removable NTFS drive should be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"F:\"], drives),
            "A drive that is not ready should be rejected");
        TestAssert.False(
            NamedPipeFileIndexSecurity.AreRequestRootsAllowed([@"C:\", @"D:\"], drives),
            "Every root in a multi-volume request must satisfy the allowlist");
    }
}
