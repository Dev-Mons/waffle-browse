using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Waffle.Browse.Core.Docking;
using Waffle.Browse.Core.Search;
using Waffle.Browse.App.Search;

namespace Waffle.Browse.App.Controls;

public partial class SearchResultsView : UserControl, IDisposable
{
    private readonly LatestSearchRequestCoordinator requestCoordinator;
    private readonly DispatcherTimer refreshTimer;
    private SearchQuery? currentQuery;
    private IReadOnlyList<SearchResultItem> currentResults = [];
    private long requestVersion;
    private bool isQueryRunning;
    private bool disposed;
    private Window? ownerWindow;

    public SearchResultsView(ISearchProvider searchService)
    {
        requestCoordinator = new LatestSearchRequestCoordinator(searchService);
        InitializeComponent();
        refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        refreshTimer.Tick += OnRefreshTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public event EventHandler<SearchResultActionRequestedEventArgs>? ResultActionRequested;

    public event EventHandler<string>? StatusChanged;

    public void UpdateSearch(TabState tab)
    {
        if (tab.LocationKind != TabLocationKind.Search || string.IsNullOrWhiteSpace(tab.SearchQuery))
        {
            StopRefreshing();
            return;
        }

        var rootPath = tab.SearchRoots.FirstOrDefault() ?? tab.SearchOriginPath;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            StopRefreshing();
            return;
        }

        var nextQuery = new SearchQuery(
            tab.SearchQuery,
            SearchScope.CurrentFolder,
            1000,
            rootPath,
            currentQuery?.Sort ?? SearchSort.NameAscending);
        var queryChanged = currentQuery is null
            || currentQuery.Text != nextQuery.Text
            || currentQuery.Scope != nextQuery.Scope
            || !string.Equals(currentQuery.RootPath, nextQuery.RootPath, StringComparison.OrdinalIgnoreCase);
        currentQuery = nextQuery;
        UpdateRefreshState();
        if (queryChanged && IsLoaded)
        {
            _ = RefreshAsync(cancelPrevious: true);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ownerWindow = Window.GetWindow(this);
        if (ownerWindow is not null)
        {
            ownerWindow.Activated += OnOwnerWindowStateChanged;
            ownerWindow.Deactivated += OnOwnerWindowStateChanged;
            ownerWindow.StateChanged += OnOwnerWindowStateChanged;
        }
        UpdateRefreshState();
        if (currentQuery is not null)
        {
            await RefreshAsync(cancelPrevious: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ownerWindow is not null)
        {
            ownerWindow.Activated -= OnOwnerWindowStateChanged;
            ownerWindow.Deactivated -= OnOwnerWindowStateChanged;
            ownerWindow.StateChanged -= OnOwnerWindowStateChanged;
            ownerWindow = null;
        }
        StopRefreshing();
        requestCoordinator.Cancel();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRefreshState();

    private void OnOwnerWindowStateChanged(object? sender, EventArgs e) => UpdateRefreshState();

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!isQueryRunning && ShouldRefresh())
        {
            await RefreshAsync(cancelPrevious: false);
        }
    }

    private async Task RefreshAsync(bool cancelPrevious)
    {
        if (disposed || currentQuery is null || (isQueryRunning && !cancelPrevious))
        {
            return;
        }

        var version = Interlocked.Increment(ref requestVersion);
        isQueryRunning = true;
        ResultStatusText.Text = "검색 중…";
        try
        {
            var requestResult = await requestCoordinator.SearchAsync(currentQuery);
            if (version != requestVersion || !requestResult.IsCurrent)
            {
                return;
            }

            var response = requestResult.Response;

            ShowStatus(response.Status);
            if (!response.Status.CanSearch)
            {
                ResultStatusText.Text = response.Status.Kind is SearchProviderStatusKind.Initializing or SearchProviderStatusKind.Rebuilding
                    ? "Waffle 인덱스를 준비하는 중입니다."
                    : currentResults.Count == 0
                        ? "검색을 사용할 수 없습니다."
                        : "기존 결과를 표시하고 있습니다.";
                return;
            }

            if (!ResultsEqual(currentResults, response.Results))
            {
                currentResults = response.Results;
                ResultsList.ItemsSource = currentResults;
            }

            ResultStatusText.Text = $"표시 {response.Results.Count:N0}개 / 전체 {response.TotalResults:N0}개";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or Win32Exception)
        {
            ShowStatus(new SearchProviderStatus(SearchProviderStatusKind.Error, ex.Message, false));
        }
        catch (Exception ex)
        {
            ShowStatus(new SearchProviderStatus(SearchProviderStatusKind.Error, ex.Message, false));
        }
        finally
        {
            if (version == requestVersion)
            {
                isQueryRunning = false;
            }

        }
    }

    private void ShowStatus(SearchProviderStatus status)
    {
        AvailabilityPanel.Visibility = status.CanSearch ? Visibility.Collapsed : Visibility.Visible;
        AvailabilityText.Text = status.Message;
        if (!status.CanSearch)
        {
            StatusChanged?.Invoke(this, status.Message);
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = RefreshAsync(cancelPrevious: true);

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => RequestSelectedAction(SearchResultAction.Open);

    private void OnResultPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) is ListViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RequestSelectedAction(SearchResultAction.Open);
            e.Handled = true;
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => RequestSelectedAction(SearchResultAction.Open);

    private void OnOpenLocationClick(object sender, RoutedEventArgs e) => RequestSelectedAction(SearchResultAction.OpenLocation);

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResultItem item)
        {
            Clipboard.SetText(item.FullPath);
            StatusChanged?.Invoke(this, "전체 경로를 복사했습니다.");
        }
    }

    private void RequestSelectedAction(SearchResultAction action)
    {
        if (ResultsList.SelectedItem is SearchResultItem item)
        {
            ResultActionRequested?.Invoke(this, new SearchResultActionRequestedEventArgs(item, action));
        }
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string column } || currentQuery is null)
        {
            return;
        }

        var sort = column switch
        {
            "Name" => currentQuery.Sort == SearchSort.NameAscending ? SearchSort.NameDescending : SearchSort.NameAscending,
            "Path" => currentQuery.Sort == SearchSort.PathAscending ? SearchSort.PathDescending : SearchSort.PathAscending,
            "Modified" => currentQuery.Sort == SearchSort.ModifiedDescending ? SearchSort.ModifiedAscending : SearchSort.ModifiedDescending,
            "Size" => currentQuery.Sort == SearchSort.SizeDescending ? SearchSort.SizeAscending : SearchSort.SizeDescending,
            _ => currentQuery.Sort
        };
        currentQuery = currentQuery with { Sort = sort };
        _ = RefreshAsync(cancelPrevious: true);
    }

    private void UpdateRefreshState()
    {
        if (ShouldRefresh())
        {
            refreshTimer.Start();
        }
        else
        {
            StopRefreshing();
        }
    }

    private bool ShouldRefresh()
    {
        var window = Window.GetWindow(this);
        return IsLoaded
            && IsVisible
            && currentQuery is not null
            && window is { IsActive: true }
            && window.WindowState != WindowState.Minimized;
    }

    private void StopRefreshing() => refreshTimer.Stop();

    private static bool ResultsEqual(IReadOnlyList<SearchResultItem> left, IReadOnlyList<SearchResultItem> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopRefreshing();
        refreshTimer.Tick -= OnRefreshTimerTick;
        requestCoordinator.Dispose();
    }
}

public enum SearchResultAction
{
    Open,
    OpenLocation
}

public sealed record SearchResultActionRequestedEventArgs(SearchResultItem Item, SearchResultAction Action);
