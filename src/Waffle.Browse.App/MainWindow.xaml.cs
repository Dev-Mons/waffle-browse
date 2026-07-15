using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Waffle.Browse.App.Diagnostics;
using Waffle.Browse.App.Controls;
using Waffle.Browse.App.Docking;
using Waffle.Browse.App.Settings;
using Waffle.Browse.App.Shell;
using Waffle.Browse.App.Theming;
using Waffle.Browse.Core.Docking;
using Waffle.Browse.Core.Navigation;
using Waffle.Browse.Core.Persistence;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Waffle.Browse.App;

public partial class MainWindow : Window
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSetFocus = 0x0007;
    private const int WmKillFocus = 0x0008;
    private const int WmMouseActivate = 0x0021;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonUp = 0x020C;
    private const int VkBack = 0x08;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkMenu = 0x12;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;

    private readonly DockLayoutService layoutService = new();
    private readonly DockDropHitTester dropHitTester = new();
    private readonly DockDropTargetResolver dropTargetResolver = new();
    private readonly DockLayoutStore layoutStore;
    private readonly UiSettingsStore settingsStore;
    private readonly ShellSearchTargetResolver shellSearchTargetResolver;
    private readonly WindowNativeThemeApplier windowNativeThemeApplier = new();
    private readonly NativeFocusEventTracer nativeFocusEventTracer = new();
    private readonly string fallbackPath;
    private DockLayoutState layoutState;
    private UiSettings settings = new();
    private bool isApplyingSettingsToUi;
    private bool isUpdatingSearchBox;
    private DockDragPayload? activeDragPayload;
    private DockDropPreview? activeDropPreview;
    private readonly Dictionary<Guid, ExplorerPanelControl> panelControlsById = [];

    public MainWindow()
    {
        fallbackPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appDataPath = ApplicationDataPath.Resolve();
        layoutStore = new DockLayoutStore(Path.Combine(appDataPath, "layout.json"));
        settingsStore = new UiSettingsStore(Path.Combine(appDataPath, "settings.json"));
        shellSearchTargetResolver = new ShellSearchTargetResolver();
        layoutState = layoutService.CreateDefault(fallbackPath);

        InitializeComponent();

        FocusTraceLogger.StartSession();
        nativeFocusEventTracer.Start((window, objectId, childId) =>
            Dispatcher.BeginInvoke(
                () => TraceFocusSnapshot(
                    "win-event-object-focus",
                    ResolveNativePanelId(window),
                    window,
                    $"objectId={objectId};childId={childId}"),
                DispatcherPriority.Background));
        DockPreviewOverlay.DragPointerMoved += OnWorkspaceDragOver;
        DockPreviewOverlay.DragDropped += OnWorkspaceDrop;
        DockPreviewOverlay.DragPointerLeft += OnWorkspaceDragLeave;
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        settings = settingsStore.Load();
        ApplySettingsToUi();
        layoutState = layoutStore.LoadOrDefault(fallbackPath, Directory.Exists);
        layoutState = RefreshSearchTargets(layoutState);
        RenderLayout();
        UpdateSearchBoxFromActiveTab();
        SetStatus("Layout restored.");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        nativeFocusEventTracer.Dispose();
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        SaveSettings();
        SaveLayout();
    }

    private void OnOnePanelClick(object sender, RoutedEventArgs e)
    {
        ApplyLayout(layoutService.SetVisiblePanelCount(layoutState, 1), "Single panel layout.");
    }

    private void OnTwoColumnClick(object sender, RoutedEventArgs e)
    {
        ApplyLayout(layoutService.SetLayout(layoutState, DockLayoutKind.OneByTwo), "Two-column layout.");
    }

    private void OnTwoRowClick(object sender, RoutedEventArgs e)
    {
        ApplyLayout(layoutService.SetLayout(layoutState, DockLayoutKind.TwoByOne), "Two-row layout.");
    }

    private void OnThreePanelClick(object sender, RoutedEventArgs e)
    {
        ApplyLayout(layoutService.SetVisiblePanelCount(layoutState, 3), "Three-panel layout.");
    }

    private void OnFourPanelClick(object sender, RoutedEventArgs e)
    {
        ApplyLayout(layoutService.SetVisiblePanelCount(layoutState, 4), "Four-panel layout.");
    }

    private void OnThemeToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || isApplyingSettingsToUi)
        {
            return;
        }

        var theme = ResolveSelectedTheme();
        settings = settings with { Theme = theme };
        ApplyTheme(theme);
        SaveSettings();
        SetStatus(theme == UiTheme.Dark ? "Dark mode enabled." : "Light mode enabled.");
    }

    private void OnQuickSearchClick(object sender, RoutedEventArgs e)
    {
        RunQuickSearch();
    }

    private void OnQuickSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        RunQuickSearch();
        e.Handled = true;
    }

    private void OnQuickSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (isUpdatingSearchBox || !string.IsNullOrWhiteSpace(QuickSearchBox.Text))
        {
            return;
        }

        ClearActiveSearch();
    }

    private void RunQuickSearch()
    {
        var text = QuickSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ClearActiveSearch();
            return;
        }

        var panelId = ActiveOrFirstVisiblePanelId();
        if (panelId is null)
        {
            SetStatus("No panel is available for search.");
            return;
        }

        var roots = ResolveCurrentPanelSearchRoots();
        if (roots.Count == 0)
        {
            SetStatus("No searchable folder is open.");
            return;
        }

        try
        {
            var searchTarget = shellSearchTargetResolver.Resolve(text, roots);
            ApplyLayout(
                layoutService.NavigateToSearch(layoutState, panelId.Value, text, roots, searchTarget),
                "Search opened in current tab.");
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void ClearActiveSearch()
    {
        if (ActiveOrFirstVisiblePanelId() is not { } panelId)
        {
            return;
        }

        var nextState = layoutService.ClearSearch(layoutState, panelId);
        if (ReferenceEquals(nextState, layoutState))
        {
            return;
        }

        ApplyLayout(nextState, "Search cleared.");
    }

    private List<string> ResolveCurrentPanelSearchRoots()
    {
        var paths = ActiveOrFirstVisiblePanelId() is { } panelId
            ? new List<string?> { ResolveSearchRootPath(layoutState.FindPanel(panelId).ActiveTab) }
            : [];

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveSearchRootPath(TabState? tab)
    {
        return tab?.LocationKind == TabLocationKind.Search
            ? tab.SearchOriginPath
            : tab?.CurrentPath;
    }

    private DockLayoutState RefreshSearchTargets(DockLayoutState state)
    {
        var panels = state.Panels.Select(panel =>
        {
            var tabs = panel.Tabs.Select(tab =>
            {
                if (tab.LocationKind != TabLocationKind.Search
                    || string.IsNullOrWhiteSpace(tab.SearchQuery)
                    || tab.SearchRoots.Count == 0)
                {
                    return tab;
                }

                try
                {
                    return tab with
                    {
                        CurrentPath = shellSearchTargetResolver.Resolve(tab.SearchQuery, tab.SearchRoots)
                    };
                }
                catch (ArgumentException)
                {
                    return tab;
                }
                catch (InvalidOperationException)
                {
                    return tab;
                }
                catch (IOException)
                {
                    return tab;
                }
                catch (UnauthorizedAccessException)
                {
                    return tab;
                }
            }).ToList();

            return panel with { Tabs = tabs };
        }).ToList();

        return state with { Panels = panels };
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        TraceFocusMessage("thread-filter", msg, handled);

        // While the native shell shows its inline rename (label-edit) box, leave the
        // keystrokes and clicks for Explorer to handle: forwarding keys to the shell view,
        // syncing selection, or restoring shell-view focus would move the caret to the
        // column headers, cancel the rename, or steal focus from the edit box. The one
        // exception is arrow keys, which WPF would otherwise consume for directional focus
        // navigation and yank focus out of the edit box; deliver those to the edit window
        // and mark them handled so they stay inside the rename.
        if (IsNativeShellRenameActive())
        {
            var renameKey = unchecked((int)msg.wParam.ToInt64());
            if (NativeShellRenameStateClassifier.ShouldRouteKeyToRenameEdit(msg.message, renameKey))
            {
                SendMessage(GetFocus(), msg.message, msg.wParam, msg.lParam);
                handled = true;
            }

            return;
        }

        if (TryForwardNativeShellKeyboardInput(msg, ref handled))
        {
            return;
        }

        ActivatePanelFromNativeMessage(msg);
        ScheduleNativeShellSelectionSync(msg);

        if (handled || TryGetNavigationShortcut(msg) is not { } shortcut)
        {
            return;
        }

        if (shortcut == FolderNavigationShortcut.Backspace && IsTextEditingFocused())
        {
            return;
        }

        var panelId = ResolveShortcutPanelId(shortcut);
        if (panelId is null)
        {
            return;
        }

        NavigatePanel(
            panelId.Value,
            FolderNavigationShortcutMapper.ToAction(shortcut),
            "Navigation updated.");
        handled = true;
    }

    private bool TryForwardNativeShellKeyboardInput(MSG msg, ref bool handled)
    {
        if (handled
            || NativeShellKeyboardInputClassifier.Resolve(msg.message, msg.wParam) != NativeShellKeyboardInputHandling.ForwardAndHandle)
        {
            return false;
        }

        var focusedWindow = GetFocus();
        foreach (var (panelId, control) in panelControlsById)
        {
            if (!control.ContainsNativeFocus(focusedWindow))
            {
                continue;
            }

            var targetWindow = control.ContainsNativeWindow(msg.hwnd) ? msg.hwnd : focusedWindow;
            if (!control.ForwardNativeKeyboardInput(targetWindow, msg))
            {
                continue;
            }

            if (!IsSelectionModifierPressed())
            {
                var selected = control.SelectFocusedShellItem(ShellFocusedItemSelectionMode.Keyboard);
                TraceFocusSnapshot(
                    "native-shell-focused-item-selected",
                    panelId,
                    targetWindow,
                    $"mode=keyboard;selected={selected}");
            }

            handled = true;
            TraceFocusSnapshot(
                "native-shell-key-forwarded",
                panelId,
                targetWindow,
                MessageName(msg.message));
            return true;
        }

        return false;
    }

    private void ScheduleNativeShellSelectionSync(MSG msg)
    {
        if (msg.message != WmLButtonUp || IsSelectionModifierPressed())
        {
            return;
        }

        foreach (var (panelId, control) in panelControlsById)
        {
            if (!control.ContainsNativeWindow(msg.hwnd))
            {
                continue;
            }

            var targetWindow = msg.hwnd;
            Dispatcher.BeginInvoke(
                () => SelectFocusedShellItemAfterMessage(panelId, targetWindow, ShellFocusedItemSelectionMode.Mouse),
                DispatcherPriority.Background);
            return;
        }
    }

    private void SelectFocusedShellItemAfterMessage(Guid panelId, IntPtr targetWindow, ShellFocusedItemSelectionMode mode)
    {
        if (!panelControlsById.TryGetValue(panelId, out var control)
            || !control.ContainsNativeWindow(targetWindow))
        {
            return;
        }

        var selected = control.SelectFocusedShellItem(mode);
        TraceFocusSnapshot(
            "native-shell-focused-item-selected",
            panelId,
            targetWindow,
            $"mode={mode};selected={selected}");
    }

    private void RenderLayout()
    {
        DockPreviewOverlay.EndDragCapture();
        panelControlsById.Clear();
        WorkspaceGrid.Children.Clear();
        WorkspaceGrid.RowDefinitions.Clear();
        WorkspaceGrid.ColumnDefinitions.Clear();

        if (layoutState.Grid is null || !CanRenderGrid(layoutState.Grid))
        {
            RenderPresetLayout();
            return;
        }

        WorkspaceGrid.RowDefinitions.Add(new RowDefinition());
        WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var visiblePanels = layoutState.VisiblePanels.ToDictionary(panel => panel.Id);
        WorkspaceGrid.Children.Add(CreateGridElement(layoutState.Grid.Root, visiblePanels));
    }

    private void RenderPresetLayout()
    {
        foreach (var definition in CreateRows(layoutState.LayoutKind))
        {
            WorkspaceGrid.RowDefinitions.Add(definition);
        }

        foreach (var definition in CreateColumns(layoutState.LayoutKind))
        {
            WorkspaceGrid.ColumnDefinitions.Add(definition);
        }

        var visiblePanels = layoutState.VisiblePanels;
        for (var index = 0; index < visiblePanels.Count; index++)
        {
            var panel = visiblePanels[index];
            var control = CreatePanelControl(panel);

            var placement = GetPlacement(layoutState.LayoutKind, index);
            Grid.SetRow(control, placement.Row);
            Grid.SetColumn(control, placement.Column);
            Grid.SetRowSpan(control, placement.RowSpan);
            Grid.SetColumnSpan(control, placement.ColumnSpan);
            WorkspaceGrid.Children.Add(control);
        }
    }

    private ExplorerPanelControl CreatePanelControl(PanelState panel)
    {
        var control = new ExplorerPanelControl(panel, layoutState.ActivePanelId == panel.Id);
        panelControlsById[panel.Id] = control;
        control.NavigationRequested += OnPanelNavigationRequested;
        control.PathSubmitted += OnPanelPathSubmitted;
        control.ShellPathChanged += OnPanelShellPathChanged;
        control.ShellPathNavigationFailed += OnPanelShellPathNavigationFailed;
        control.TabAddRequested += OnTabAddRequested;
        control.TabCloseRequested += OnTabCloseRequested;
        control.TabOpenRequested += OnTabOpenRequested;
        control.TabSelected += OnTabSelected;
        control.PanelFocusRequested += OnPanelFocusRequested;
        control.TabDragStarted += OnTabDragStarted;
        control.TabDragOverPanel += OnTabDragOverPanel;
        control.TabDroppedOnPanel += OnTabDroppedOnPanel;
        control.TabDragCompleted += OnTabDragCompleted;
        control.ApplyTheme(settings.Theme);
        return control;
    }

    private FrameworkElement CreateGridElement(DockNode node, IReadOnlyDictionary<Guid, PanelState> visiblePanels)
    {
        return node switch
        {
            DockLeaf leaf when visiblePanels.TryGetValue(leaf.PanelId, out var panel) => CreatePanelControl(panel),
            DockSplit split => CreateSplitGrid(split, visiblePanels),
            _ => new Grid()
        };
    }

    private Grid CreateSplitGrid(DockSplit split, IReadOnlyDictionary<Guid, PanelState> visiblePanels)
    {
        var grid = new Grid();
        var first = CreateGridElement(split.First, visiblePanels);
        var second = CreateGridElement(split.Second, visiblePanels);
        var ratio = Math.Clamp(split.Ratio, 0.05, 0.95);

        if (split.Orientation == DockOrientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
            Grid.SetColumn(second, 1);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star) });
            Grid.SetRow(second, 1);
        }

        grid.Children.Add(first);
        grid.Children.Add(second);
        return grid;
    }

    private bool CanRenderGrid(DockGridState grid)
    {
        var visiblePanelIds = layoutState.Panels.Where(panel => panel.IsVisible).Select(panel => panel.Id).ToHashSet();
        var gridPanelIds = new DockGridService().GetLeafPanelIds(grid);
        return gridPanelIds.Count == visiblePanelIds.Count
            && gridPanelIds.All(visiblePanelIds.Contains);
    }

    private FolderNavigationShortcut? TryGetNavigationShortcut(MSG msg)
    {
        if (msg.message == WmXButtonUp)
        {
            return HiWord(msg.wParam) switch
            {
                XButton1 => FolderNavigationShortcut.MouseBackButton,
                XButton2 => FolderNavigationShortcut.MouseForwardButton,
                _ => null
            };
        }

        if (msg.message is not WmKeyDown and not WmSysKeyDown)
        {
            return null;
        }

        var virtualKey = unchecked((int)msg.wParam.ToInt64());
        if (msg.message == WmKeyDown && virtualKey == VkBack)
        {
            return FolderNavigationShortcut.Backspace;
        }

        if (IsAltPressed())
        {
            return virtualKey switch
            {
                VkLeft => FolderNavigationShortcut.AltLeft,
                VkRight => FolderNavigationShortcut.AltRight,
                _ => null
            };
        }

        return null;
    }

    private Guid? ResolveShortcutPanelId(FolderNavigationShortcut shortcut)
    {
        return shortcut is FolderNavigationShortcut.MouseBackButton or FolderNavigationShortcut.MouseForwardButton
            ? ResolvePanelIdUnderMouse() ?? ResolveFocusedPanelId() ?? ActiveOrFirstVisiblePanelId()
            : ResolveFocusedPanelId() ?? ActiveOrFirstVisiblePanelId();
    }

    private Guid? ResolvePanelIdUnderMouse()
    {
        if (!GetCursorPos(out var screenPoint))
        {
            return null;
        }

        var workspacePoint = WorkspaceRoot.PointFromScreen(new Point(screenPoint.X, screenPoint.Y));
        var target = dropTargetResolver.Resolve(
            CreateDropTargets(),
            new DockPoint(workspacePoint.X, workspacePoint.Y));
        return target?.PanelId;
    }

    private Guid? ResolveFocusedPanelId()
    {
        foreach (var (panelId, control) in panelControlsById)
        {
            if (control.IsKeyboardFocusWithin || control.ContainsNativeFocus(GetFocus()))
            {
                return panelId;
            }
        }

        return null;
    }

    private void ActivatePanelFromNativeMessage(MSG msg)
    {
        if (NativeShellActivationClassifier.Resolve(msg.message) != NativeShellActivationTiming.ActivatePanelThenRestoreFocus)
        {
            return;
        }

        foreach (var (panelId, control) in panelControlsById)
        {
            if (control.ContainsNativeWindow(msg.hwnd))
            {
                var targetWindow = msg.hwnd;
                TraceFocusSnapshot("native-shell-activation-scheduled", panelId, targetWindow, MessageName(msg.message));
                Dispatcher.BeginInvoke(
                    () => ActivatePanelAndRestoreNativeShellFocusAfterMessage(panelId, targetWindow),
                    DispatcherPriority.Background);
                return;
            }
        }
    }

    private void ActivatePanelAndRestoreNativeShellFocusAfterMessage(Guid panelId, IntPtr targetWindow)
    {
        if (panelControlsById.TryGetValue(panelId, out var control)
            && control.ContainsNativeWindow(targetWindow))
        {
            TraceFocusSnapshot("native-shell-restore-before", panelId, targetWindow, null);
            ActivatePanel(panelId);
            var focusedNativeWindow = control.FocusNativeWindow(targetWindow);
            var activatedShellView = control.ActivateShellViewWithFocus();
            TraceFocusSnapshot(
                "native-shell-restore-after",
                panelId,
                targetWindow,
                $"focusNativeWindow={focusedNativeWindow};activateShellView={activatedShellView}");
        }
    }

    private void TraceFocusMessage(string stage, MSG msg, bool handled)
    {
        if (!FocusTraceLogger.IsEnabled)
        {
            return;
        }

        var currentFocus = GetFocus();
        var messagePanelId = ResolveNativePanelId(msg.hwnd);
        var focusPanelId = ResolveNativePanelId(currentFocus);
        if (!ShouldTraceFocusMessage(msg, messagePanelId, focusPanelId))
        {
            return;
        }

        FocusTraceLogger.Write(new FocusTraceEntry(
            DateTimeOffset.Now,
            stage,
            MessageName(msg.message),
            msg.message,
            msg.hwnd,
            WindowClassName(msg.hwnd),
            msg.wParam,
            currentFocus,
            WindowClassName(currentFocus),
            DescribeWpfFocus(),
            messagePanelId,
            focusPanelId,
            handled,
            null));
    }

    private void TraceFocusSnapshot(string stage, Guid? panelId, IntPtr relatedWindow, string? details)
    {
        if (!FocusTraceLogger.IsEnabled)
        {
            return;
        }

        var currentFocus = GetFocus();
        FocusTraceLogger.Write(new FocusTraceEntry(
            DateTimeOffset.Now,
            stage,
            "SNAPSHOT",
            0,
            relatedWindow,
            WindowClassName(relatedWindow),
            IntPtr.Zero,
            currentFocus,
            WindowClassName(currentFocus),
            DescribeWpfFocus(),
            panelId ?? ResolveNativePanelId(relatedWindow),
            ResolveNativePanelId(currentFocus),
            false,
            details));
    }

    private Guid? ResolveNativePanelId(IntPtr window)
    {
        foreach (var (panelId, control) in panelControlsById)
        {
            if (control.ContainsNativeWindow(window))
            {
                return panelId;
            }
        }

        return null;
    }

    private static bool ShouldTraceFocusMessage(MSG msg, Guid? messagePanelId, Guid? focusPanelId)
    {
        if (msg.message is WmSetFocus or WmKillFocus)
        {
            return true;
        }

        if (IsTraceKeyMessage(msg))
        {
            return true;
        }

        var isTraceMouseMessage = msg.message is WmMouseActivate
                or WmLButtonDown
                or WmLButtonUp
                or WmRButtonDown
                or WmRButtonUp
                or WmMButtonDown
                or WmMButtonUp
                or WmXButtonUp;

        return isTraceMouseMessage && (messagePanelId is not null || focusPanelId is not null);
    }

    private static bool IsTraceKeyMessage(MSG msg)
    {
        if (msg.message is not WmKeyDown and not WmSysKeyDown)
        {
            return false;
        }

        var virtualKey = unchecked((int)msg.wParam.ToInt64());
        return virtualKey is VkBack or VkLeft or VkUp or VkRight or VkDown;
    }

    private static string MessageName(int message)
    {
        return message switch
        {
            WmSetFocus => "WM_SETFOCUS",
            WmKillFocus => "WM_KILLFOCUS",
            WmMouseActivate => "WM_MOUSEACTIVATE",
            WmKeyDown => "WM_KEYDOWN",
            WmSysKeyDown => "WM_SYSKEYDOWN",
            WmLButtonDown => "WM_LBUTTONDOWN",
            WmLButtonUp => "WM_LBUTTONUP",
            WmRButtonDown => "WM_RBUTTONDOWN",
            WmRButtonUp => "WM_RBUTTONUP",
            WmMButtonDown => "WM_MBUTTONDOWN",
            WmMButtonUp => "WM_MBUTTONUP",
            WmXButtonUp => "WM_XBUTTONUP",
            _ => "0x" + message.ToString("X4")
        };
    }

    private static string DescribeWpfFocus()
    {
        return Keyboard.FocusedElement switch
        {
            null => "-",
            FrameworkElement { Name.Length: > 0 } element => $"{element.GetType().Name}#{element.Name}",
            FrameworkElement element => element.GetType().Name,
            var element => element.GetType().Name
        };
    }

    private static string WindowClassName(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return "-";
        }

        var builder = new StringBuilder(256);
        var length = GetClassName(window, builder, builder.Capacity);
        return length > 0 ? builder.ToString() : "?";
    }

    private Guid? ActiveOrFirstVisiblePanelId()
    {
        return layoutState.ActivePanelId ?? layoutState.VisiblePanels.FirstOrDefault()?.Id;
    }

    private static FolderNavigationAction ToFolderNavigationAction(PanelNavigation navigation)
    {
        return navigation switch
        {
            PanelNavigation.Back => FolderNavigationAction.Back,
            PanelNavigation.Forward => FolderNavigationAction.Forward,
            PanelNavigation.Up => FolderNavigationAction.Up,
            _ => FolderNavigationAction.Back
        };
    }

    private static bool IsTextEditingFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase or PasswordBox;
    }

    private bool IsNativeShellRenameActive()
    {
        var focused = GetFocus();
        if (focused == IntPtr.Zero)
        {
            return false;
        }

        var belongsToShellPanel = panelControlsById.Values.Any(control => control.ContainsNativeWindow(focused));
        return NativeShellRenameStateClassifier.IsInlineRenameActive(WindowClassName(focused), belongsToShellPanel);
    }

    private static bool IsAltPressed()
    {
        return (GetKeyState(VkMenu) & 0x8000) != 0;
    }

    private static bool IsSelectionModifierPressed()
    {
        return (GetKeyState(VkShift) & 0x8000) != 0
            || (GetKeyState(VkControl) & 0x8000) != 0;
    }

    private static int HiWord(IntPtr value)
    {
        return (int)((value.ToInt64() >> 16) & 0xFFFF);
    }

    private void OnPanelNavigationRequested(object? sender, PanelNavigationRequestedEventArgs e)
    {
        NavigatePanel(e.PanelId, ToFolderNavigationAction(e.Navigation), "Navigation updated.");
    }

    private void NavigatePanel(Guid panelId, FolderNavigationAction action, string status)
    {
        var next = action switch
        {
            FolderNavigationAction.Back => layoutService.NavigateBack(layoutState, panelId),
            FolderNavigationAction.Forward => layoutService.NavigateForward(layoutState, panelId),
            FolderNavigationAction.Up => layoutService.NavigateUp(layoutState, panelId),
            _ => layoutState
        };

        ApplyLayout(next, status);
    }

    private void OnPanelPathSubmitted(object? sender, PanelPathSubmittedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Path))
        {
            SetStatus("Path is empty.");
            return;
        }

        ApplyLayout(layoutService.NavigateTo(layoutState, e.PanelId, e.Path), "Path updated.");
    }

    private void OnPanelShellPathChanged(object? sender, PanelPathSubmittedEventArgs e)
    {
        var currentPath = layoutState.FindPanel(e.PanelId).ActiveTab?.CurrentPath;
        if (string.Equals(currentPath, e.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyState(layoutService.NavigateTo(layoutState, e.PanelId, e.Path), "Shell path updated.");
    }

    private void OnPanelShellPathNavigationFailed(object? sender, PanelPathSubmittedEventArgs e)
    {
        var tab = layoutState.FindPanel(e.PanelId).ActiveTab;
        if (tab is null)
        {
            SetStatus("Shell navigation failed.");
            return;
        }

        if (!string.Equals(tab.CurrentPath, e.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetStatus(tab.LocationKind == TabLocationKind.Search
            ? "Shell search navigation failed."
            : "Shell navigation failed.");
    }

    private void OnTabAddRequested(object? sender, PanelTabRequestedEventArgs e)
    {
        var panel = layoutState.FindPanel(e.PanelId);
        var path = panel.ActiveTab?.CurrentPath ?? fallbackPath;
        ApplyLayout(layoutService.AddTab(layoutState, e.PanelId, path), "Tab added.");
    }

    private void OnTabCloseRequested(object? sender, PanelTabRequestedEventArgs e)
    {
        ApplyLayout(layoutService.CloseTab(layoutState, e.PanelId, e.TabId), "Tab closed.");
    }

    private void OnTabOpenRequested(object? sender, PanelTabRequestedEventArgs e)
    {
        var panel = layoutState.FindPanel(e.PanelId);
        var tab = panel.Tabs.FirstOrDefault(item => item.Id == e.TabId);
        if (tab is null || string.IsNullOrWhiteSpace(tab.CurrentPath))
        {
            SetStatus("Tab path is empty.");
            return;
        }

        try
        {
            OpenFolderInExplorer(tab.CurrentPath);
            SetStatus("Tab folder opened.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            SetStatus($"Could not open folder: {ex.Message}");
        }
    }

    private void OnTabSelected(object? sender, PanelTabRequestedEventArgs e)
    {
        TraceFocusSnapshot("tab-selected", e.PanelId, IntPtr.Zero, $"tabId={e.TabId:N}");
        var panel = layoutState.FindPanel(e.PanelId);
        var updatedPanel = panel with { ActiveTabId = e.TabId };
        var nextState = layoutState with
        {
            ActivePanelId = e.PanelId,
            Panels = layoutState.Panels.Select(item => item.Id == e.PanelId ? updatedPanel : item).ToList()
        };
        ApplyState(nextState, null);
    }

    private void OnPanelFocusRequested(object? sender, PanelFocusRequestedEventArgs e)
    {
        TraceFocusSnapshot("panel-focus-requested", e.PanelId, IntPtr.Zero, null);
        ActivatePanel(e.PanelId);
    }

    private void ActivatePanel(Guid panelId)
    {
        TraceFocusSnapshot("activate-panel", panelId, IntPtr.Zero, null);
        var nextState = layoutService.ActivatePanel(layoutState, panelId);
        if (ReferenceEquals(nextState, layoutState))
        {
            return;
        }

        ApplyState(nextState, null);
    }

    private void OnTabDragStarted(object? sender, TabDragStartedEventArgs e)
    {
        activeDragPayload = e.Payload;
        SetShellHostsVisible(false);
        DockPreviewOverlay.BeginDragCapture();
    }

    private void OnTabDragOverPanel(object? sender, TabDragPointerEventArgs e)
    {
        activeDropPreview = CreateDropPreview(e.Payload, e.TargetElement.TranslatePoint(e.PointerInPanel, WorkspaceRoot));
        ShowOrClearActivePreview();
    }

    private void OnTabDroppedOnPanel(object? sender, TabDragPointerEventArgs e)
    {
        var workspacePointer = e.TargetElement.TranslatePoint(e.PointerInPanel, WorkspaceRoot);
        CommitDrop(e.Payload, CreateDropPreview(e.Payload, workspacePointer));
    }

    private void OnWorkspaceDragOver(object? sender, DockOverlayDragEventArgs e)
    {
        activeDragPayload = e.Payload;
        var workspacePointer = DockPreviewOverlay.TranslatePoint(e.PointerInOverlay, WorkspaceRoot);
        activeDropPreview = CreateDropPreview(e.Payload, workspacePointer);
        ShowOrClearActivePreview();
    }

    private void OnWorkspaceDrop(object? sender, DockOverlayDragEventArgs e)
    {
        var workspacePointer = DockPreviewOverlay.TranslatePoint(e.PointerInOverlay, WorkspaceRoot);
        CommitDrop(e.Payload, CreateDropPreview(e.Payload, workspacePointer));
    }

    private void OnWorkspaceDragLeave(object? sender, EventArgs e)
    {
        activeDropPreview = null;
        DockPreviewOverlay.ClearPreview();
    }

    private void OnTabDragCompleted(object? sender, EventArgs e)
    {
        activeDragPayload = null;
        activeDropPreview = null;
        DockPreviewOverlay.EndDragCapture();
        SetShellHostsVisible(true);
    }

    private void CommitDrop(DockDragPayload payload, DockDropPreview? preview)
    {
        DockPreviewOverlay.EndDragCapture();
        SetShellHostsVisible(true);
        activeDragPayload = null;
        activeDropPreview = null;

        if (preview is null)
        {
            SetStatus("No dock target under pointer.");
            return;
        }

        var result = layoutService.CommitDrop(layoutState, payload, preview);
        if (!result.Accepted)
        {
            SetStatus(result.Reason ?? "Docking was not accepted.");
            return;
        }

        ApplyLayout(result.State, "Docked tab.");
    }

    private DockDropPreview? CreateDropPreview(DockDragPayload payload, Point workspacePointer)
    {
        var pointer = new DockPoint(workspacePointer.X, workspacePointer.Y);
        var target = dropTargetResolver.Resolve(CreateDropTargets(), pointer);
        if (target is null)
        {
            return null;
        }

        return dropHitTester.HitTest(
            target.Bounds,
            pointer,
            target.PanelId,
            payload,
            new DockDropOptions(CurrentVisiblePanelCount: layoutState.VisiblePanels.Count));
    }

    private IEnumerable<DockDropTarget> CreateDropTargets()
    {
        foreach (var (panelId, control) in panelControlsById)
        {
            if (control.ActualWidth <= 0 || control.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = control.TranslatePoint(new Point(0, 0), WorkspaceRoot);
            yield return new DockDropTarget(
                panelId,
                new DockRect(topLeft.X, topLeft.Y, control.ActualWidth, control.ActualHeight));
        }
    }

    private void ShowOrClearActivePreview()
    {
        if (activeDropPreview is null)
        {
            DockPreviewOverlay.ClearPreview();
            return;
        }

        DockPreviewOverlay.ShowPreview(activeDropPreview);
    }

    private void SetShellHostsVisible(bool isVisible)
    {
        foreach (var panel in panelControlsById.Values)
        {
            panel.SetShellHostVisible(isVisible);
        }
    }

    private void ApplySettingsToUi()
    {
        isApplyingSettingsToUi = true;
        try
        {
            ApplyTheme(settings.Theme);
            DarkModeToggle.IsChecked = settings.Theme == UiTheme.Dark;
        }
        finally
        {
            isApplyingSettingsToUi = false;
        }
    }

    private void SaveSettings()
    {
        settings = settings with
        {
            Theme = ResolveSelectedTheme()
        };

        try
        {
            settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save settings: {ex.Message}");
        }
    }

    private void ApplyLayout(DockLayoutState nextState, string status)
    {
        ApplyState(nextState, status);
    }

    private void ApplyState(DockLayoutState nextState, string? status)
    {
        var previousState = layoutState;
        layoutState = nextState;
        SaveLayout();

        if (DockLayoutRenderInvalidation.RequiresLayoutRender(previousState, nextState))
        {
            RenderLayout();
        }
        else
        {
            RefreshVisiblePanelControls();
        }

        UpdateSearchBoxFromActiveTab();

        if (status is not null)
        {
            SetStatus(status);
        }
    }

    private void UpdateSearchBoxFromActiveTab()
    {
        var text = ActiveOrFirstVisiblePanelId() is { } panelId
            ? layoutState.FindPanel(panelId).ActiveTab is { LocationKind: TabLocationKind.Search } tab
                ? tab.SearchQuery ?? string.Empty
                : string.Empty
            : string.Empty;

        if (string.Equals(QuickSearchBox.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        isUpdatingSearchBox = true;
        try
        {
            QuickSearchBox.Text = text;
        }
        finally
        {
            isUpdatingSearchBox = false;
        }
    }

    private void RefreshVisiblePanelControls()
    {
        foreach (var panel in layoutState.VisiblePanels)
        {
            if (panelControlsById.TryGetValue(panel.Id, out var control))
            {
                control.UpdatePanel(panel, layoutState.ActivePanelId == panel.Id);
            }
        }
    }

    private void SaveLayout()
    {
        try
        {
            layoutStore.Save(layoutState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save layout: {ex.Message}");
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private UiTheme ResolveSelectedTheme()
    {
        return DarkModeToggle.IsChecked == true ? UiTheme.Dark : UiTheme.Light;
    }

    private void ApplyTheme(UiTheme theme)
    {
        AppThemeResources.Apply(Application.Current.Resources, theme);
        windowNativeThemeApplier.Apply(new WindowInteropHelper(this).Handle, theme);
        foreach (var panelControl in panelControlsById.Values)
        {
            panelControl.ApplyTheme(theme);
        }
    }

    private static void OpenFolderInExplorer(string path)
    {
        var target = FolderOpenTarget.ForPath(path);
        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            UseShellExecute = false
        };

        foreach (var argument in target.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
    }

    private static IReadOnlyList<RowDefinition> CreateRows(DockLayoutKind layoutKind)
    {
        return layoutKind switch
        {
            DockLayoutKind.TwoByOne or DockLayoutKind.ThreePanelPrimaryLeft or DockLayoutKind.TwoByTwo =>
            [
                new RowDefinition(),
                new RowDefinition()
            ],
            _ =>
            [
                new RowDefinition()
            ]
        };
    }

    private static IReadOnlyList<ColumnDefinition> CreateColumns(DockLayoutKind layoutKind)
    {
        return layoutKind switch
        {
            DockLayoutKind.OneByTwo or DockLayoutKind.ThreePanelPrimaryLeft or DockLayoutKind.TwoByTwo =>
            [
                new ColumnDefinition(),
                new ColumnDefinition()
            ],
            _ =>
            [
                new ColumnDefinition()
            ]
        };
    }

    private static GridPlacement GetPlacement(DockLayoutKind layoutKind, int index)
    {
        return layoutKind switch
        {
            DockLayoutKind.OneByTwo => new GridPlacement(0, index, 1, 1),
            DockLayoutKind.TwoByOne => new GridPlacement(index, 0, 1, 1),
            DockLayoutKind.ThreePanelPrimaryLeft when index == 0 => new GridPlacement(0, 0, 2, 1),
            DockLayoutKind.ThreePanelPrimaryLeft when index == 1 => new GridPlacement(0, 1, 1, 1),
            DockLayoutKind.ThreePanelPrimaryLeft => new GridPlacement(1, 1, 1, 1),
            DockLayoutKind.TwoByTwo => new GridPlacement(index / 2, index % 2, 1, 1),
            _ => new GridPlacement(0, 0, 1, 1)
        };
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    private sealed record GridPlacement(int Row, int Column, int RowSpan, int ColumnSpan);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
