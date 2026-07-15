using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Waffle.Browse.Core.Search.Indexing;

public sealed class NamedPipeFileIndexHost
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly IFileIndexSnapshotSource source;
    private readonly string pipeName;
    private readonly bool enforceProductionSecurity;
    private readonly TimeSpan idleTimeout;

    public NamedPipeFileIndexHost()
        : this(
            new NtfsMftIndexSource(),
            NamedPipeFileIndexSource.DefaultPipeName,
            enforceProductionSecurity: true)
    {
    }

    internal NamedPipeFileIndexHost(
        IFileIndexSnapshotSource source,
        string pipeName,
        bool enforceProductionSecurity,
        TimeSpan? idleTimeout = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Named-pipe 이름은 비어 있을 수 없습니다.", nameof(pipeName));
        }

        var effectiveIdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (effectiveIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeout),
                "Helper 유휴 제한 시간은 0보다 커야 합니다.");
        }

        this.pipeName = pipeName;
        this.enforceProductionSecurity = enforceProductionSecurity;
        this.idleTimeout = effectiveIdleTimeout;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Waffle 인덱서 helper는 Windows에서만 실행할 수 있습니다.");
        }

        if (enforceProductionSecurity)
        {
            NamedPipeFileIndexSecurity.VerifyCurrentProcessIsElevated();
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CreateServerStream();
            try
            {
                if (!await WaitForConnectionAsync(pipe, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                if (enforceProductionSecurity)
                {
                    NamedPipeFileIndexSecurity.VerifyClient(pipe);
                }

                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A malformed request, an abandoned client, or a failed source
                // must only terminate this connection. The next client gets a
                // fresh current-user-secured server instance.
            }
        }
    }

    private async Task<bool> WaitForConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken lifetimeCancellation)
    {
        using var idleCancellation = new CancellationTokenSource(idleTimeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation,
            idleCancellation.Token);
        try
        {
            await pipe.WaitForConnectionAsync(waitCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (idleCancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateServerStream()
    {
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: NamedPipeJsonFraming.MaximumFrameBytes,
            outBufferSize: NamedPipeJsonFraming.MaximumFrameBytes,
            pipeSecurity: NamedPipeFileIndexSecurity.CreateServerSecurity(),
            inheritability: HandleInheritability.None,
            additionalAccessRights: PipeAccessRights.TakeOwnership);
        try
        {
            NamedPipeFileIndexSecurity.ApplyMediumIntegrityLabel(pipe.SafePipeHandle);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken lifetimeCancellation)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation);
        connectionCancellation.CancelAfter(ConnectionTimeout);
        Task<FileIndexBuildResult>? operation = null;
        try
        {
            var request = await NamedPipeFileIndexProtocol.ReadRequestAsync(
                pipe,
                connectionCancellation.Token).ConfigureAwait(false);
            if (enforceProductionSecurity)
            {
                NamedPipeFileIndexSecurity.VerifyRequestRoots(request.Roots);
            }

            operation = request.Baseline is null
                ? source.BuildAsync(request.Roots, connectionCancellation.Token)
                : source.RefreshAsync(request.Roots, request.Baseline, connectionCancellation.Token);
            var result = await WaitWithHeartbeatAsync(
                pipe,
                operation,
                connectionCancellation.Token).ConfigureAwait(false);
            await NamedPipeFileIndexProtocol.WriteResultAsync(
                pipe,
                result,
                connectionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            connectionCancellation.Cancel();
            ObserveFailure(operation);
            try
            {
                await NamedPipeFileIndexProtocol.WriteErrorAsync(
                    pipe,
                    ex is OperationCanceledException
                        ? new TimeoutException("Waffle 인덱서 helper 연결 제한 시간을 초과했습니다.", ex)
                        : ex,
                    lifetimeCancellation).ConfigureAwait(false);
            }
            catch (Exception writeException) when (writeException is IOException
                                                    or InvalidOperationException
                                                    or ObjectDisposedException)
            {
                // The peer can disappear while the request is being handled.
            }
        }
    }

    private static async Task<FileIndexBuildResult> WaitWithHeartbeatAsync(
        Stream pipe,
        Task<FileIndexBuildResult> operation,
        CancellationToken cancellationToken)
    {
        while (!operation.IsCompleted)
        {
            var heartbeatDelay = Task.Delay(HeartbeatInterval, cancellationToken);
            if (await Task.WhenAny(operation, heartbeatDelay).ConfigureAwait(false) == operation)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await NamedPipeFileIndexProtocol.WriteProgressAsync(
                pipe,
                cancellationToken).ConfigureAwait(false);
        }

        return await operation.ConfigureAwait(false);
    }

    private static void ObserveFailure(Task? operation)
    {
        if (operation is null)
        {
            return;
        }

        _ = operation.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
