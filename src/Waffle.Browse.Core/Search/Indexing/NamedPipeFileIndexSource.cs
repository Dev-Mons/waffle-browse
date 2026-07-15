using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace Waffle.Browse.Core.Search.Indexing;

public sealed class NamedPipeFileIndexSource : IFileIndexSnapshotSource
{
    private const string DefaultPipeNamePrefix = "Waffle.Browse.Indexer.v1";

    private const int DefaultConnectTimeoutMilliseconds = 750;
    private const int DefaultLaunchConnectTimeoutMilliseconds = 15_000;
    private const int DefaultOperationTimeoutMilliseconds = 30 * 60 * 1000;

    public static string DefaultPipeName { get; } = CreateDefaultPipeName(
        Environment.ProcessPath ?? AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? Process.GetCurrentProcess().SessionId : 0);

    private static readonly IndexerLaunchCoordinator DefaultLaunchCoordinator = new();

    private readonly string pipeName;
    private readonly int connectTimeoutMilliseconds;
    private readonly int operationTimeoutMilliseconds;
    private readonly bool enforceProductionSecurity;
    private readonly IIndexerProcessLauncher? processLauncher;
    private readonly int launchConnectTimeoutMilliseconds;
    private readonly IndexerLaunchCoordinator launchCoordinator;
    private readonly bool useCrossProcessOperationLock;

    public NamedPipeFileIndexSource()
        : this(
            DefaultPipeName,
            DefaultConnectTimeoutMilliseconds,
            DefaultOperationTimeoutMilliseconds,
            enforceProductionSecurity: true,
            processLauncher: null,
            DefaultLaunchConnectTimeoutMilliseconds,
            DefaultLaunchCoordinator)
    {
    }

    internal NamedPipeFileIndexSource(IIndexerProcessLauncher processLauncher)
        : this(
            DefaultPipeName,
            DefaultConnectTimeoutMilliseconds,
            DefaultOperationTimeoutMilliseconds,
            enforceProductionSecurity: true,
            processLauncher,
            DefaultLaunchConnectTimeoutMilliseconds,
            DefaultLaunchCoordinator)
    {
        ArgumentNullException.ThrowIfNull(processLauncher);
    }

    internal NamedPipeFileIndexSource(
        string pipeName,
        int connectTimeoutMilliseconds,
        bool enforceProductionSecurity)
        : this(
            pipeName,
            connectTimeoutMilliseconds,
            DefaultOperationTimeoutMilliseconds,
            enforceProductionSecurity,
            processLauncher: null,
            DefaultLaunchConnectTimeoutMilliseconds)
    {
    }

    internal NamedPipeFileIndexSource(
        string pipeName,
        int connectTimeoutMilliseconds,
        int operationTimeoutMilliseconds,
        bool enforceProductionSecurity)
        : this(
            pipeName,
            connectTimeoutMilliseconds,
            operationTimeoutMilliseconds,
            enforceProductionSecurity,
            processLauncher: null,
            DefaultLaunchConnectTimeoutMilliseconds)
    {
    }

    internal NamedPipeFileIndexSource(
        string pipeName,
        int connectTimeoutMilliseconds,
        int operationTimeoutMilliseconds,
        bool enforceProductionSecurity,
        IIndexerProcessLauncher? processLauncher,
        int launchConnectTimeoutMilliseconds,
        IndexerLaunchCoordinator? launchCoordinator = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Named-pipe 이름은 비어 있을 수 없습니다.", nameof(pipeName));
        }

        if (connectTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeoutMilliseconds),
                "Named-pipe 연결 제한 시간은 0보다 커야 합니다.");
        }

        if (operationTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeoutMilliseconds),
                "Named-pipe 작업 제한 시간은 0보다 커야 합니다.");
        }

        if (launchConnectTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(launchConnectTimeoutMilliseconds),
                "승격 helper 시작 후 연결 제한 시간은 0보다 커야 합니다.");
        }

        this.pipeName = pipeName;
        this.connectTimeoutMilliseconds = connectTimeoutMilliseconds;
        this.operationTimeoutMilliseconds = operationTimeoutMilliseconds;
        this.enforceProductionSecurity = enforceProductionSecurity;
        this.processLauncher = processLauncher;
        this.launchConnectTimeoutMilliseconds = launchConnectTimeoutMilliseconds;
        this.launchCoordinator = launchCoordinator ?? new IndexerLaunchCoordinator();
        useCrossProcessOperationLock = enforceProductionSecurity
            && string.Equals(pipeName, DefaultPipeName, StringComparison.Ordinal);
    }

    public Task<FileIndexBuildResult> BuildAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return RunAsync(roots, baseline: null, cancellationToken);
    }

    public Task<FileIndexBuildResult> RefreshAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(baseline);
        return RunAsync(roots, baseline, cancellationToken);
    }

    private async Task<FileIndexBuildResult> RunAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot? baseline,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Waffle 인덱서 helper는 Windows에서만 사용할 수 있습니다.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        NamedPipeFileIndexProtocol.ValidateRequest(roots, baseline);

        await launchCoordinator.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IndexerCrossProcessOperationLock? crossProcessLock = null;
        try
        {
            if (useCrossProcessOperationLock)
            {
                crossProcessLock = await IndexerCrossProcessOperationLock.AcquireAsync(
                    launchConnectTimeoutMilliseconds,
                    cancellationToken,
                    lockIdentity: pipeName).ConfigureAwait(false);
                if (crossProcessLock.WasContended)
                {
                    // Another app process may still be finishing a successful
                    // helper startup. A short connection miss must not open a
                    // second UAC prompt in this process.
                    launchCoordinator.SuppressLaunchPrompt = true;
                }
            }

            return await RunExclusiveAsync(roots, baseline, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (crossProcessLock is not null)
                {
                    await crossProcessLock.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                launchCoordinator.OperationGate.Release();
            }
        }
    }

    internal static string CreateDefaultPipeName(string processPath, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new ArgumentException("프로세스 경로는 비어 있을 수 없습니다.", nameof(processPath));
        }

        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        var normalizedPath = Path.GetFullPath(processPath);
        var deploymentDirectory = Directory.Exists(normalizedPath)
            ? normalizedPath
            : Path.GetDirectoryName(normalizedPath)
                ?? throw new ArgumentException(
                    "프로세스 경로에서 배포 디렉터리를 확인하지 못했습니다.",
                    nameof(processPath));
        var identityBytes = Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(deploymentDirectory).ToUpperInvariant());
        var identityHash = SHA256.HashData(identityBytes);
        return $"{DefaultPipeNamePrefix}.s{sessionId}.{Convert.ToHexString(identityHash.AsSpan(0, 8))}";
    }

    [SupportedOSPlatform("windows")]
    private async Task<FileIndexBuildResult> RunExclusiveAsync(
        IReadOnlyList<string> roots,
        FileIndexSnapshot? baseline,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe;
        try
        {
            pipe = await ConnectOrLaunchAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (enforceProductionSecurity)
                {
                    NamedPipeFileIndexSecurity.VerifyServer(pipe);
                }

                // A launch-in-progress marker is cleared only after the peer
                // has passed the production identity boundary.
                launchCoordinator.SuppressLaunchPrompt = false;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or OperationCanceledException)
        {
            throw new NotSupportedException(
                "Waffle 인덱서 helper에 연결할 수 없습니다.",
                ex);
        }

        await using var connectedPipe = pipe;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(operationTimeoutMilliseconds);
        try
        {
            await NamedPipeFileIndexProtocol.WriteRequestAsync(
                connectedPipe,
                roots,
                baseline,
                operationCancellation.Token).ConfigureAwait(false);
            return await NamedPipeFileIndexProtocol.ReadResultAsync(
                connectedPipe,
                operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                   or TimeoutException
                                   or IOException)
        {
            throw new NotSupportedException(
                "Waffle 인덱서 helper 통신을 완료하지 못했습니다.",
                ex);
        }
    }

    private async Task<NamedPipeClientStream> ConnectOrLaunchAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectAsync(
                connectTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRetryableConnectionFailure(ex))
        {
            return await LaunchAndReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<NamedPipeClientStream> LaunchAndReconnectAsync(
        Exception initialFailure,
        CancellationToken cancellationToken)
    {
        if (processLauncher is null)
        {
            throw new NotSupportedException(
                "Waffle 인덱서 helper가 실행 중이지 않습니다.",
                initialFailure);
        }

        if (launchCoordinator.SuppressLaunchPrompt)
        {
            throw new NotSupportedException(
                "이번 앱 실행에서는 Waffle 인덱서 helper 승격을 다시 요청하지 않습니다.",
                initialFailure);
        }

        // An externally started helper can become ready between the first
        // connection failure and the elevation decision. Retry with a fresh pipe.
        try
        {
            var existing = await ConnectAsync(
                connectTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            return existing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRetryableConnectionFailure(ex))
        {
            initialFailure = ex;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IndexerLaunchResult launch;
        try
        {
            launch = processLauncher.Launch();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            launchCoordinator.SuppressLaunchPrompt = true;
            throw new NotSupportedException(
                "Waffle 인덱서 helper 승격 실행을 시작하지 못했습니다.",
                ex);
        }

        if (!launch.Started)
        {
            launchCoordinator.SuppressLaunchPrompt = true;
            throw new NotSupportedException(launch.Message, launch.Exception ?? initialFailure);
        }

        // Record the started generation before observing caller cancellation.
        // A later request can connect to this process, but it must not create a
        // second UAC prompt while startup or peer verification is unresolved.
        launchCoordinator.SuppressLaunchPrompt = true;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var launched = await ConnectAsync(
                launchConnectTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            return launched;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRetryableConnectionFailure(ex))
        {
            launchCoordinator.SuppressLaunchPrompt = true;
            throw new NotSupportedException(
                "승격된 Waffle 인덱서 helper가 제한 시간 안에 연결되지 않았습니다.",
                ex);
        }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsRetryableConnectionFailure(Exception exception) =>
        exception is TimeoutException
            or IOException
            or OperationCanceledException;
}
