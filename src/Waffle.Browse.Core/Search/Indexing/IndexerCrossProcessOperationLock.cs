using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Waffle.Browse.Core.Search.Indexing;

internal sealed class IndexerCrossProcessOperationLock : IAsyncDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    private readonly FileStream stream;
    private bool disposed;

    private IndexerCrossProcessOperationLock(FileStream stream, bool wasContended)
    {
        this.stream = stream;
        WasContended = wasContended;
    }

    internal bool WasContended { get; }

    [SupportedOSPlatform("windows")]
    internal static async Task<IndexerCrossProcessOperationLock> AcquireAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        string? lockPath = null,
        string? lockIdentity = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Waffle 인덱서 cross-process lock은 Windows에서만 사용할 수 있습니다.");
        }

        if (timeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        FileStream? stream = null;
        var wasContended = false;
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeoutMilliseconds);
        try
        {
            var localApplicationData = lockPath is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : null;
            if (lockPath is null
                && (string.IsNullOrWhiteSpace(localApplicationData)
                    || !Path.IsPathFullyQualified(localApplicationData)))
            {
                throw new NotSupportedException(
                    "현재 사용자의 LocalAppData 경로를 확인하지 못했습니다.");
            }

            var directory = lockPath is null
                ? Path.Combine(localApplicationData!, "Waffle Browse")
                : Path.GetDirectoryName(Path.GetFullPath(lockPath))
                    ?? throw new ArgumentException(
                        "Cross-process lock 경로에 디렉터리가 없습니다.",
                        nameof(lockPath));
            Directory.CreateDirectory(directory);
            var resolvedLockPath = lockPath is null
                ? Path.Combine(directory, CreateDefaultLockFileName(lockIdentity))
                : Path.GetFullPath(lockPath);
            stream = new FileStream(
                resolvedLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.Asynchronous);

            while (true)
            {
                timeoutCancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    stream.Lock(0, 1);
                    timeoutCancellation.Token.ThrowIfCancellationRequested();
                    return new IndexerCrossProcessOperationLock(stream, wasContended);
                }
                catch (IOException)
                {
                    wasContended = true;
                    await Task.Delay(
                        RetryInterval,
                        timeoutCancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
            throw;
        }
        catch (OperationCanceledException ex)
        {
            stream?.Dispose();
            throw new NotSupportedException(
                "다른 Waffle Browse 프로세스가 인덱서 helper를 사용 중입니다.",
                ex);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            stream?.Dispose();
            throw new NotSupportedException(
                "Waffle 인덱서 cross-process lock을 확보하지 못했습니다.",
                ex);
        }
    }

    private static string CreateDefaultLockFileName(string? lockIdentity)
    {
        if (string.IsNullOrWhiteSpace(lockIdentity))
        {
            return "indexer-operation.lock";
        }

        var identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(lockIdentity));
        return $"indexer-operation-{Convert.ToHexString(identityHash.AsSpan(0, 8))}.lock";
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                stream.Unlock(0, 1);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closing the handle also releases any surviving byte-range lock.
        }

        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
