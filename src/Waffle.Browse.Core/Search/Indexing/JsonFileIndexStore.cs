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

            if (ValidateSnapshot(snapshot) is { } validationError)
            {
                return new FileIndexLoadResult(FileIndexLoadKind.Corrupt, ErrorMessage: validationError);
            }

            return new FileIndexLoadResult(FileIndexLoadKind.Loaded, snapshot);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new FileIndexLoadResult(FileIndexLoadKind.Corrupt, ErrorMessage: ex.Message);
        }
    }

    private static string? ValidateSnapshot(FileIndexSnapshot snapshot)
    {
        if (snapshot.State is null || snapshot.Entries is null)
        {
            return "Waffle 인덱스의 필수 상태 또는 항목 컬렉션이 없습니다.";
        }

        var state = snapshot.State;
        if (state.Checkpoints is null
            || state.Generation < 0
            || state.ItemCount < 0
            || !Enum.IsDefined(state.BuildState))
        {
            return "Waffle 인덱스 상태가 올바르지 않습니다.";
        }

        if (state.ItemCount != snapshot.Entries.Count)
        {
            return "Waffle 인덱스 항목 수와 저장된 상태가 일치하지 않습니다.";
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.FullPath)
                || string.IsNullOrWhiteSpace(entry.Name)
                || entry.ParentPath is null
                || !Enum.IsDefined(entry.Kind)
                || entry.Size < 0
                || !paths.Add(entry.FullPath))
            {
                return "Waffle 인덱스에 유효하지 않거나 중복된 파일 항목이 있습니다.";
            }
        }

        foreach (var checkpoint in state.Checkpoints)
        {
            if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.RootPath))
            {
                return "Waffle 인덱스에 유효하지 않은 볼륨 체크포인트가 있습니다.";
            }
        }

        return null;
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
