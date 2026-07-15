using System.Text.Json;

namespace Waffle.Browse.Core.Search.Indexing;

public sealed class JsonFileIndexStore : IFileIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string filePath;

    public JsonFileIndexStore(string filePath)
    {
        this.filePath = Path.GetFullPath(filePath);
    }

    public async Task<FileIndexLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new FileIndexLoadResult(FileIndexLoadKind.Missing);
        }

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = await JsonSerializer.DeserializeAsync<FileIndexSnapshot>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null || snapshot.FormatVersion != FileIndexSnapshot.CurrentFormatVersion)
            {
                return new FileIndexLoadResult(FileIndexLoadKind.Corrupt, ErrorMessage: "지원하지 않는 Waffle 인덱스 형식입니다.");
            }

            return new FileIndexLoadResult(FileIndexLoadKind.Loaded, snapshot);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new FileIndexLoadResult(FileIndexLoadKind.Corrupt, ErrorMessage: ex.Message);
        }
    }

    public async Task SaveAsync(FileIndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
