using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Waffle.Browse.App.Settings;
using Waffle.Browse.App.Search;
using Waffle.Browse.App.Shell;
using Waffle.Browse.Core.Docking;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Controls;

public partial class ExplorerPanelControl : UserControl, IDisposable
{
    private Point? dragStartPoint;
    private Guid? dragCandidateTabId;
    private ShellExplorerHost shellHost;
    private readonly SearchResultsView searchResultsView;
    private string currentShellPath = string.Empty;
    private UiTheme currentTheme = UiTheme.Light;
    private bool isActivePanel;
    private bool disposed;

    public ExplorerPanelControl(PanelState panel, bool isActive, ISearchProvider? searchService = null)
    {
        Panel = panel;
        var resolvedSearchService = searchService ?? UnavailableSearchProvider.Instance;
        InitializeComponent();

        shellHost = CreateShellHost();
        ShellHostContainer.Content = shellHost;
        searchResultsView = new SearchResultsView(resolvedSearchService);
        searchResultsView.ResultActionRequested += OnSearchResultActionRequested;
        searchResultsView.StatusChanged += OnSearchStatusChanged;
        SearchResultsContainer.Content = searchResultsView;
        ApplyPanel(panel, isActive);
    }

    public event EventHandler<PanelNavigationRequestedEventArgs>? NavigationRequested;

    public event EventHandler<PanelPathSubmittedEventArgs>? PathSubmitted;

    public event EventHandler<PanelPathSubmittedEventArgs>? ShellPathChanged;

    public event EventHandler<PanelPathSubmittedEventArgs>? ShellPathNavigationFailed;

    public event EventHandler<PanelTabRequestedEventArgs>? TabAddRequested;

    public event EventHandler<PanelTabRequestedEventArgs>? TabCloseRequested;

    public event EventHandler<PanelTabRequestedEventArgs>? TabOpenRequested;

    public event EventHandler<PanelTabRequestedEventArgs>? TabSelected;

    public event EventHandler<PanelFocusRequestedEventArgs>? PanelFocusRequested;

    public event EventHandler<TabDragStartedEventArgs>? TabDragStarted;

    public event EventHandler<TabDragPointerEventArgs>? TabDragOverPanel;

    public event EventHandler<TabDragPointerEventArgs>? TabDroppedOnPanel;

    public event EventHandler? TabDragCompleted;

    public event EventHandler<SearchResultActionRequestedEventArgs>? SearchResultActionRequested;

    public event EventHandler<string>? SearchStatusChanged;

    public PanelState Panel { get; private set; }

    public void SetShellHostVisible(bool isVisible)
    {
        var isSearch = Panel.ActiveTab?.LocationKind == TabLocationKind.Search;
        ShellHostContainer.Visibility = isVisible && !isSearch ? Visibility.Visible : Visibility.Hidden;
        SearchResultsContainer.Visibility = isVisible && isSearch ? Visibility.Visible : Visibility.Hidden;
    }

    public void ApplyTheme(UiTheme theme)
    {
        if (currentTheme != theme)
        {
            RecreateShellHost(theme);
            return;
        }

        shellHost.ApplyTheme(theme);
    }

    public bool ContainsNativeFocus(IntPtr focusedWindow)
    {
        return shellHost.ContainsNativeFocus(focusedWindow);
    }

    public bool ContainsNativeWindow(IntPtr window)
    {
        return shellHost.ContainsNativeWindow(window);
    }

    public bool FocusNativeWindow(IntPtr window)
    {
        return shellHost.FocusNativeWindow(window);
    }

    public bool ForwardNativeKeyboardInput(IntPtr window, MSG msg)
    {
        return shellHost.ForwardNativeKeyboardInput(window, msg);
    }

    public bool SelectFocusedShellItem(ShellFocusedItemSelectionMode mode)
    {
        return shellHost.SelectFocusedShellItem(mode);
    }

    public bool ActivateShellViewWithFocus()
    {
        return shellHost.ActivateShellViewWithFocus();
    }

    public void UpdatePanel(PanelState panel, bool isActive)
    {
        if (ReferenceEquals(Panel, panel))
        {
            ApplyActiveState(isActive);
            return;
        }

        ApplyPanel(panel, isActive);
    }

    private void ApplyPanel(PanelState panel, bool isActive)
    {
        Panel = panel;
        ApplyActiveState(isActive);
        TabsListBox.ItemsSource = panel.Tabs;
        TabsListBox.SelectedItem = panel.ActiveTab;

        var activeTab = panel.ActiveTab;
        var path = activeTab?.CurrentPath ?? string.Empty;
        AddressBox.Text = activeTab?.LocationKind == TabLocationKind.Search
            ? activeTab.SearchOriginPath ?? string.Empty
            : path;
        if (activeTab is { LocationKind: TabLocationKind.Search }
            && !string.IsNullOrWhiteSpace(activeTab.SearchQuery))
        {
            ShellHostContainer.Visibility = Visibility.Hidden;
            SearchResultsContainer.Visibility = Visibility.Visible;
            searchResultsView.UpdateSearch(activeTab);
        }
        else
        {
            SearchResultsContainer.Visibility = Visibility.Collapsed;
            ShellHostContainer.Visibility = Visibility.Visible;
            NavigateShellIfChanged(path);
        }
    }

    private void ApplyActiveState(bool isActive)
    {
        isActivePanel = isActive;
        RootBorder.BorderBrush = isActive
            ? (Brush)FindResource("ActivePanelBorderBrush")
            : (Brush)FindResource("PanelBorderBrush");
        RootBorder.BorderThickness = new Thickness(2);
    }

    private ShellExplorerHost CreateShellHost()
    {
        var host = new ShellExplorerHost(string.Empty);
        host.NavigationCompleted += OnShellNavigationCompleted;
        host.NavigationFailed += OnShellNavigationFailed;
        return host;
    }

    private void RecreateShellHost(UiTheme theme)
    {
        var previousHost = shellHost;
        previousHost.NavigationCompleted -= OnShellNavigationCompleted;
        previousHost.NavigationFailed -= OnShellNavigationFailed;
        ShellHostContainer.Content = null;
        if (previousHost is IDisposable disposable)
        {
            disposable.Dispose();
        }

        shellHost = CreateShellHost();
        ShellHostContainer.Content = shellHost;
        currentShellPath = string.Empty;
        currentTheme = theme;
        shellHost.ApplyTheme(theme);
        ApplyPanel(Panel, isActivePanel);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        NavigationRequested?.Invoke(this, new PanelNavigationRequestedEventArgs(Panel.Id, PanelNavigation.Back));
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        NavigationRequested?.Invoke(this, new PanelNavigationRequestedEventArgs(Panel.Id, PanelNavigation.Forward));
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        NavigationRequested?.Invoke(this, new PanelNavigationRequestedEventArgs(Panel.Id, PanelNavigation.Up));
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        PathSubmitted?.Invoke(this, new PanelPathSubmittedEventArgs(Panel.Id, AddressBox.Text.Trim()));
        e.Handled = true;
    }

    private void OnShellNavigationCompleted(object? sender, string path)
    {
        currentShellPath = path;
        AddressBox.Text = path;
        ShellPathChanged?.Invoke(this, new PanelPathSubmittedEventArgs(Panel.Id, path));
    }

    private void OnShellNavigationFailed(object? sender, string path)
    {
        ShellPathNavigationFailed?.Invoke(this, new PanelPathSubmittedEventArgs(Panel.Id, path));
    }

    private void RequestPanelFocus()
    {
        PanelFocusRequested?.Invoke(this, new PanelFocusRequestedEventArgs(Panel.Id));
    }

    private void NavigateShellIfChanged(string path)
    {
        if (PathEquals(currentShellPath, path))
        {
            return;
        }

        currentShellPath = path;
        shellHost.Navigate(path);
    }

    private void OnSearchResultActionRequested(object? sender, SearchResultActionRequestedEventArgs e)
        => SearchResultActionRequested?.Invoke(this, e);

    private void OnSearchStatusChanged(object? sender, string e)
        => SearchStatusChanged?.Invoke(this, e);

    private void OnSearchResultsPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        RequestPanelFocus();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        searchResultsView.ResultActionRequested -= OnSearchResultActionRequested;
        searchResultsView.StatusChanged -= OnSearchStatusChanged;
        searchResultsView.Dispose();
        shellHost.NavigationCompleted -= OnShellNavigationCompleted;
        shellHost.NavigationFailed -= OnShellNavigationFailed;
        if (shellHost is IDisposable disposableShellHost)
        {
            disposableShellHost.Dispose();
        }

    }

    private sealed class UnavailableSearchProvider : ISearchProvider
    {
        public static UnavailableSearchProvider Instance { get; } = new();

        public string Id => "unavailable";

        public string DisplayName => "검색 비활성";

        public Task<SearchProviderStatus> CheckStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchProviderStatus(SearchProviderStatusKind.Unavailable, "검색 인덱스가 연결되지 않았습니다.", false));

        public Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResponse([], 0, new SearchProviderStatus(
                SearchProviderStatusKind.Unavailable,
                "검색 인덱스가 연결되지 않았습니다.",
                false), Id));
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private void OnCloseTabMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (FindTabFromMenuItem(sender) is { } tab)
        {
            TabCloseRequested?.Invoke(this, new PanelTabRequestedEventArgs(Panel.Id, tab.Id));
            e.Handled = true;
        }
    }

    private void OnOpenTabMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (FindTabFromMenuItem(sender) is { } tab)
        {
            TabOpenRequested?.Invoke(this, new PanelTabRequestedEventArgs(Panel.Id, tab.Id));
            e.Handled = true;
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabsListBox.SelectedItem is TabState tab && Panel.ActiveTabId != tab.Id)
        {
            TabSelected?.Invoke(this, new PanelTabRequestedEventArgs(Panel.Id, tab.Id));
        }
    }

    private void OnTabMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RequestPanelFocus();
        var tab = FindTabFromEvent(e);
        if (tab is null)
        {
            TabAddRequested?.Invoke(this, new PanelTabRequestedEventArgs(Panel.Id, Guid.Empty));
            e.Handled = true;
            return;
        }

        dragStartPoint = e.GetPosition(this);
        dragCandidateTabId = tab.Id;
        TabsListBox.SelectedItem = tab;
    }

    private void OnTabPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        if (sender is ListBoxItem { DataContext: TabState tab })
        {
            RequestPanelFocus();
            TabCloseRequested?.Invoke(this, new PanelTabRequestedEventArgs(Panel.Id, tab.Id));
            e.Handled = true;
        }
    }

    private void OnTabPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        RequestPanelFocus();
        if (sender is ListBoxItem { DataContext: TabState tab })
        {
            TabsListBox.SelectedItem = tab;
        }
    }

    private void OnTabPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragStartPoint is null)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - dragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - dragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (dragCandidateTabId is not { } tabId)
        {
            return;
        }

        var tab = Panel.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab is null)
        {
            return;
        }

        var payload = new DockDragPayload(Panel.Id, tab.Id);
        var data = new DataObject();
        data.SetData(TabDragPayload.Format, new TabDragPayload(payload.SourcePanelId, payload.TabId));
        TabDragStarted?.Invoke(this, new TabDragStartedEventArgs(payload));
        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        }
        finally
        {
            dragStartPoint = null;
            dragCandidateTabId = null;
            TabDragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPanelDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabDragPayload.Format))
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPanelDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = e.Data.GetDataPresent(TabDragPayload.Format);
    }

    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TabDragPayload.Format) is not TabDragPayload payload)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        TabDragOverPanel?.Invoke(this, new TabDragPointerEventArgs(
            new DockDragPayload(payload.SourcePanelId, payload.TabId),
            Panel.Id,
            e.GetPosition(ShellHostArea),
            ShellHostArea));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPanelDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TabDragPayload.Format) is not TabDragPayload payload)
        {
            return;
        }

        TabDroppedOnPanel?.Invoke(this, new TabDragPointerEventArgs(
            new DockDragPayload(payload.SourcePanelId, payload.TabId),
            Panel.Id,
            e.GetPosition(ShellHostArea),
            ShellHostArea));
        e.Handled = true;
    }

    private TabState? FindTabFromEvent(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return null;
        }

        var item = FindAncestor<ListBoxItem>(source);
        return item?.DataContext as TabState;
    }

    private static TabState? FindTabFromMenuItem(object sender)
    {
        return sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TabState tab } } }
            ? tab
            : null;
    }

    private static T? FindAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T ancestor)
            {
                return ancestor;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        return current is Visual or Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);
    }
}

public enum PanelNavigation
{
    Back,
    Forward,
    Up
}

public sealed record PanelNavigationRequestedEventArgs(Guid PanelId, PanelNavigation Navigation);

public sealed record PanelPathSubmittedEventArgs(Guid PanelId, string Path);

public sealed record PanelTabRequestedEventArgs(Guid PanelId, Guid TabId);

public sealed record PanelFocusRequestedEventArgs(Guid PanelId);

public sealed record TabDragStartedEventArgs(DockDragPayload Payload);

public sealed record TabDragPointerEventArgs(
    DockDragPayload Payload,
    Guid TargetPanelId,
    Point PointerInPanel,
    FrameworkElement TargetElement);

[Serializable]
public sealed record TabDragPayload(Guid SourcePanelId, Guid TabId)
{
    public const string Format = "Waffle.Browse.TabDragPayload";
}
