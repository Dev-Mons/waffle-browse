using Waffle.Browse.Core.Docking;
using Waffle.Browse.Core.Persistence;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.Core.Tests.Persistence;

internal static class DockLayoutStoreTests
{
    public static void DockLayoutStoreRoundTripsState()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"), "layout.json");
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 4);
        var panels = state.VisiblePanels.ToList();

        state = service.NavigateTo(state, panels[0].Id, @"C:\One");
        state = service.NavigateTo(state, panels[1].Id, @"C:\Two");
        state = service.NavigateTo(state, panels[2].Id, @"C:\Three");
        state = service.NavigateTo(state, panels[3].Id, @"C:\Four");

        var store = new DockLayoutStore(tempFile);
        store.Save(state);
        var loaded = store.LoadOrDefault(@"C:\", _ => true);

        TestAssert.Equal(DockLayoutKind.TwoByTwo, loaded.LayoutKind, "Layout kind should round-trip");
        TestAssert.Equal(4, loaded.VisiblePanels.Count, "Visible panel count should round-trip");
        TestAssert.Equal(@"C:\One", loaded.FindPanel(panels[0].Id).ActiveTab?.CurrentPath, "First panel path should round-trip");
        TestAssert.Equal(@"C:\Four", loaded.FindPanel(panels[3].Id).ActiveTab?.CurrentPath, "Fourth panel path should round-trip");
    }

    public static void DockLayoutStorePreservesEmptyLayout()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"), "layout.json");
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var panel = state.VisiblePanels[0];
        state = service.CloseTab(state, panel.Id, state.FindPanel(panel.Id).ActiveTab!.Id);

        var store = new DockLayoutStore(tempFile);
        store.Save(state);
        var loaded = store.LoadOrDefault(@"C:\", _ => true);

        TestAssert.Equal(DockLayoutKind.Empty, loaded.LayoutKind, "Empty layout kind should round-trip");
        TestAssert.Equal(0, loaded.VisiblePanels.Count, "Empty layout should restore with no visible panels");
        TestAssert.Equal(null, loaded.ActivePanelId, "Empty layout should restore without an active panel");
        TestAssert.Equal(null, loaded.Grid, "Empty layout should restore without a grid root");
    }

    public static void RestoreNormalizationFallsBackForUnavailablePaths()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\Valid"), 2);
        var firstPanel = state.VisiblePanels[0];
        var secondPanel = state.VisiblePanels[1];

        state = service.NavigateTo(state, firstPanel.Id, @"C:\Valid");
        state = service.NavigateTo(state, secondPanel.Id, @"Z:\Missing");

        var normalized = DockLayoutStore.NormalizeForRestore(state, @"C:\Fallback", path => path == @"C:\Valid");

        TestAssert.Equal(@"C:\Valid", normalized.FindPanel(firstPanel.Id).ActiveTab?.CurrentPath, "Available path should be kept");
        TestAssert.Equal(@"C:\Fallback", normalized.FindPanel(secondPanel.Id).ActiveTab?.CurrentPath, "Unavailable path should fall back");
        TestAssert.Equal(2, normalized.VisiblePanels.Count, "Normalization should not change the visible layout");
    }

    public static void RestoreCreatesGridWhenSavedStateHasOnlyVisiblePanels()
    {
        var service = new DockLayoutService();
        var legacy = service.SetVisiblePanelCount(service.CreateDefault(@"C:\Valid"), 2) with
        {
            Grid = null
        };

        var normalized = DockLayoutStore.NormalizeForRestore(legacy, @"C:\Fallback", path => true);

        TestAssert.NotNull(normalized.Grid, "Restore should create grid for legacy layout state");
        TestAssert.Equal(2, normalized.VisiblePanels.Count, "Visible panel count should survive migration");
        TestAssert.Equal(DockLayoutKind.OneByTwo, normalized.LayoutKind, "Migrated two-panel layout should preserve layout kind");
    }

    public static void RestoreNormalizationRebuildsSearchTabs()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\Valid");
        var panel = state.VisiblePanels[0];

        state = service.NavigateToSearch(
            state,
            panel.Id,
            new SearchQuery("report", SearchScope.CurrentFolder, 1000, @"C:\Valid"));
        var searchTab = state.FindPanel(panel.Id).ActiveTab!;
        state = state with
        {
            Panels = state.Panels.Select(item => item.Id == panel.Id
                ? item with { Tabs = item.Tabs.Select(tab => tab.Id == searchTab.Id ? tab with { CurrentPath = "search-ms:legacy" } : tab).ToList() }
                : item).ToList()
        };

        var normalized = DockLayoutStore.NormalizeForRestore(state, @"C:\Fallback", path => path == @"C:\Valid");
        var tab = normalized.FindPanel(panel.Id).ActiveTab!;

        TestAssert.Equal(TabLocationKind.Search, tab.LocationKind, "Valid search tabs should remain search tabs after restore");
        TestAssert.Equal(@"waffle-search:?query=report&scope=CurrentFolder&root=C%3A%5CValid", tab.CurrentPath, "Restore should migrate legacy search state to Waffle search");
        TestAssert.Equal(@"C:\Valid", tab.SearchOriginPath, "Restore should keep the search origin");
    }

    public static void RestoreNormalizationConvertsInvalidSearchTabsToFallbackFolders()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\Missing");
        var panel = state.VisiblePanels[0];

        state = service.NavigateToSearch(
            state,
            panel.Id,
            new SearchQuery("report", SearchScope.CurrentFolder, 1000, @"C:\Missing"));

        var normalized = DockLayoutStore.NormalizeForRestore(state, @"C:\Fallback", path => path == @"C:\Fallback");
        var tab = normalized.FindPanel(panel.Id).ActiveTab!;

        TestAssert.Equal(TabLocationKind.Folder, tab.LocationKind, "Invalid search tabs should become fallback folder tabs");
        TestAssert.Equal(@"C:\Fallback", tab.CurrentPath, "Invalid search tabs should restore to fallback path");
        TestAssert.Equal(null, tab.SearchQuery, "Invalid search tabs should clear query metadata");
    }
}
