namespace Waffle.Browse.Core.Search.Indexing;

public sealed class WaffleFileSearchProvider : ISearchProvider, IDisposable
{
    public const string ProviderId = "waffle-index";
    private const int MaxWarningSummaryCount = 3;
    private const int MaxWarningSummaryLength = 160;

    private readonly FileSearchIndex index = new();
    private readonly IFileIndexSource source;
    private readonly IFileIndexStore store;
    private readonly IReadOnlyList<string> roots;
    private readonly bool watchChanges;
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private readonly SemaphoreSlim rebuildGate = new(1, 1);
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object stateGate = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly List<FileIndexChange> pendingChanges = [];
    private CancellationTokenSource? persistenceDelay;
    private FileIndexState state = FileIndexState.Empty;
    private bool initialized;
    private bool rebuildInProgress;
    private bool disposed;

    public WaffleFileSearchProvider(
        IFileIndexSource source,
        IFileIndexStore store,
        IReadOnlyList<string> roots,
        bool watchChanges = true)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(roots);
        this.roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        this.watchChanges = watchChanges;
    }

    public string Id => ProviderId;

    public string DisplayName => "Waffle 자체 인덱스";

    public FileIndexState State
    {
        get
        {
            lock (stateGate)
            {
                return state;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
            {
                return;
            }

            SetState(State with { BuildState = FileIndexBuildState.Loading, ErrorMessage = null });
            var load = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (load is { Kind: FileIndexLoadKind.Loaded, Snapshot: { } snapshot })
            {
                index.Replace(snapshot.Entries);
                SetState(snapshot.State with
                {
                    BuildState = FileIndexBuildState.Ready,
                    ItemCount = index.Count,
                    ErrorMessage = null
                });
            }
            else if (load.Kind == FileIndexLoadKind.Corrupt)
            {
                SetState(State with
                {
                    BuildState = FileIndexBuildState.NeedsRebuild,
                    ErrorMessage = load.ErrorMessage
                });
            }

            initialized = true;
            StartWatchers();
        }
        finally
        {
            initializeGate.Release();
        }

        await RebuildAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        await rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            FileIndexState stateBeforeRebuild;
            lock (stateGate)
            {
                stateBeforeRebuild = state;
                rebuildInProgress = true;
                pendingChanges.Clear();
                state = state with
                {
                    BuildState = FileIndexBuildState.Rebuilding,
                    ItemCount = index.Count,
                    ErrorMessage = null
                };
            }

            var completedGenerationPublished = false;
            try
            {
                var result = await source.BuildAsync(roots, cancellationToken).ConfigureAwait(false);
                var replacement = index.PrepareReplacement(result.Entries, cancellationToken);
                var warning = SummarizeBuildWarnings(result);

                lock (stateGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index.ReplaceAndApply(replacement, pendingChanges, cancellationToken);
                    pendingChanges.Clear();
                    rebuildInProgress = false;
                    state = new FileIndexState(
                        FileIndexBuildState.Ready,
                        stateBeforeRebuild.Generation + 1,
                        index.Count,
                        DateTimeOffset.UtcNow,
                        result.Checkpoints,
                        warning);
                }

                completedGenerationPublished = true;
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (stateGate)
                {
                    rebuildInProgress = false;
                    if (!completedGenerationPublished)
                    {
                        if (HasCompletedGeneration(stateBeforeRebuild))
                        {
                            index.Apply(pendingChanges);
                            state = stateBeforeRebuild with
                            {
                                BuildState = FileIndexBuildState.Ready,
                                ItemCount = index.Count
                            };
                        }
                        else
                        {
                            state = FileIndexState.Empty;
                        }
                    }

                    pendingChanges.Clear();
                }

                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                lock (stateGate)
                {
                    var hasCompletedGeneration = HasCompletedGeneration(stateBeforeRebuild);
                    if (hasCompletedGeneration)
                    {
                        index.Apply(pendingChanges);
                    }

                    rebuildInProgress = false;
                    pendingChanges.Clear();
                    state = state with
                    {
                        BuildState = hasCompletedGeneration ? FileIndexBuildState.Ready : FileIndexBuildState.Failed,
                        ItemCount = index.Count,
                        ErrorMessage = ex.Message
                    };
                }
            }
        }
        finally
        {
            rebuildGate.Release();
        }
    }

    private static string? SummarizeBuildWarnings(FileIndexBuildResult result)
    {
        var summaries = new List<string>();
        if (result.SkippedPathCount > 0)
        {
            summaries.Add($"일부 경로를 건너뜀: {result.SkippedPathCount:N0}개");
        }

        var warnings = result.Warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (warnings.Count > 0)
        {
            var displayed = warnings
                .Take(MaxWarningSummaryCount)
                .Select(TruncateWarning);
            var omittedCount = warnings.Count - MaxWarningSummaryCount;
            var omitted = omittedCount > 0 ? $" (외 {omittedCount:N0}개)" : string.Empty;
            summaries.Add($"경고: {string.Join(" | ", displayed)}{omitted}");
        }

        return summaries.Count == 0 ? null : string.Join("; ", summaries);
    }

    private static string TruncateWarning(string warning) =>
        warning.Length <= MaxWarningSummaryLength
            ? warning
            : warning[..(MaxWarningSummaryLength - 3)] + "...";

    private static bool HasCompletedGeneration(FileIndexState candidate) =>
        candidate.BuildState == FileIndexBuildState.Ready
        || candidate.Generation > 0
        || candidate.LastCompletedAt is not null;

    public Task<SearchProviderStatus> CheckStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToProviderStatus(State));
    }

    public Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = ToProviderStatus(State);
        return Task.FromResult(index.Search(query, status, Id));
    }

    private void StartWatchers()
    {
        if (!watchChanges || watchers.Count > 0)
        {
            return;
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.Size
                        | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = false
                };
                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                SetState(State with { ErrorMessage = $"변경 감시를 시작하지 못했습니다: {ex.Message}" });
            }
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (RecursiveFileIndexSource.TryReadEntry(e.FullPath) is { } entry)
        {
            ApplyChange(FileIndexChange.Upsert(entry));
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (RecursiveFileIndexSource.TryReadEntry(e.FullPath) is { } entry)
        {
            ApplyChange(FileIndexChange.UpdateMetadata(entry));
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        ApplyChange(FileIndexChange.Delete(e.FullPath));

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        ApplyChange(FileIndexChange.Rename(e.OldFullPath, e.FullPath));

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }
        }

        SetState(State with
        {
            BuildState = FileIndexBuildState.NeedsRebuild,
            ErrorMessage = $"파일 변경 이벤트가 손실되었습니다: {e.GetException().Message}"
        });
        _ = RebuildAfterWatcherErrorAsync();
    }

    private async Task RebuildAfterWatcherErrorAsync()
    {
        try
        {
            await RebuildAsync(lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ApplyChange(FileIndexChange change)
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            if (rebuildInProgress)
            {
                pendingChanges.Add(change);
                return;
            }

            index.Apply([change]);
            state = state with { ItemCount = index.Count };
        }

        SchedulePersistence();
    }

    private void SchedulePersistence()
    {
        CancellationToken token;
        lock (stateGate)
        {
            persistenceDelay?.Cancel();
            persistenceDelay?.Dispose();
            persistenceDelay = new CancellationTokenSource();
            token = persistenceDelay.Token;
        }

        _ = PersistAfterDelayAsync(token);
    }

    private async Task PersistAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetState(State with { ErrorMessage = $"인덱스를 저장하지 못했습니다: {ex.Message}" });
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileIndexSnapshot snapshot;
            lock (stateGate)
            {
                var entries = index.Snapshot();
                snapshot = new FileIndexSnapshot(
                    FileIndexSnapshot.CurrentFormatVersion,
                    state with { ItemCount = entries.Count },
                    entries);
            }

            await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            persistenceGate.Release();
        }
    }

    private void SetState(FileIndexState next)
    {
        lock (stateGate)
        {
            state = next;
        }
    }

    private static SearchProviderStatus ToProviderStatus(FileIndexState current)
    {
        var detail = current.ErrorMessage is null ? string.Empty : $" ({current.ErrorMessage})";
        return current.BuildState switch
        {
            FileIndexBuildState.Ready => new SearchProviderStatus(
                SearchProviderStatusKind.Ready,
                $"Waffle 인덱스 {current.ItemCount:N0}개 항목을 사용할 수 있습니다.{detail}",
                true),
            FileIndexBuildState.Rebuilding => new SearchProviderStatus(
                SearchProviderStatusKind.Rebuilding,
                $"Waffle 인덱스를 다시 만드는 중입니다.{detail}",
                current.ItemCount > 0),
            FileIndexBuildState.NeedsRebuild => new SearchProviderStatus(
                SearchProviderStatusKind.CorruptIndex,
                $"Waffle 인덱스를 다시 만들어야 합니다.{detail}",
                current.ItemCount > 0),
            FileIndexBuildState.Failed => new SearchProviderStatus(
                SearchProviderStatusKind.Error,
                $"Waffle 인덱스를 만들지 못했습니다.{detail}",
                false),
            _ => new SearchProviderStatus(
                SearchProviderStatusKind.Initializing,
                "Waffle 인덱스를 준비하는 중입니다.",
                false)
        };
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifetimeCancellation.Cancel();
            persistenceDelay?.Cancel();
            persistenceDelay?.Dispose();
            persistenceDelay = null;
        }

        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        watchers.Clear();
    }
}
