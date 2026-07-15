using Waffle.Browse.Core.Navigation;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.Core.Docking;

public sealed class DockLayoutService
{
    public const int MaxPanels = 4;

    public DockLayoutState CreateDefault(string initialPath)
    {
        var panels = Enumerable.Range(1, MaxPanels)
            .Select(index => CreatePanel(index, index == 1, initialPath))
            .ToList();

        return new DockLayoutState
        {
            LayoutKind = DockLayoutKind.OneByOne,
            ActivePanelId = panels[0].Id,
            Grid = DockGridState.Single(panels[0].Id),
            Panels = panels
        };
    }

    public DockLayoutState SetVisiblePanelCount(DockLayoutState state, int count)
    {
        return SetLayout(state, count switch
        {
            0 => DockLayoutKind.Empty,
            1 => DockLayoutKind.OneByOne,
            2 => DockLayoutKind.OneByTwo,
            3 => DockLayoutKind.ThreePanelPrimaryLeft,
            4 => DockLayoutKind.TwoByTwo,
            _ => throw new ArgumentOutOfRangeException(nameof(count), "Panel count must be between 0 and 4.")
        });
    }

    public DockLayoutState SetLayout(DockLayoutState state, DockLayoutKind layoutKind)
    {
        var visibleCount = VisibleCountFor(layoutKind);
        var fallbackPath = FindFallbackPath(state);
        var panels = EnsurePanelSlots(state, fallbackPath)
            .Select((panel, index) => EnsurePanelHasTab(panel, fallbackPath) with { IsVisible = index < visibleCount })
            .ToList();
        var activePanelId = panels.Any(panel => panel.IsVisible && panel.Id == state.ActivePanelId)
            ? state.ActivePanelId
            : panels.FirstOrDefault(panel => panel.IsVisible)?.Id;
        var visiblePanels = panels.Where(panel => panel.IsVisible).ToList();
        var grid = visiblePanels.Count == 0
            ? null
            : CreatePresetGrid(visiblePanels, layoutKind);

        return state with
        {
            LayoutKind = layoutKind,
            ActivePanelId = activePanelId,
            Grid = grid,
            Panels = panels
        };
    }

    public DockLayoutState ActivatePanel(DockLayoutState state, Guid panelId)
    {
        var panel = state.FindPanel(panelId);
        return state.ActivePanelId == panel.Id
            ? state
            : state with { ActivePanelId = panel.Id };
    }

    public DockLayoutState NavigateTo(DockLayoutState state, Guid panelId, string path)
    {
        var panel = state.FindPanel(panelId);
        var tab = panel.ActiveTab ?? CreateTab(path);
        var addHistory = !string.IsNullOrWhiteSpace(tab.CurrentPath)
            && !PathEquals(tab.CurrentPath, path);
        var updatedTab = tab with
        {
            CurrentPath = path,
            Title = CreateTitle(path),
            LocationKind = TabLocationKind.Folder,
            SearchQuery = null,
            SearchOriginPath = null,
            SearchScope = SearchScope.GlobalIndex,
            SearchRoots = [],
            BackStack = addHistory ? [.. tab.BackStack, tab.CurrentPath] : [.. tab.BackStack],
            ForwardStack = []
        };
        var updatedPanel = ReplaceTab(panel, updatedTab) with { ActiveTabId = updatedTab.Id };

        return ReplacePanel(state, updatedPanel) with { ActivePanelId = panelId };
    }

    public DockLayoutState NavigateToSearch(
        DockLayoutState state,
        Guid panelId,
        SearchQuery searchQuery)
    {
        var query = searchQuery.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query is required.", nameof(searchQuery));
        }

        var panel = state.FindPanel(panelId);
        var tab = panel.ActiveTab ?? CreateTab(searchQuery.RootPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var originPath = tab.LocationKind == TabLocationKind.Search && !string.IsNullOrWhiteSpace(tab.SearchOriginPath)
            ? tab.SearchOriginPath
            : tab.CurrentPath;
        if (string.IsNullOrWhiteSpace(originPath))
        {
            originPath = searchQuery.RootPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var rootPath = searchQuery.Scope == SearchScope.CurrentFolder
            ? searchQuery.RootPath ?? originPath
            : null;
        var searchTarget = EverythingSearchLocation.Build(query, searchQuery.Scope, rootPath);
        var backStack = tab.BackStack.ToList();
        if (tab.LocationKind != TabLocationKind.Search && ShouldPushHistory(backStack, originPath))
        {
            backStack.Add(originPath);
        }

        var updatedTab = tab with
        {
            CurrentPath = searchTarget,
            Title = CreateSearchTitle(query),
            LocationKind = TabLocationKind.Search,
            SearchQuery = query,
            SearchOriginPath = originPath,
            SearchScope = searchQuery.Scope,
            SearchRoots = rootPath is null ? [] : [rootPath],
            BackStack = backStack,
            ForwardStack = []
        };

        return ReplacePanel(state, ReplaceTab(panel, updatedTab) with { ActiveTabId = updatedTab.Id })
            with { ActivePanelId = panelId };
    }

    public DockLayoutState ClearSearch(DockLayoutState state, Guid panelId)
    {
        var panel = state.FindPanel(panelId);
        var tab = panel.ActiveTab;
        if (tab is null || tab.LocationKind != TabLocationKind.Search || string.IsNullOrWhiteSpace(tab.SearchOriginPath))
        {
            return state;
        }

        var originPath = tab.SearchOriginPath;
        var backStack = tab.BackStack.Count > 0 && PathEquals(tab.BackStack[^1], originPath)
            ? tab.BackStack.Take(tab.BackStack.Count - 1).ToList()
            : [.. tab.BackStack];
        var updatedTab = tab with
        {
            CurrentPath = originPath,
            Title = CreateTitle(originPath),
            LocationKind = TabLocationKind.Folder,
            SearchQuery = null,
            SearchOriginPath = null,
            SearchScope = SearchScope.GlobalIndex,
            SearchRoots = [],
            BackStack = backStack,
            ForwardStack = []
        };

        return ReplacePanel(state, ReplaceTab(panel, updatedTab) with { ActiveTabId = updatedTab.Id })
            with { ActivePanelId = panelId };
    }

    public DockLayoutState NavigateBack(DockLayoutState state, Guid panelId)
    {
        var panel = state.FindPanel(panelId);
        var tab = panel.ActiveTab;
        if (tab is null || tab.BackStack.Count == 0)
        {
            return state;
        }

        var previousPath = tab.BackStack[^1];
        var updatedTab = tab with
        {
            CurrentPath = previousPath,
            Title = CreateTitle(previousPath),
            LocationKind = TabLocationKind.Folder,
            SearchQuery = null,
            SearchOriginPath = null,
            SearchScope = SearchScope.GlobalIndex,
            SearchRoots = [],
            BackStack = tab.BackStack.Take(tab.BackStack.Count - 1).ToList(),
            ForwardStack = [tab.CurrentPath, .. tab.ForwardStack]
        };

        return ReplacePanel(state, ReplaceTab(panel, updatedTab) with { ActiveTabId = updatedTab.Id }) with { ActivePanelId = panelId };
    }

    public DockLayoutState NavigateForward(DockLayoutState state, Guid panelId)
    {
        var panel = state.FindPanel(panelId);
        var tab = panel.ActiveTab;
        if (tab is null || tab.ForwardStack.Count == 0)
        {
            return state;
        }

        var nextPath = tab.ForwardStack[0];
        if (EverythingSearchLocation.TryParse(nextPath, out var searchQuery))
        {
            var originPath = searchQuery.RootPath ?? tab.CurrentPath;
            var updatedSearchTab = tab with
            {
                CurrentPath = nextPath,
                Title = CreateSearchTitle(searchQuery.Text),
                LocationKind = TabLocationKind.Search,
                SearchQuery = searchQuery.Text,
                SearchOriginPath = originPath,
                SearchScope = searchQuery.Scope,
                SearchRoots = searchQuery.RootPath is null ? [] : [searchQuery.RootPath],
                BackStack = [.. tab.BackStack, tab.CurrentPath],
                ForwardStack = tab.ForwardStack.Skip(1).ToList()
            };

            return ReplacePanel(state, ReplaceTab(panel, updatedSearchTab) with { ActiveTabId = updatedSearchTab.Id })
                with { ActivePanelId = panelId };
        }

        var updatedTab = tab with
        {
            CurrentPath = nextPath,
            Title = CreateTitle(nextPath),
            LocationKind = TabLocationKind.Folder,
            SearchQuery = null,
            SearchOriginPath = null,
            SearchScope = SearchScope.GlobalIndex,
            SearchRoots = [],
            BackStack = [.. tab.BackStack, tab.CurrentPath],
            ForwardStack = tab.ForwardStack.Skip(1).ToList()
        };

        return ReplacePanel(state, ReplaceTab(panel, updatedTab) with { ActiveTabId = updatedTab.Id }) with { ActivePanelId = panelId };
    }

    public DockLayoutState NavigateUp(DockLayoutState state, Guid panelId)
    {
        var panel = state.FindPanel(panelId);
        var currentPath = panel.ActiveTab?.CurrentPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return state;
        }

        if (ShellFolderPaths.IsThisPc(currentPath))
        {
            return state;
        }

        if (IsRootPath(currentPath))
        {
            return NavigateTo(state, panelId, ShellFolderPaths.ThisPc);
        }

        var trimmed = currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrWhiteSpace(parent) ? state : NavigateTo(state, panelId, parent);
    }

    public DockLayoutState AddTab(DockLayoutState state, Guid panelId, string path)
    {
        var panel = state.FindPanel(panelId);
        var tab = CreateTab(path);
        var updatedPanel = panel with
        {
            Tabs = [.. panel.Tabs, tab],
            ActiveTabId = tab.Id
        };

        return ReplacePanel(state, updatedPanel) with { ActivePanelId = panelId };
    }

    public DockLayoutState CloseTab(DockLayoutState state, Guid panelId, Guid tabId)
    {
        var panel = state.FindPanel(panelId);
        var tabs = panel.Tabs.Where(tab => tab.Id != tabId).ToList();

        var activeTabId = panel.ActiveTabId == tabId
            ? tabs.LastOrDefault()?.Id
            : panel.ActiveTabId;
        var updatedPanel = panel with
        {
            Tabs = tabs,
            ActiveTabId = activeTabId,
            IsVisible = tabs.Count > 0 && panel.IsVisible
        };
        var updatedState = ReplacePanel(state, updatedPanel);
        if (updatedPanel.IsVisible)
        {
            return updatedState with { ActivePanelId = panelId };
        }

        updatedState = updatedState with
        {
            Grid = state.Grid is null ? null : new DockGridService().RemoveLeaf(state.Grid, panelId)
        };

        return NormalizeVisibleLayout(updatedState);
    }

    public DockLayoutState MoveTabWithinPanel(DockLayoutState state, Guid panelId, Guid tabId, int targetIndex)
    {
        var panel = state.FindPanel(panelId);
        var tab = panel.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab is null)
        {
            return state;
        }

        var tabs = panel.Tabs.Where(item => item.Id != tabId).ToList();
        var boundedIndex = Math.Clamp(targetIndex, 0, tabs.Count);
        tabs.Insert(boundedIndex, tab);

        return ReplacePanel(state, panel with { Tabs = tabs, ActiveTabId = tabId }) with { ActivePanelId = panelId };
    }

    public DockOperationResult DockTab(DockLayoutState state, Guid sourcePanelId, Guid tabId, Guid targetPanelId, DockDirection direction)
    {
        var operation = direction == DockDirection.Center
            ? DockDropOperation.MoveIntoPanel
            : DockDropOperation.SplitPanel;
        var accepted = direction == DockDirection.Center || state.VisiblePanels.Count < MaxPanels;
        var preview = new DockDropPreview(
            operation,
            targetPanelId,
            direction == DockDirection.Center ? null : direction,
            new DockRect(0, 0, 1, 1),
            accepted,
            accepted ? null : "The layout already has four visible panels.");

        return CommitDrop(state, new DockDragPayload(sourcePanelId, tabId), preview);
    }

    public DockOperationResult CommitDrop(DockLayoutState state, DockDragPayload payload, DockDropPreview preview)
    {
        if (!preview.Accepted)
        {
            return new DockOperationResult(false, state, preview.RejectionReason);
        }

        return preview.Operation switch
        {
            DockDropOperation.MoveIntoPanel => MoveTabToPanel(state, payload.SourcePanelId, payload.TabId, preview.TargetPanelId),
            DockDropOperation.SplitPanel when preview.SplitDirection is { } direction =>
                SplitPanelWithTab(state, payload.SourcePanelId, payload.TabId, preview.TargetPanelId, direction),
            DockDropOperation.ReorderTab => new DockOperationResult(false, state, "Tab reorder commit requires a target index."),
            _ => new DockOperationResult(false, state, "No drop operation is available.")
        };
    }

    internal static string CreateTitle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Home";
        }

        if (ShellFolderPaths.IsThisPc(path))
        {
            return ShellFolderPaths.ThisPcDisplayName;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    internal static string CreateSearchTitle(string queryText)
    {
        return $"검색: {queryText.Trim()}";
    }

    internal static TabState CreateTab(string path)
    {
        return new TabState
        {
            Title = CreateTitle(path),
            CurrentPath = path,
            LocationKind = TabLocationKind.Folder
        };
    }

    internal DockLayoutState Normalize(DockLayoutState state, string fallbackPath)
    {
        var layoutKind = Enum.IsDefined(state.LayoutKind) ? state.LayoutKind : DockLayoutKind.OneByOne;
        var visibleCount = VisibleCountFor(layoutKind);
        var panels = EnsurePanelSlots(state, fallbackPath)
            .Select((panel, index) =>
            {
                var ensured = EnsurePanelHasTab(panel, fallbackPath);
                var activeTabId = ensured.Tabs.Any(tab => tab.Id == ensured.ActiveTabId)
                    ? ensured.ActiveTabId
                    : ensured.Tabs[0].Id;

                return ensured with
                {
                    IsVisible = index < visibleCount,
                    ActiveTabId = activeTabId
                };
            })
            .ToList();
        var activePanelId = panels.Any(panel => panel.IsVisible && panel.Id == state.ActivePanelId)
            ? state.ActivePanelId
            : panels.FirstOrDefault(panel => panel.IsVisible)?.Id;
        var visiblePanels = panels.Where(panel => panel.IsVisible).ToList();
        var grid = visiblePanels.Count == 0
            ? null
            : state.Grid is not null && GridMatchesVisiblePanels(state.Grid, visiblePanels)
            ? state.Grid
            : CreatePresetGrid(visiblePanels, layoutKind);
        layoutKind = grid is null ? DockLayoutKind.Empty : new DockGridService().GetLayoutKind(grid);

        return state with
        {
            LayoutKind = layoutKind,
            ActivePanelId = activePanelId,
            Grid = grid,
            Panels = panels
        };
    }

    private static DockOperationResult MoveTabToPanel(DockLayoutState state, Guid sourcePanelId, Guid tabId, Guid targetPanelId)
    {
        if (sourcePanelId == targetPanelId)
        {
            return new DockOperationResult(true, state with { ActivePanelId = targetPanelId });
        }

        var sourcePanel = state.FindPanel(sourcePanelId);
        var targetPanel = state.FindPanel(targetPanelId);
        var movingTab = sourcePanel.Tabs.FirstOrDefault(tab => tab.Id == tabId);
        if (movingTab is null)
        {
            return new DockOperationResult(false, state, "The source tab does not exist.");
        }

        var withoutTab = RemoveTabFromPanel(state, sourcePanelId, tabId, keepReplacementTab: false);
        withoutTab = HidePanelIfEmpty(withoutTab, sourcePanelId);
        var currentTarget = withoutTab.FindPanel(targetPanel.Id);
        var updatedTarget = currentTarget with
        {
            Tabs = [.. currentTarget.Tabs, movingTab],
            ActiveTabId = movingTab.Id
        };

        var withMovedTab = ReplacePanel(withoutTab, updatedTarget);
        return new DockOperationResult(true, NormalizeVisibleLayout(withMovedTab with { ActivePanelId = targetPanelId }));
    }

    private static DockOperationResult SplitPanelWithTab(
        DockLayoutState state,
        Guid sourcePanelId,
        Guid tabId,
        Guid targetPanelId,
        DockDirection direction)
    {
        if (state.VisiblePanels.Count >= MaxPanels)
        {
            return new DockOperationResult(false, state, "The layout already has four visible panels.");
        }

        var sourcePanel = state.FindPanel(sourcePanelId);
        var movingTab = sourcePanel.Tabs.FirstOrDefault(tab => tab.Id == tabId);
        if (movingTab is null)
        {
            return new DockOperationResult(false, state, "The source tab does not exist.");
        }

        if (sourcePanelId == targetPanelId && sourcePanel.Tabs.Count == 1)
        {
            return new DockOperationResult(false, state, "Cannot split the only tab in a panel into itself.");
        }

        var destinationPanel = state.Panels.FirstOrDefault(panel => !panel.IsVisible);
        if (destinationPanel is null)
        {
            return new DockOperationResult(false, state, "No hidden panel is available.");
        }

        var withoutTab = RemoveTabFromPanel(state, sourcePanelId, tabId, keepReplacementTab: true);
        var updatedDestination = destinationPanel with
        {
            IsVisible = true,
            Tabs = [movingTab],
            ActiveTabId = movingTab.Id
        };
        var withDestination = ReplacePanel(withoutTab, updatedDestination);
        DockGridState grid;
        try
        {
            grid = new DockGridService().Split(EnsureGrid(state), targetPanelId, updatedDestination.Id, direction);
        }
        catch (InvalidOperationException ex) when (ex.Message == "The target panel does not exist in the layout grid.")
        {
            grid = CreatePresetGrid(withDestination.VisiblePanels, LayoutAfterDock(state.VisiblePanels.Count + 1, direction));
        }
        var layoutKind = new DockGridService().GetLayoutKind(grid);

        return new DockOperationResult(true, withDestination with
        {
            Grid = grid,
            LayoutKind = layoutKind,
            ActivePanelId = updatedDestination.Id
        });
    }

    private static DockLayoutState RemoveTabFromPanel(DockLayoutState state, Guid panelId, Guid tabId, bool keepReplacementTab)
    {
        var panel = state.FindPanel(panelId);
        var tabs = panel.Tabs.Where(tab => tab.Id != tabId).ToList();
        if (tabs.Count == 0 && keepReplacementTab)
        {
            tabs.Add(CreateTab(FindFallbackPath(state, tabId)));
        }

        var activeTabId = panel.ActiveTabId == tabId
            ? tabs.FirstOrDefault()?.Id
            : panel.ActiveTabId;

        return ReplacePanel(state, panel with { Tabs = tabs, ActiveTabId = activeTabId });
    }

    private static DockLayoutState HidePanelIfEmpty(DockLayoutState state, Guid panelId)
    {
        var panel = state.FindPanel(panelId);
        if (panel.Tabs.Count > 0)
        {
            return state;
        }

        return ReplacePanel(state, panel with
        {
            IsVisible = false,
            ActiveTabId = null
        }) with
        {
            Grid = state.Grid is null ? null : new DockGridService().RemoveLeaf(state.Grid, panelId)
        };
    }

    private static PanelState CreatePanel(int index, bool isVisible, string initialPath)
    {
        var tab = CreateTab(initialPath);
        return new PanelState
        {
            IsVisible = isVisible,
            Tabs = [tab],
            ActiveTabId = tab.Id
        };
    }

    private static PanelState EnsurePanelHasTab(PanelState panel, string fallbackPath)
    {
        if (panel.Tabs.Count > 0)
        {
            var activeTabId = panel.Tabs.Any(tab => tab.Id == panel.ActiveTabId)
                ? panel.ActiveTabId
                : panel.Tabs[0].Id;
            return panel with { ActiveTabId = activeTabId };
        }

        var tab = CreateTab(fallbackPath);
        return panel with
        {
            Tabs = [tab],
            ActiveTabId = tab.Id
        };
    }

    private static List<PanelState> EnsurePanelSlots(DockLayoutState state, string fallbackPath)
    {
        var panels = state.Panels.ToList();
        for (var index = panels.Count + 1; index <= MaxPanels; index++)
        {
            panels.Add(CreatePanel(index, false, fallbackPath));
        }

        return panels.Take(MaxPanels).ToList();
    }

    private static PanelState ReplaceTab(PanelState panel, TabState tab)
    {
        var tabs = panel.Tabs.Any(item => item.Id == tab.Id)
            ? panel.Tabs.Select(item => item.Id == tab.Id ? tab : item).ToList()
            : [.. panel.Tabs, tab];

        return panel with { Tabs = tabs };
    }

    private static DockLayoutState ReplacePanel(DockLayoutState state, PanelState panel)
    {
        return state with
        {
            Panels = state.Panels.Select(item => item.Id == panel.Id ? panel : item).ToList()
        };
    }

    private static int VisibleCountFor(DockLayoutKind layoutKind)
    {
        return layoutKind switch
        {
            DockLayoutKind.Empty => 0,
            DockLayoutKind.OneByOne => 1,
            DockLayoutKind.OneByTwo => 2,
            DockLayoutKind.TwoByOne => 2,
            DockLayoutKind.ThreePanelPrimaryLeft => 3,
            DockLayoutKind.TwoByTwo => 4,
            _ => 1
        };
    }

    private static DockLayoutKind LayoutAfterDock(int visibleCount, DockDirection direction)
    {
        return visibleCount switch
        {
            1 => DockLayoutKind.OneByOne,
            2 when direction is DockDirection.Top or DockDirection.Bottom => DockLayoutKind.TwoByOne,
            2 => DockLayoutKind.OneByTwo,
            3 => DockLayoutKind.ThreePanelPrimaryLeft,
            _ => DockLayoutKind.TwoByTwo
        };
    }

    private static DockGridState EnsureGrid(DockLayoutState state)
    {
        if (state.VisiblePanels.Count == 0)
        {
            throw new InvalidOperationException("The layout does not contain a visible panel.");
        }

        return state.Grid is not null && GridMatchesVisiblePanels(state.Grid, state.VisiblePanels)
            ? state.Grid
            : CreatePresetGrid(state.VisiblePanels, state.LayoutKind);
    }

    private static DockGridState CreatePresetGrid(IReadOnlyList<PanelState> visiblePanels, DockLayoutKind layoutKind)
    {
        if (visiblePanels.Count == 0)
        {
            throw new InvalidOperationException("The layout must contain at least one visible panel.");
        }

        var gridService = new DockGridService();
        var grid = DockGridState.Single(visiblePanels[0].Id);

        if (visiblePanels.Count >= 4 && layoutKind == DockLayoutKind.TwoByTwo)
        {
            grid = gridService.Split(grid, visiblePanels[0].Id, visiblePanels[2].Id, DockDirection.Bottom);
            grid = gridService.Split(grid, visiblePanels[0].Id, visiblePanels[1].Id, DockDirection.Right);
            return gridService.Split(grid, visiblePanels[2].Id, visiblePanels[3].Id, DockDirection.Right);
        }

        if (visiblePanels.Count >= 2)
        {
            var direction = layoutKind == DockLayoutKind.TwoByOne
                ? DockDirection.Bottom
                : DockDirection.Right;
            grid = gridService.Split(grid, visiblePanels[0].Id, visiblePanels[1].Id, direction);
        }

        if (visiblePanels.Count >= 3)
        {
            grid = gridService.Split(grid, visiblePanels[1].Id, visiblePanels[2].Id, DockDirection.Bottom);
        }

        if (visiblePanels.Count >= 4)
        {
            grid = gridService.Split(grid, visiblePanels[2].Id, visiblePanels[3].Id, DockDirection.Right);
        }

        return grid;
    }

    private static bool GridMatchesVisiblePanels(DockGridState grid, IReadOnlyList<PanelState> visiblePanels)
    {
        var gridPanelIds = new DockGridService().GetLeafPanelIds(grid);
        return gridPanelIds.Count == visiblePanels.Count
            && gridPanelIds.All(panelId => visiblePanels.Any(panel => panel.Id == panelId));
    }

    private static DockLayoutState NormalizeVisibleLayout(DockLayoutState state)
    {
        if (state.VisiblePanels.Count == 0)
        {
            return state with
            {
                LayoutKind = DockLayoutKind.Empty,
                ActivePanelId = null,
                Grid = null
            };
        }

        var grid = EnsureGrid(state);
        var layoutKind = new DockGridService().GetLayoutKind(grid);
        var visiblePanels = state.VisiblePanels;
        var activePanelId = visiblePanels.Any(panel => panel.Id == state.ActivePanelId)
            ? state.ActivePanelId
            : visiblePanels.FirstOrDefault()?.Id;

        return state with
        {
            LayoutKind = layoutKind,
            ActivePanelId = activePanelId,
            Grid = grid
        };
    }

    private static string FindFallbackPath(DockLayoutState state, Guid? excludedTabId = null)
    {
        return state.Panels
                   .SelectMany(panel => panel.Tabs)
                   .Select(tab => tab.Id == excludedTabId ? null : FindFallbackCandidate(tab))
                   .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? FindFallbackCandidate(TabState tab)
    {
        return tab.LocationKind == TabLocationKind.Search && !string.IsNullOrWhiteSpace(tab.SearchOriginPath)
            ? tab.SearchOriginPath
            : tab.CurrentPath;
    }

    private static bool ShouldPushHistory(IReadOnlyList<string> backStack, string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && (backStack.Count == 0 || !PathEquals(backStack[^1], path));
    }

    private static bool IsRootPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return PathEquals(path, root);
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
