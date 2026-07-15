using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.Core.Search.Indexing;

internal static class NamedPipeJsonFraming
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumFrameBytes = 64 * 1024;
    internal const int MaximumMessageBytes = 2 * 1024 * 1024;
    internal static TimeSpan PhysicalFrameIdleTimeout { get; } = TimeSpan.FromSeconds(10);

    // 48,000 bytes expands to 64,000 base64 characters, leaving room for the
    // JSON envelope while keeping every physical frame below 64 KiB.
    private const int MaximumChunkBytes = 48_000;

    internal static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static NamedPipeFileIndexJsonContext SerializerContext { get; } =
        new(SerializerOptions);

    internal static JsonTypeInfo<T> GetTypeInfo<T>() =>
        SerializerContext.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException(
            $"Named-pipe JSON type '{typeof(T).FullName}' is not registered for source generation.");

    internal static async ValueTask WriteMessageAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken = default,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveIdleTimeout = ValidateIdleTimeout(idleTimeout);

        byte[] message;
        try
        {
            message = JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<T>());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Named-pipe JSON 메시지를 직렬화할 수 없습니다.", ex);
        }

        if (message.Length == 0 || message.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                $"Named-pipe 논리 메시지는 1~{MaximumMessageBytes:N0}바이트여야 합니다.");
        }

        var sequence = 0;
        for (var offset = 0; offset < message.Length; offset += MaximumChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(MaximumChunkBytes, message.Length - offset);
            var frame = new PipeChunkFrame(
                ProtocolVersion,
                sequence,
                offset + chunkLength == message.Length,
                Convert.ToBase64String(message, offset, chunkLength));
            var frameBytes = JsonSerializer.SerializeToUtf8Bytes(
                frame,
                GetTypeInfo<PipeChunkFrame>());
            if (frameBytes.Length == 0 || frameBytes.Length > MaximumFrameBytes)
            {
                throw new InvalidDataException("Named-pipe JSON 프레임이 최대 크기를 초과했습니다.");
            }

            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, frameBytes.Length);
            await WriteWithIdleTimeoutAsync(
                stream,
                prefix,
                cancellationToken,
                effectiveIdleTimeout).ConfigureAwait(false);
            await WriteWithIdleTimeoutAsync(
                stream,
                frameBytes,
                cancellationToken,
                effectiveIdleTimeout).ConfigureAwait(false);
            sequence++;
        }
    }

    internal static async ValueTask<T> ReadMessageAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var effectiveIdleTimeout = ValidateIdleTimeout(idleTimeout);
        using var message = new MemoryStream();
        var expectedSequence = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameBytes = await ReadFrameAsync(
                stream,
                cancellationToken,
                effectiveIdleTimeout).ConfigureAwait(false);
            PipeChunkFrame frame;
            try
            {
                frame = JsonSerializer.Deserialize(
                    frameBytes,
                    GetTypeInfo<PipeChunkFrame>())
                    ?? throw new JsonException("프레임이 null입니다.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Named-pipe JSON 프레임 형식이 올바르지 않습니다.", ex);
            }

            if (frame.ProtocolVersion != ProtocolVersion)
            {
                throw new InvalidDataException(
                    $"지원하지 않는 named-pipe 프로토콜 버전입니다: {frame.ProtocolVersion}");
            }

            if (frame.Sequence != expectedSequence || frame.Payload is null)
            {
                throw new InvalidDataException("Named-pipe 프레임 청크 순서 또는 payload가 올바르지 않습니다.");
            }

            byte[] chunk;
            try
            {
                chunk = Convert.FromBase64String(frame.Payload);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("Named-pipe 프레임 payload가 유효한 base64가 아닙니다.", ex);
            }

            if (chunk.Length == 0
                || chunk.Length > MaximumChunkBytes
                || message.Length + chunk.Length > MaximumMessageBytes)
            {
                throw new InvalidDataException("Named-pipe 프레임 청크 또는 논리 메시지가 너무 큽니다.");
            }

            await message.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (frame.IsLast)
            {
                break;
            }

            expectedSequence++;
        }

        try
        {
            return JsonSerializer.Deserialize(
                    message.GetBuffer().AsSpan(0, checked((int)message.Length)),
                    GetTypeInfo<T>())
                ?? throw new JsonException("메시지가 null입니다.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Named-pipe JSON 메시지 형식이 올바르지 않습니다.", ex);
        }
    }

    private static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken,
        TimeSpan idleTimeout)
    {
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, cancellationToken, idleTimeout).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"Named-pipe JSON 프레임은 1~{MaximumFrameBytes:N0}바이트여야 합니다.");
        }

        var frame = new byte[length];
        await ReadExactlyAsync(stream, frame, cancellationToken, idleTimeout).ConfigureAwait(false);
        return frame;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken,
        TimeSpan idleTimeout)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await ReadWithIdleTimeoutAsync(
                stream,
                buffer[read..],
                cancellationToken,
                idleTimeout).ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException("Named-pipe JSON 프레임이 중간에서 끝났습니다.");
            }

            read += count;
        }
    }

    private static async ValueTask<int> ReadWithIdleTimeoutAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken,
        TimeSpan idleTimeout)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                    && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Named-pipe physical frame read was idle for {idleTimeout.TotalSeconds:N0} seconds.",
                ex);
        }
    }

    private static async ValueTask WriteWithIdleTimeoutAsync(
        Stream stream,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken,
        TimeSpan idleTimeout)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try
        {
            await stream.WriteAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                    && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Named-pipe physical frame write was idle for {idleTimeout.TotalSeconds:N0} seconds.",
                ex);
        }
    }

    private static TimeSpan ValidateIdleTimeout(TimeSpan? idleTimeout)
    {
        var value = idleTimeout ?? PhysicalFrameIdleTimeout;
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeout),
                "Named-pipe physical frame idle timeout must be positive and finite.");
        }

        return value;
    }
}

internal static class NamedPipeFileIndexProtocol
{
    internal const int MaximumRootCount = 64;
    internal const int MaximumPathLength = 32_768;

    private const int MaximumCheckpointCount = MaximumRootCount;
    private const int MaximumWarningCount = 1_024;
    private const int MaximumEntryCount = 50_000_000;
    private const int MaximumInitialCollectionCapacity = 4_096;
    private const int MaximumEntriesPerBatch = 256;

    private static readonly int EmptyEntryBatchMessageBytes = JsonSerializer.SerializeToUtf8Bytes(
        new PipeEntryBatchMessage(PipeMessageKinds.Entries, []),
        NamedPipeJsonFraming.GetTypeInfo<PipeEntryBatchMessage>()).Length;

    internal static async ValueTask WriteRequestAsync(
        Stream stream,
        IReadOnlyList<string> roots,
        FileIndexSnapshot? baseline,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(roots, baseline);

        await NamedPipeJsonFraming.WriteMessageAsync(
            stream,
            new PipeRequestHeader(
                PipeMessageKinds.Request,
                baseline is null ? PipeOperations.Build : PipeOperations.Refresh,
                roots.Count),
            cancellationToken).ConfigureAwait(false);

        foreach (var root in roots)
        {
            await NamedPipeJsonFraming.WriteMessageAsync(
                stream,
                new PipePathMessage(PipeMessageKinds.Root, root),
                cancellationToken).ConfigureAwait(false);
        }

        if (baseline is null)
        {
            return;
        }

        await NamedPipeJsonFraming.WriteMessageAsync(
            stream,
            PipeBaselineHeader.FromSnapshot(baseline),
            cancellationToken).ConfigureAwait(false);
        foreach (var checkpoint in baseline.State.Checkpoints)
        {
            await NamedPipeJsonFraming.WriteMessageAsync(
                stream,
                PipeCheckpointMessage.FromCheckpoint(checkpoint),
                cancellationToken).ConfigureAwait(false);
        }

        await WriteEntryBatchesAsync(
            stream,
            baseline.Entries,
            "baseline",
            cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateRequest(
        IReadOnlyList<string> roots,
        FileIndexSnapshot? baseline)
    {
        ValidateRoots(roots, nameof(roots));
        if (baseline is not null)
        {
            ValidateSnapshot(baseline, nameof(baseline));
        }
    }

    internal static async ValueTask<PipeFileIndexRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = await NamedPipeJsonFraming.ReadMessageAsync<PipeRequestHeader>(
            stream,
            cancellationToken).ConfigureAwait(false);
        RequireKind(header.Kind, PipeMessageKinds.Request);
        if (header.RootCount < 0 || header.RootCount > MaximumRootCount)
        {
            throw new InvalidDataException($"인덱스 루트 수는 {MaximumRootCount}개 이하여야 합니다.");
        }

        var isRefresh = header.Operation switch
        {
            PipeOperations.Build => false,
            PipeOperations.Refresh => true,
            _ => throw new InvalidDataException("알 수 없는 인덱스 요청 작업입니다.")
        };

        var roots = new List<string>(header.RootCount);
        for (var index = 0; index < header.RootCount; index++)
        {
            var root = await NamedPipeJsonFraming.ReadMessageAsync<PipePathMessage>(
                stream,
                cancellationToken).ConfigureAwait(false);
            RequireKind(root.Kind, PipeMessageKinds.Root);
            ValidatePath(root.Path, "root");
            roots.Add(root.Path);
        }

        if (!isRefresh)
        {
            return new PipeFileIndexRequest(roots, null);
        }

        var baselineHeader = await NamedPipeJsonFraming.ReadMessageAsync<PipeBaselineHeader>(
            stream,
            cancellationToken).ConfigureAwait(false);
        RequireKind(baselineHeader.Kind, PipeMessageKinds.Baseline);
        ValidateCounts(baselineHeader.CheckpointCount, baselineHeader.EntryCount, warningCount: 0);
        ValidateText(baselineHeader.ErrorMessage, "baseline error", allowNull: true);
        if (!Enum.IsDefined(baselineHeader.BuildState)
            || baselineHeader.Generation < 0
            || baselineHeader.ItemCount < 0)
        {
            throw new InvalidDataException("Baseline 상태 메타데이터가 올바르지 않습니다.");
        }

        var checkpoints = new List<FileIndexCheckpoint>(baselineHeader.CheckpointCount);
        for (var index = 0; index < baselineHeader.CheckpointCount; index++)
        {
            var checkpoint = await NamedPipeJsonFraming.ReadMessageAsync<PipeCheckpointMessage>(
                stream,
                cancellationToken).ConfigureAwait(false);
            checkpoints.Add(checkpoint.ToCheckpoint());
        }

        var entries = await ReadEntryBatchesAsync(
            stream,
            baselineHeader.EntryCount,
            cancellationToken).ConfigureAwait(false);

        var state = new FileIndexState(
            baselineHeader.BuildState,
            baselineHeader.Generation,
            baselineHeader.ItemCount,
            baselineHeader.LastCompletedAt,
            checkpoints,
            baselineHeader.ErrorMessage);
        return new PipeFileIndexRequest(
            roots,
            new FileIndexSnapshot(baselineHeader.FormatVersion, state, entries));
    }

    internal static async ValueTask WriteResultAsync(
        Stream stream,
        FileIndexBuildResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateResult(result);

        await NamedPipeJsonFraming.WriteMessageAsync(
            stream,
            new PipeResultHeader(
                PipeMessageKinds.Result,
                PipeResultStatus.Success,
                result.Checkpoints.Count,
                result.Warnings.Count,
                result.Entries.Count,
                result.SkippedPathCount,
                null,
                null),
            cancellationToken).ConfigureAwait(false);

        foreach (var checkpoint in result.Checkpoints)
        {
            await NamedPipeJsonFraming.WriteMessageAsync(
                stream,
                PipeCheckpointMessage.FromCheckpoint(checkpoint),
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var warning in result.Warnings)
        {
            await NamedPipeJsonFraming.WriteMessageAsync(
                stream,
                new PipeWarningMessage(PipeMessageKinds.Warning, warning),
                cancellationToken).ConfigureAwait(false);
        }

        await WriteEntryBatchesAsync(
            stream,
            result.Entries,
            "result",
            cancellationToken).ConfigureAwait(false);
    }

    internal static ValueTask WriteProgressAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        NamedPipeJsonFraming.WriteMessageAsync(
            stream,
            new PipeResultHeader(
                PipeMessageKinds.Result,
                PipeResultStatus.Progress,
                0,
                0,
                0,
                0,
                null,
                null),
            cancellationToken);

    internal static ValueTask WriteErrorAsync(
        Stream stream,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var (code, message) = ClassifyError(exception);
        return NamedPipeJsonFraming.WriteMessageAsync(
            stream,
            new PipeResultHeader(
                PipeMessageKinds.Result,
                PipeResultStatus.Error,
                0,
                0,
                0,
                0,
                code,
                Truncate(message, MaximumPathLength)),
            cancellationToken);
    }

    internal static async ValueTask<FileIndexBuildResult> ReadResultAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        PipeResultHeader header;
        while (true)
        {
            header = await NamedPipeJsonFraming.ReadMessageAsync<PipeResultHeader>(
                stream,
                cancellationToken).ConfigureAwait(false);
            RequireKind(header.Kind, PipeMessageKinds.Result);
            if (header.Status != PipeResultStatus.Progress)
            {
                break;
            }

            if (header.CheckpointCount != 0
                || header.WarningCount != 0
                || header.EntryCount != 0
                || header.SkippedPathCount != 0
                || header.ErrorCode is not null
                || header.ErrorMessage is not null)
            {
                throw new InvalidDataException("Named-pipe 진행 결과 헤더가 올바르지 않습니다.");
            }
        }

        if (header.Status == PipeResultStatus.Error)
        {
            if (header.CheckpointCount != 0
                || header.WarningCount != 0
                || header.EntryCount != 0
                || header.SkippedPathCount != 0
                || !PipeErrorCodes.IsDefined(header.ErrorCode))
            {
                throw new InvalidDataException("Named-pipe 오류 결과 헤더가 올바르지 않습니다.");
            }

            ValidateText(header.ErrorMessage, "remote error", allowNull: true);
            throw CreateRemoteException(header.ErrorCode, header.ErrorMessage);
        }

        if (header.Status != PipeResultStatus.Success
            || header.SkippedPathCount < 0
            || header.ErrorCode is not null
            || header.ErrorMessage is not null)
        {
            throw new InvalidDataException("Named-pipe 인덱스 결과 헤더가 올바르지 않습니다.");
        }

        ValidateCounts(header.CheckpointCount, header.EntryCount, header.WarningCount);
        var checkpoints = new List<FileIndexCheckpoint>(header.CheckpointCount);
        for (var index = 0; index < header.CheckpointCount; index++)
        {
            var checkpoint = await NamedPipeJsonFraming.ReadMessageAsync<PipeCheckpointMessage>(
                stream,
                cancellationToken).ConfigureAwait(false);
            checkpoints.Add(checkpoint.ToCheckpoint());
        }

        var warnings = new List<string>(header.WarningCount);
        for (var index = 0; index < header.WarningCount; index++)
        {
            var warning = await NamedPipeJsonFraming.ReadMessageAsync<PipeWarningMessage>(
                stream,
                cancellationToken).ConfigureAwait(false);
            RequireKind(warning.Kind, PipeMessageKinds.Warning);
            ValidateText(warning.Warning, "warning", allowNull: false);
            warnings.Add(warning.Warning);
        }

        var entries = await ReadEntryBatchesAsync(
            stream,
            header.EntryCount,
            cancellationToken).ConfigureAwait(false);

        return new FileIndexBuildResult(entries, checkpoints, warnings, header.SkippedPathCount);
    }

    private static async ValueTask WriteEntryBatchesAsync(
        Stream stream,
        IReadOnlyList<FileIndexEntry> entries,
        string validationContext,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var batch = new List<PipeEntryMessage>(
            Math.Min(entries.Count, MaximumEntriesPerBatch));
        var batchBytes = EmptyEntryBatchMessageBytes;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntry(entry, validationContext);
            var message = PipeEntryMessage.FromEntry(entry);
            // The empty envelope already accounts for `[]`. Replacing it with
            // serialized entries adds only each entry and the separating commas.
            var entryBytes = JsonSerializer.SerializeToUtf8Bytes(
                message,
                NamedPipeJsonFraming.GetTypeInfo<PipeEntryMessage>()).Length;
            var separatorBytes = batch.Count == 0 ? 0 : 1;
            if (batch.Count > 0
                && batchBytes + separatorBytes + entryBytes > NamedPipeJsonFraming.MaximumMessageBytes)
            {
                await WriteEntryBatchAsync(stream, batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
                batchBytes = EmptyEntryBatchMessageBytes;
                separatorBytes = 0;
            }

            if (batchBytes + separatorBytes + entryBytes > NamedPipeJsonFraming.MaximumMessageBytes)
            {
                throw new InvalidDataException(
                    "Named-pipe entry 하나가 논리 메시지 최대 크기를 초과했습니다.");
            }

            batch.Add(message);
            batchBytes += separatorBytes + entryBytes;
            if (batch.Count == MaximumEntriesPerBatch)
            {
                await WriteEntryBatchAsync(stream, batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
                batchBytes = EmptyEntryBatchMessageBytes;
            }
        }

        if (batch.Count > 0)
        {
            await WriteEntryBatchAsync(stream, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask WriteEntryBatchAsync(
        Stream stream,
        List<PipeEntryMessage> batch,
        CancellationToken cancellationToken) =>
        NamedPipeJsonFraming.WriteMessageAsync(
            stream,
            new PipeEntryBatchMessage(PipeMessageKinds.Entries, batch.ToArray()),
            cancellationToken);

    private static async ValueTask<List<FileIndexEntry>> ReadEntryBatchesAsync(
        Stream stream,
        int expectedEntryCount,
        CancellationToken cancellationToken)
    {
        var entries = new List<FileIndexEntry>(
            Math.Min(expectedEntryCount, MaximumInitialCollectionCapacity));
        while (entries.Count < expectedEntryCount)
        {
            var batch = await NamedPipeJsonFraming.ReadMessageAsync<PipeEntryBatchMessage>(
                stream,
                cancellationToken).ConfigureAwait(false);
            RequireKind(batch.Kind, PipeMessageKinds.Entries);
            if (batch.Entries is null
                || batch.Entries.Count == 0
                || batch.Entries.Count > MaximumEntriesPerBatch
                || batch.Entries.Count > expectedEntryCount - entries.Count)
            {
                throw new InvalidDataException(
                    "Named-pipe entry batch 수가 결과 헤더와 일치하지 않습니다.");
            }

            foreach (var entry in batch.Entries)
            {
                if (entry is null)
                {
                    throw new InvalidDataException("Named-pipe entry batch에 null entry가 있습니다.");
                }

                entries.Add(entry.ToEntry());
            }
        }

        return entries;
    }

    private static void ValidateSnapshot(FileIndexSnapshot snapshot, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(snapshot, parameterName);
        ArgumentNullException.ThrowIfNull(snapshot.State, parameterName);
        ArgumentNullException.ThrowIfNull(snapshot.State.Checkpoints, parameterName);
        ArgumentNullException.ThrowIfNull(snapshot.Entries, parameterName);
        ValidateCounts(snapshot.State.Checkpoints.Count, snapshot.Entries.Count, warningCount: 0);
        ValidateText(snapshot.State.ErrorMessage, "baseline error", allowNull: true);
        if (!Enum.IsDefined(snapshot.State.BuildState)
            || snapshot.State.Generation < 0
            || snapshot.State.ItemCount < 0)
        {
            throw new ArgumentException("Baseline 상태 메타데이터가 올바르지 않습니다.", parameterName);
        }

        foreach (var checkpoint in snapshot.State.Checkpoints)
        {
            ValidateCheckpoint(checkpoint, parameterName);
        }

    }

    private static void ValidateResult(FileIndexBuildResult result)
    {
        ArgumentNullException.ThrowIfNull(result.Entries);
        ArgumentNullException.ThrowIfNull(result.Checkpoints);
        ArgumentNullException.ThrowIfNull(result.Warnings);
        ValidateCounts(result.Checkpoints.Count, result.Entries.Count, result.Warnings.Count);
        if (result.SkippedPathCount < 0)
        {
            throw new InvalidDataException("건너뛴 경로 수는 음수일 수 없습니다.");
        }

        foreach (var checkpoint in result.Checkpoints)
        {
            ValidateCheckpoint(checkpoint, "result");
        }

        foreach (var warning in result.Warnings)
        {
            ValidateText(warning, "warning", allowNull: false);
        }

    }

    private static void ValidateRoots(IReadOnlyList<string> roots, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(roots, parameterName);
        if (roots.Count > MaximumRootCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"인덱스 루트 수는 {MaximumRootCount}개 이하여야 합니다.");
        }

        foreach (var root in roots)
        {
            try
            {
                ValidatePath(root, "root");
            }
            catch (InvalidDataException ex)
            {
                throw new ArgumentException(ex.Message, parameterName, ex);
            }
        }
    }

    private static void ValidateCounts(int checkpointCount, int entryCount, int warningCount)
    {
        if (checkpointCount < 0 || checkpointCount > MaximumCheckpointCount
            || warningCount < 0 || warningCount > MaximumWarningCount
            || entryCount < 0 || entryCount > MaximumEntryCount)
        {
            throw new InvalidDataException("Named-pipe 인덱스 컬렉션 수가 허용 범위를 벗어났습니다.");
        }
    }

    private static void ValidateCheckpoint(FileIndexCheckpoint? checkpoint, string context)
    {
        if (checkpoint is null)
        {
            throw new InvalidDataException($"{context} checkpoint가 null입니다.");
        }

        ValidatePath(checkpoint.RootPath, "checkpoint root");
        ValidateText(checkpoint.VolumeId, "volume ID", allowNull: true);
        ValidateText(checkpoint.FileSystem, "file system", allowNull: true);
        if (checkpoint.JournalId.HasValue != checkpoint.NextUsn.HasValue)
        {
            throw new InvalidDataException("Checkpoint journal ID와 next USN은 함께 있어야 합니다.");
        }
    }

    private static void ValidateEntry(FileIndexEntry? entry, string context)
    {
        if (entry is null)
        {
            throw new InvalidDataException($"{context} entry가 null입니다.");
        }

        ValidatePath(entry.FullPath, "entry full path");
        ValidateText(entry.Name, "entry name", allowNull: false);
        ValidatePath(entry.ParentPath, "entry parent path", allowEmpty: true);
        ValidateText(entry.VolumeId, "volume ID", allowNull: true);
        if (!Enum.IsDefined(entry.Kind) || entry.Size is < 0)
        {
            throw new InvalidDataException("인덱스 entry 종류 또는 크기가 올바르지 않습니다.");
        }
    }

    private static void ValidatePath(string? path, string field, bool allowEmpty = false)
    {
        if (path is null
            || (!allowEmpty && string.IsNullOrWhiteSpace(path))
            || path.Length > MaximumPathLength)
        {
            throw new InvalidDataException(
                $"{field} 길이는 {(allowEmpty ? 0 : 1)}~{MaximumPathLength:N0}자여야 합니다.");
        }
    }

    private static void ValidateText(string? value, string field, bool allowNull)
    {
        if ((!allowNull && value is null) || value?.Length > MaximumPathLength)
        {
            throw new InvalidDataException($"{field} 길이는 {MaximumPathLength:N0}자 이하여야 합니다.");
        }
    }

    private static void RequireKind(string? actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"예상한 named-pipe 메시지 종류는 '{expected}'입니다.");
        }
    }

    private static (string Code, string Message) ClassifyError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => (PipeErrorCodes.Unauthorized, exception.Message),
        PlatformNotSupportedException or NotSupportedException => (PipeErrorCodes.Unsupported, exception.Message),
        InvalidDataException or JsonException or ArgumentException => (PipeErrorCodes.Protocol, exception.Message),
        IOException => (PipeErrorCodes.Io, exception.Message),
        _ => (PipeErrorCodes.Failed, "인덱서 작업을 완료하지 못했습니다.")
    };

    private static Exception CreateRemoteException(string? code, string? message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "원격 인덱서 작업이 실패했습니다."
            : Truncate(message, MaximumPathLength);
        return code switch
        {
            PipeErrorCodes.Unauthorized => new UnauthorizedAccessException(safeMessage),
            PipeErrorCodes.Unsupported => new NotSupportedException(safeMessage),
            PipeErrorCodes.Protocol => new InvalidDataException(safeMessage),
            PipeErrorCodes.Io => new IOException(safeMessage),
            _ => new IOException(safeMessage)
        };
    }

    private static string Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "인덱서 작업이 실패했습니다.";
        }

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static class PipeMessageKinds
    {
        internal const string Request = "request";
        internal const string Root = "root";
        internal const string Baseline = "baseline";
        internal const string Checkpoint = "checkpoint";
        internal const string Entry = "entry";
        internal const string Entries = "entries";
        internal const string Warning = "warning";
        internal const string Result = "result";
    }

    private static class PipeOperations
    {
        internal const string Build = "build";
        internal const string Refresh = "refresh";
    }

    private static class PipeResultStatus
    {
        internal const string Progress = "progress";
        internal const string Success = "success";
        internal const string Error = "error";
    }

    private static class PipeErrorCodes
    {
        internal const string Unauthorized = "unauthorized";
        internal const string Unsupported = "unsupported";
        internal const string Protocol = "protocol";
        internal const string Io = "io";
        internal const string Failed = "failed";

        internal static bool IsDefined(string? value) => value is
            Unauthorized or Unsupported or Protocol or Io or Failed;
    }

    internal sealed record PipeFileIndexRequest(
        IReadOnlyList<string> Roots,
        FileIndexSnapshot? Baseline);

    internal sealed record PipeRequestHeader(string Kind, string Operation, int RootCount);

    internal sealed record PipePathMessage(string Kind, string Path);

    internal sealed record PipeBaselineHeader(
        string Kind,
        int FormatVersion,
        FileIndexBuildState BuildState,
        long Generation,
        long ItemCount,
        DateTimeOffset? LastCompletedAt,
        int CheckpointCount,
        int EntryCount,
        string? ErrorMessage)
    {
        internal static PipeBaselineHeader FromSnapshot(FileIndexSnapshot snapshot) => new(
            PipeMessageKinds.Baseline,
            snapshot.FormatVersion,
            snapshot.State.BuildState,
            snapshot.State.Generation,
            snapshot.State.ItemCount,
            snapshot.State.LastCompletedAt,
            snapshot.State.Checkpoints.Count,
            snapshot.Entries.Count,
            snapshot.State.ErrorMessage);
    }

    internal sealed record PipeCheckpointMessage(
        string Kind,
        string RootPath,
        string? VolumeId,
        string? FileSystem,
        ulong? JournalId,
        long? NextUsn,
        DateTimeOffset CapturedAt,
        uint? VolumeSerialNumber)
    {
        internal static PipeCheckpointMessage FromCheckpoint(FileIndexCheckpoint checkpoint) => new(
            PipeMessageKinds.Checkpoint,
            checkpoint.RootPath,
            checkpoint.VolumeId,
            checkpoint.FileSystem,
            checkpoint.JournalId,
            checkpoint.NextUsn,
            checkpoint.CapturedAt,
            checkpoint.VolumeSerialNumber);

        internal FileIndexCheckpoint ToCheckpoint()
        {
            RequireKind(Kind, PipeMessageKinds.Checkpoint);
            var checkpoint = new FileIndexCheckpoint(
                RootPath,
                VolumeId,
                FileSystem,
                JournalId,
                NextUsn,
                CapturedAt,
                VolumeSerialNumber);
            ValidateCheckpoint(checkpoint, "remote");
            return checkpoint;
        }
    }

    internal sealed record PipeEntryMessage(
        string Kind,
        string FullPath,
        string Name,
        string ParentPath,
        SearchItemKind ItemKind,
        long? Size,
        DateTimeOffset? ModifiedAt,
        string? VolumeId,
        ulong? ReferenceLow,
        ulong? ReferenceHigh,
        FileReferenceIdWidth? ReferenceWidth)
    {
        internal static PipeEntryMessage FromEntry(FileIndexEntry entry) => new(
            PipeMessageKinds.Entry,
            entry.FullPath,
            entry.Name,
            entry.ParentPath,
            entry.Kind,
            entry.Size,
            entry.ModifiedAt,
            entry.VolumeId,
            entry.FileReferenceNumber?.Low,
            entry.FileReferenceNumber?.High,
            entry.FileReferenceNumber?.Width);

        internal FileIndexEntry ToEntry()
        {
            RequireKind(Kind, PipeMessageKinds.Entry);
            if (ReferenceLow.HasValue != ReferenceWidth.HasValue
                || ReferenceHigh.HasValue != ReferenceLow.HasValue
                || ReferenceWidth is { } width && !Enum.IsDefined(width)
                || ReferenceHigh is not null and not 0
                    && ReferenceWidth == FileReferenceIdWidth.Bits64)
            {
                throw new InvalidDataException("인덱스 entry 파일 참조 번호가 올바르지 않습니다.");
            }

            FileReferenceId? reference = ReferenceLow is { } low
                ? new FileReferenceId(low, ReferenceHigh!.Value, ReferenceWidth!.Value)
                : null;
            var entry = new FileIndexEntry(
                FullPath,
                Name,
                ParentPath,
                ItemKind,
                Size,
                ModifiedAt,
                VolumeId,
                reference);
            ValidateEntry(entry, "remote");
            return entry;
        }
    }

    internal sealed record PipeEntryBatchMessage(
        string Kind,
        IReadOnlyList<PipeEntryMessage> Entries);

    internal sealed record PipeWarningMessage(string Kind, string Warning);

    internal sealed record PipeResultHeader(
        string Kind,
        string Status,
        int CheckpointCount,
        int WarningCount,
        int EntryCount,
        long SkippedPathCount,
        string? ErrorCode,
        string? ErrorMessage);
}

internal sealed record PipeChunkFrame(
    int ProtocolVersion,
    int Sequence,
    bool IsLast,
    string Payload);
