namespace Waffle.Browse.Core.Search.Indexing;

public sealed class WaffleFileSearchProvider : ISearchProvider, IDisposable
{
    public const string ProviderId = "waffle-index";

    private readonly FileSearchIndex index = new();
    private readonly IFileIndexSource source;
    private readonly IFileIndexStore store;
    private IReadOnlyList<string> roots;
    private readonly bool watchChanges;
    private readonly bool buildOnInitialize;
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private readonly SemaphoreSlim rebuildGate = new(1, 1);
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object stateGate = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly HashSet<string> watchedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pendingRefreshRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileIndexChange> pendingChanges = [];
    private readonly Dictionary<string, List<string>> warningsByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IFileIndexProgressSource? progressSource;
    private readonly List<string> unscopedWarnings = [];
    private CancellationTokenSource? persistenceDelay;
    private Timer? networkRevalidationTimer;
    private FileIndexState state = FileIndexState.Empty;
    private FileIndexProgressEventArgs progress = FileIndexProgressEventArgs.Initial;
    private bool initialized;
    private bool rebuildInProgress;
    private bool refreshWorkerRunning;
    private bool disposed;

    public WaffleFileSearchProvider(
        IFileIndexSource source,
        IFileIndexStore store,
        IReadOnlyList<string> roots,
        bool watchChanges = true,
        bool buildOnInitialize = true)
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
        this.buildOnInitialize = buildOnInitialize;
        progressSource = source as IFileIndexProgressSource;
        if (progressSource is not null)
        {
            progressSource.ProgressChanged += OnSourceProgressChanged;
        }
    }

    public string Id => ProviderId;

    public string DisplayName => "Waffle 자체 인덱스";

    public event EventHandler? IndexStatusChanged;

    public IReadOnlyList<string> IndexRoots
    {
        get
        {
            lock (stateGate)
            {
                return [.. roots];
            }
        }
    }

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

    public FileIndexProgressEventArgs Progress
    {
        get
        {
            lock (stateGate)
            {
                return progress;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        FileIndexSnapshot? baseline = null;
        await initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
            {
                return;
            }

            if (!buildOnInitialize)
            {
                initialized = true;
                return;
            }

            SetState(State with { BuildState = FileIndexBuildState.Loading, ErrorMessage = null });
            var load = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (load is { Kind: FileIndexLoadKind.Loaded, Snapshot: { } snapshot })
            {
                baseline = snapshot;
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
            StartNetworkRevalidation();
        }
        finally
        {
            initializeGate.Release();
        }

        await RebuildAsync(baseline, cancellationToken).ConfigureAwait(false);
    }

    public async Task IndexFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var root = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"색인할 폴더를 찾을 수 없습니다: {root}");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await RebuildAsync(
            baseline: null,
            cancellationToken,
            replacementRoots: [root]).ConfigureAwait(false);
    }

    public Task RebuildAsync(CancellationToken cancellationToken = default) =>
        RebuildAsync(baseline: null, cancellationToken);

    private Task RefreshRootsAsync(
        IReadOnlyList<string> refreshRoots,
        CancellationToken cancellationToken) =>
        RebuildAsync(baseline: null, cancellationToken, refreshRoots);

    private async Task RebuildAsync(
        FileIndexSnapshot? baseline,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? refreshRoots = null,
        IReadOnlyList<string>? replacementRoots = null)
    {
        if (refreshRoots is { Count: 0 })
        {
            return;
        }

        await rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (replacementRoots is not null)
            {
                StopWatchers();
                lock (stateGate)
                {
                    roots = replacementRoots
                        .Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    pendingRefreshRoots.Clear();
                    warningsByRoot.Clear();
                    unscopedWarnings.Clear();
                }
            }

            IReadOnlyList<string> buildRoots = roots;
            FileIndexSnapshot? mergeBaseline = null;
            lock (stateGate)
            {
                if (refreshRoots is not null)
                {
                    buildRoots = refreshRoots
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    mergeBaseline = new FileIndexSnapshot(
                        FileIndexSnapshot.CurrentFormatVersion,
                        state with { ItemCount = index.Count },
                        index.Snapshot());
                    baseline = mergeBaseline;
                }

                rebuildInProgress = true;
                pendingChanges.Clear();
                state = state with
                {
                    BuildState = FileIndexBuildState.Rebuilding,
                    ItemCount = index.Count,
                    ErrorMessage = null
                };
                progress = new FileIndexProgressEventArgs(0, buildRoots.Count, null, 0, 0);
            }
            NotifyIndexStatusChanged();

            try
            {
                var result = baseline is not null && source is IFileIndexSnapshotSource snapshotSource
                    ? await snapshotSource.RefreshAsync(buildRoots, baseline, cancellationToken).ConfigureAwait(false)
                    : await source.BuildAsync(buildRoots, cancellationToken).ConfigureAwait(false);
                var effectiveResult = mergeBaseline is null
                    ? result
                    : MergeRefreshedRoots(mergeBaseline, buildRoots, result);
                var replacement = FileSearchIndex.PrepareReplacement(effectiveResult.Entries);
                var nativeRoots = effectiveResult.Checkpoints
                    .Where(IsNativeJournalCheckpoint)
                    .Select(checkpoint => checkpoint.RootPath)
                    .ToList();
                var refreshNativeRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                lock (stateGate)
                {
                    UpdateTrackedWarnings(buildRoots, result, replaceAll: mergeBaseline is null);
                    var warning = BuildTrackedWarning();
                    var nonNativeChanges = pendingChanges
                        .Where(change =>
                        {
                            var nativeRoot = nativeRoots.FirstOrDefault(root => IsWithinRoot(change.Path, root));
                            if (nativeRoot is null)
                            {
                                return true;
                            }

                            refreshNativeRoots.Add(nativeRoot);
                            return false;
                        })
                        .ToList();
                    index.ReplacePrepared(replacement, nonNativeChanges);
                    pendingChanges.Clear();
                    state = new FileIndexState(
                        FileIndexBuildState.Ready,
                        state.Generation + 1,
                        index.Count,
                        DateTimeOffset.UtcNow,
                        effectiveResult.Checkpoints,
                        warning);
                    rebuildInProgress = false;
                }
                NotifyIndexStatusChanged();

                StartWatchers();
                foreach (var root in refreshNativeRoots)
                {
                    ScheduleRootRefresh(root);
                }

                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var appliedBufferedChanges = false;
                lock (stateGate)
                {
                    var hasPublishedGeneration = state.Generation > 0 || state.LastCompletedAt is not null;
                    if (hasPublishedGeneration && pendingChanges.Count > 0)
                    {
                        index.Apply(pendingChanges);
                        appliedBufferedChanges = true;
                    }

                    rebuildInProgress = false;
                    pendingChanges.Clear();
                    state = state with
                    {
                        BuildState = hasPublishedGeneration ? FileIndexBuildState.Ready : FileIndexBuildState.Empty,
                        ItemCount = index.Count
                    };
                }
                NotifyIndexStatusChanged();

                if (appliedBufferedChanges)
                {
                    SchedulePersistence();
                }

                throw;
            }
            catch (Exception ex)
            {
                var appliedBufferedChanges = false;
                lock (stateGate)
                {
                    var hasPublishedGeneration = state.Generation > 0 || state.LastCompletedAt is not null;
                    if (hasPublishedGeneration && pendingChanges.Count > 0)
                    {
                        index.Apply(pendingChanges);
                        appliedBufferedChanges = true;
                    }

                    rebuildInProgress = false;
                    pendingChanges.Clear();
                    state = state with
                    {
                        BuildState = hasPublishedGeneration ? FileIndexBuildState.Ready : FileIndexBuildState.Failed,
                        ItemCount = index.Count,
                        ErrorMessage = ex.Message
                    };
                }
                NotifyIndexStatusChanged();

                if (appliedBufferedChanges)
                {
                    SchedulePersistence();
                }
            }
        }
        finally
        {
            rebuildGate.Release();
        }
    }

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
        if (!watchChanges)
        {
            return;
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            lock (stateGate)
            {
                if (!watchedRoots.Add(root))
                {
                    continue;
                }
            }

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
                watcher.Created += OnCreatedOrChanged;
                watcher.Changed += OnCreatedOrChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                lock (stateGate)
                {
                    if (disposed)
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                        watchedRoots.Remove(root);
                        continue;
                    }

                    watchers.Add(watcher);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                lock (stateGate)
                {
                    watchedRoots.Remove(root);
                }

                SetState(State with { ErrorMessage = $"변경 감시를 시작하지 못했습니다: {ex.Message}" });
            }
        }
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (FindNativeJournalRoot(e.FullPath) is { } nativeRoot)
        {
            ScheduleRootRefresh(nativeRoot);
            return;
        }

        if (RecursiveFileIndexSource.TryReadEntry(e.FullPath) is { } entry)
        {
            ApplyChange(FileIndexChange.Upsert(entry));
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (FindNativeJournalRoot(e.FullPath) is { } nativeRoot)
        {
            ScheduleRootRefresh(nativeRoot);
            return;
        }

        ApplyChange(FileIndexChange.Delete(e.FullPath));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var oldNativeRoot = FindNativeJournalRoot(e.OldFullPath);
        var newNativeRoot = FindNativeJournalRoot(e.FullPath);
        if (oldNativeRoot is not null || newNativeRoot is not null)
        {
            if (oldNativeRoot is not null)
            {
                ScheduleRootRefresh(oldNativeRoot);
            }

            if (newNativeRoot is not null)
            {
                ScheduleRootRefresh(newNativeRoot);
            }

            return;
        }

        ApplyChange(FileIndexChange.Rename(e.OldFullPath, e.FullPath));
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        FileSystemWatcher? failedWatcher = null;
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            if (sender is FileSystemWatcher watcher)
            {
                failedWatcher = watcher;
                watchers.Remove(watcher);
                watchedRoots.Remove(watcher.Path);
            }
        }

        if (failedWatcher is not null)
        {
            failedWatcher.EnableRaisingEvents = false;
            failedWatcher.Dispose();
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
        NotifyIndexStatusChanged();

        SchedulePersistence();
    }

    private void SchedulePersistence()
    {
        CancellationToken token;
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            persistenceDelay?.Cancel();
            persistenceDelay?.Dispose();
            persistenceDelay = new CancellationTokenSource();
            token = persistenceDelay.Token;
        }

        _ = PersistAfterDelayAsync(token);
    }

    internal void ScheduleRootRefresh(string root)
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            pendingRefreshRoots.Add(root);
            if (refreshWorkerRunning)
            {
                return;
            }

            refreshWorkerRunning = true;
        }

        _ = RefreshRootsLoopAsync();
    }

    private void StartNetworkRevalidation()
    {
        if (!watchChanges
            || networkRevalidationTimer is not null
            || !roots.Any(root => root.StartsWith(@"\\", StringComparison.Ordinal)))
        {
            return;
        }

        networkRevalidationTimer = new Timer(
            _ =>
            {
                foreach (var root in roots.Where(root => root.StartsWith(@"\\", StringComparison.Ordinal)))
                {
                    ScheduleRootRefresh(root);
                }
            },
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));
    }

    private async Task RefreshRootsLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(250),
                    lifetimeCancellation.Token).ConfigureAwait(false);

                IReadOnlyList<string> refreshRoots;
                lock (stateGate)
                {
                    if (disposed)
                    {
                        refreshWorkerRunning = false;
                        return;
                    }

                    refreshRoots = pendingRefreshRoots.ToList();
                    pendingRefreshRoots.Clear();
                }

                await RefreshRootsAsync(refreshRoots, lifetimeCancellation.Token).ConfigureAwait(false);

                lock (stateGate)
                {
                    if (pendingRefreshRoots.Count == 0)
                    {
                        refreshWorkerRunning = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (stateGate)
            {
                refreshWorkerRunning = false;
            }
        }
        catch (ObjectDisposedException)
        {
            lock (stateGate)
            {
                refreshWorkerRunning = false;
            }
        }
        catch (Exception ex)
        {
            lock (stateGate)
            {
                refreshWorkerRunning = false;
                if (!disposed)
                {
                    state = state with { ErrorMessage = $"인덱스 갱신을 완료하지 못했습니다: {ex.Message}" };
                }
            }
        }
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
                snapshot = new FileIndexSnapshot(
                    FileIndexSnapshot.CurrentFormatVersion,
                    state with { ItemCount = index.Count },
                    index.Snapshot());
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
        NotifyIndexStatusChanged();
    }

    private void StopWatchers()
    {
        List<FileSystemWatcher> watchersToDispose;
        lock (stateGate)
        {
            watchersToDispose = [.. watchers];
            watchers.Clear();
            watchedRoots.Clear();
            networkRevalidationTimer?.Dispose();
            networkRevalidationTimer = null;
        }

        foreach (var watcher in watchersToDispose)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreatedOrChanged;
            watcher.Changed -= OnCreatedOrChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
    }

    private void OnSourceProgressChanged(object? sender, FileIndexProgressEventArgs e)
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            progress = e;
        }
        NotifyIndexStatusChanged();
    }

    private void NotifyIndexStatusChanged() =>
        IndexStatusChanged?.Invoke(this, EventArgs.Empty);

    private static FileIndexBuildResult MergeRefreshedRoots(
        FileIndexSnapshot baseline,
        IReadOnlyList<string> refreshedRoots,
        FileIndexBuildResult refreshed)
    {
        var entries = baseline.Entries
            .Where(entry => !refreshedRoots.Any(root => IsWithinRoot(entry.FullPath, root)))
            .Concat(refreshed.Entries)
            .ToList();
        var checkpoints = baseline.State.Checkpoints
            .Where(checkpoint => !refreshedRoots.Any(root =>
                IsWithinRoot(checkpoint.RootPath, root)))
            .ToList();
        checkpoints = checkpoints
            .Concat(refreshed.Checkpoints)
            .ToList();
        return new FileIndexBuildResult(
            entries,
            checkpoints,
            refreshed.Warnings,
            refreshed.SkippedPathCount);
    }

    private void UpdateTrackedWarnings(
        IReadOnlyList<string> refreshedRoots,
        FileIndexBuildResult result,
        bool replaceAll)
    {
        if (replaceAll)
        {
            warningsByRoot.Clear();
        }

        var normalizedRoots = refreshedRoots
            .Select(root => (Original: root, Normalized: NormalizeWarningRoot(root)))
            .DistinctBy(root => root.Normalized, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var root in normalizedRoots)
        {
            warningsByRoot.Remove(root.Normalized);
        }

        // Unscoped warnings cannot safely be attributed to an unaffected root.
        // Drop them after any successful refresh rather than keeping stale state.
        unscopedWarnings.Clear();
        foreach (var warning in result.Warnings)
        {
            var owner = normalizedRoots
                .OrderByDescending(root => root.Original.Length)
                .FirstOrDefault(root => warning.StartsWith(
                    root.Original + ":",
                    StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(owner.Normalized))
            {
                AddTrackedWarning(owner.Normalized, warning);
            }
            else if (normalizedRoots.Count == 1)
            {
                AddTrackedWarning(normalizedRoots[0].Normalized, warning);
            }
            else
            {
                unscopedWarnings.Add(warning);
            }
        }

        if (result.SkippedPathCount > 0 && result.Warnings.Count == 0)
        {
            var warning = $"일부 경로를 건너뜀: {result.SkippedPathCount:N0}개";
            if (normalizedRoots.Count == 1)
            {
                AddTrackedWarning(normalizedRoots[0].Normalized, warning);
            }
            else
            {
                unscopedWarnings.Add(warning);
            }
        }
    }

    private void AddTrackedWarning(string normalizedRoot, string warning)
    {
        if (!warningsByRoot.TryGetValue(normalizedRoot, out var warnings))
        {
            warnings = [];
            warningsByRoot.Add(normalizedRoot, warnings);
        }

        warnings.Add(warning);
    }

    private string? BuildTrackedWarning()
    {
        var warnings = warningsByRoot.Values
            .SelectMany(value => value)
            .Concat(unscopedWarnings)
            .ToList();
        if (warnings.Count == 0)
        {
            return null;
        }

        var remaining = warnings.Count - 1;
        var suffix = remaining > 0 ? $" (외 {remaining:N0}개)" : string.Empty;
        return warnings[0] + suffix;
    }

    private static string NormalizeWarningRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        var pathRoot = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private string? FindNativeJournalRoot(string path) =>
        State.Checkpoints
            .Where(IsNativeJournalCheckpoint)
            .Select(checkpoint => checkpoint.RootPath)
            .FirstOrDefault(root => IsWithinRoot(path, root));

    private static bool IsNativeJournalCheckpoint(FileIndexCheckpoint checkpoint) =>
        checkpoint.JournalId is not null
        && checkpoint.NextUsn is not null
        && string.Equals(checkpoint.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root);
        if (!string.Equals(normalizedRoot, Path.GetPathRoot(normalizedRoot), StringComparison.OrdinalIgnoreCase))
        {
            normalizedRoot = normalizedRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? normalizedRoot
                    : normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
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
        List<FileSystemWatcher> watchersToDispose;
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
            pendingRefreshRoots.Clear();
            networkRevalidationTimer?.Dispose();
            networkRevalidationTimer = null;
            watchersToDispose = [.. watchers];
            watchers.Clear();
            watchedRoots.Clear();
        }

        foreach (var watcher in watchersToDispose)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreatedOrChanged;
            watcher.Changed -= OnCreatedOrChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        if (progressSource is not null)
        {
            progressSource.ProgressChanged -= OnSourceProgressChanged;
        }
    }
}
