using Waffle.Browse.Core.Docking;
using Waffle.Browse.Core.Navigation;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockLayoutServiceTests
{
    public static void PresetSwitchingCreatesExpectedVisiblePanels()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");

        TestAssert.Equal(DockLayoutKind.OneByOne, state.LayoutKind, "Default layout should be single panel");
        TestAssert.Equal(1, state.VisiblePanels.Count, "Default layout should show one panel");

        state = service.SetVisiblePanelCount(state, 2);
        TestAssert.Equal(DockLayoutKind.OneByTwo, state.LayoutKind, "Two panels should use the left/right preset by default");
        TestAssert.Equal(2, state.VisiblePanels.Count, "Two panels should be visible");

        state = service.SetLayout(state, DockLayoutKind.TwoByOne);
        TestAssert.Equal(DockLayoutKind.TwoByOne, state.LayoutKind, "Two-row preset should be selectable");
        TestAssert.Equal(2, state.VisiblePanels.Count, "Two-row preset should still show two panels");

        state = service.SetVisiblePanelCount(state, 3);
        TestAssert.Equal(DockLayoutKind.ThreePanelPrimaryLeft, state.LayoutKind, "Three panels should use the primary-left preset");
        TestAssert.Equal(3, state.VisiblePanels.Count, "Three panels should be visible");

        state = service.SetVisiblePanelCount(state, 4);
        TestAssert.Equal(DockLayoutKind.TwoByTwo, state.LayoutKind, "Four panels should use the 2x2 preset");
        TestAssert.Equal(4, state.VisiblePanels.Count, "Four panels should be visible");
    }

    public static void PanelPathsStaySeparatedAcrossLayoutChanges()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 4);
        var panels = state.VisiblePanels.ToList();

        state = service.NavigateTo(state, panels[0].Id, @"C:\Alpha");
        state = service.NavigateTo(state, panels[1].Id, @"C:\Beta");
        state = service.NavigateTo(state, panels[2].Id, @"C:\Gamma");
        state = service.NavigateTo(state, panels[3].Id, @"C:\Delta");

        state = service.SetVisiblePanelCount(state, 2);
        TestAssert.Equal(2, state.VisiblePanels.Count, "Reducing panel count should hide panels");

        state = service.SetVisiblePanelCount(state, 4);
        var restored = state.Panels.ToDictionary(panel => panel.Id);

        TestAssert.Equal(@"C:\Alpha", restored[panels[0].Id].ActiveTab?.CurrentPath, "First panel path should stay isolated");
        TestAssert.Equal(@"C:\Beta", restored[panels[1].Id].ActiveTab?.CurrentPath, "Second panel path should stay isolated");
        TestAssert.Equal(@"C:\Gamma", restored[panels[2].Id].ActiveTab?.CurrentPath, "Hidden third panel path should be restored");
        TestAssert.Equal(@"C:\Delta", restored[panels[3].Id].ActiveTab?.CurrentPath, "Hidden fourth panel path should be restored");
    }

    public static void FourPanelPresetCreatesTrueTwoByTwoGrid()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 4);
        var panels = state.VisiblePanels.ToList();

        TestAssert.Equal(DockLayoutKind.TwoByTwo, state.LayoutKind, "Four panels should use the 2x2 preset");
        TestAssert.NotNull(state.Grid, "Four panels should have a grid");

        var bounds = new DockGridService().GetNormalizedLeafBounds(state.Grid!);
        TestAssert.Equal(new DockRect(0, 0, 0.5, 0.5), bounds[panels[0].Id], "First panel should be top-left");
        TestAssert.Equal(new DockRect(0.5, 0, 0.5, 0.5), bounds[panels[1].Id], "Second panel should be top-right");
        TestAssert.Equal(new DockRect(0, 0.5, 0.5, 0.5), bounds[panels[2].Id], "Third panel should be bottom-left");
        TestAssert.Equal(new DockRect(0.5, 0.5, 0.5, 0.5), bounds[panels[3].Id], "Fourth panel should be bottom-right");
    }

    public static void DockingTabsCreatesLayoutsAndRefusesFifthPanel()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var sourcePanel = state.VisiblePanels[0];

        state = service.AddTab(state, sourcePanel.Id, @"C:\DockRight");
        var firstDockedTab = state.FindPanel(sourcePanel.Id).ActiveTab!.Id;
        var result = service.DockTab(state, sourcePanel.Id, firstDockedTab, sourcePanel.Id, DockDirection.Right);

        TestAssert.True(result.Accepted, "Docking right from one panel should be accepted");
        state = result.State;
        TestAssert.Equal(DockLayoutKind.OneByTwo, state.LayoutKind, "Docking right should create a left/right layout");
        TestAssert.Equal(2, state.VisiblePanels.Count, "Docking right should create a second visible panel");

        var activePanel = state.FindPanel(state.ActivePanelId!.Value);
        state = service.AddTab(state, activePanel.Id, @"C:\DockBottom");
        result = service.DockTab(state, activePanel.Id, state.FindPanel(activePanel.Id).ActiveTab!.Id, activePanel.Id, DockDirection.Bottom);
        TestAssert.True(result.Accepted, "Docking a third panel should be accepted");
        state = result.State;
        TestAssert.Equal(3, state.VisiblePanels.Count, "Third dock should create three visible panels");

        activePanel = state.FindPanel(state.ActivePanelId!.Value);
        state = service.AddTab(state, activePanel.Id, @"C:\DockFourth");
        result = service.DockTab(state, activePanel.Id, state.FindPanel(activePanel.Id).ActiveTab!.Id, activePanel.Id, DockDirection.Left);
        TestAssert.True(result.Accepted, "Docking a fourth panel should be accepted");
        state = result.State;
        TestAssert.Equal(DockLayoutKind.TwoByTwo, state.LayoutKind, "Four panels should normalize to 2x2");
        TestAssert.Equal(4, state.VisiblePanels.Count, "Fourth dock should create four visible panels");

        activePanel = state.FindPanel(state.ActivePanelId!.Value);
        state = service.AddTab(state, activePanel.Id, @"C:\DockRejected");
        result = service.DockTab(state, activePanel.Id, state.FindPanel(activePanel.Id).ActiveTab!.Id, activePanel.Id, DockDirection.Top);
        TestAssert.False(result.Accepted, "A fifth visible panel should be refused");
        TestAssert.Equal(4, result.State.VisiblePanels.Count, "Refused docking should keep four visible panels");
        TestAssert.Equal(DockLayoutKind.TwoByTwo, result.State.LayoutKind, "Refused docking should preserve the current layout");
    }

    public static void EdgeDockingFromOnePanelCreatesHorizontalAndVerticalSplits()
    {
        var service = new DockLayoutService();
        var horizontalState = service.CreateDefault(@"C:\");
        var horizontalPanel = horizontalState.VisiblePanels[0];
        horizontalState = service.AddTab(horizontalState, horizontalPanel.Id, @"C:\Right");

        var horizontalResult = service.DockTab(
            horizontalState,
            horizontalPanel.Id,
            horizontalState.FindPanel(horizontalPanel.Id).ActiveTab!.Id,
            horizontalPanel.Id,
            DockDirection.Right);

        TestAssert.True(horizontalResult.Accepted, "Right edge dock should be accepted");
        TestAssert.Equal(DockLayoutKind.OneByTwo, horizontalResult.State.LayoutKind, "Right edge dock should create a 1x2 layout");
        TestAssert.Equal(2, horizontalResult.State.VisiblePanels.Count, "Right edge dock should create two visible panels");

        var verticalState = service.CreateDefault(@"C:\");
        var verticalPanel = verticalState.VisiblePanels[0];
        verticalState = service.AddTab(verticalState, verticalPanel.Id, @"C:\Bottom");

        var verticalResult = service.DockTab(
            verticalState,
            verticalPanel.Id,
            verticalState.FindPanel(verticalPanel.Id).ActiveTab!.Id,
            verticalPanel.Id,
            DockDirection.Bottom);

        TestAssert.True(verticalResult.Accepted, "Bottom edge dock should be accepted");
        TestAssert.Equal(DockLayoutKind.TwoByOne, verticalResult.State.LayoutKind, "Bottom edge dock should create a 2x1 layout");
        TestAssert.Equal(2, verticalResult.State.VisiblePanels.Count, "Bottom edge dock should create two visible panels");
    }

    public static void MovingTabsToCenterPreservesTargetPanelState()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 2);
        var source = state.VisiblePanels[0];
        var target = state.VisiblePanels[1];

        state = service.NavigateTo(state, target.Id, @"C:\Target");
        state = service.AddTab(state, source.Id, @"C:\Moved");
        var movingTab = state.FindPanel(source.Id).ActiveTab!.Id;

        var result = service.DockTab(state, source.Id, movingTab, target.Id, DockDirection.Center);

        TestAssert.True(result.Accepted, "Center drop should move a tab into the target panel");
        var movedState = result.State;
        TestAssert.Equal(2, movedState.VisiblePanels.Count, "Center drop should not create a new panel");
        TestAssert.Equal(2, movedState.FindPanel(target.Id).Tabs.Count, "Target panel should keep its existing tab and receive the moved tab");
        TestAssert.Equal(@"C:\Moved", movedState.FindPanel(target.Id).ActiveTab?.CurrentPath, "Moved tab should become active in target panel");
    }

    public static void CenterDropOfLastSourceTabRemovesEmptyPanel()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var source = state.VisiblePanels[0];
        state = service.AddTab(state, source.Id, @"C:\Second");

        var split = service.DockTab(
            state,
            source.Id,
            state.FindPanel(source.Id).ActiveTab!.Id,
            source.Id,
            DockDirection.Right);
        TestAssert.True(split.Accepted, "Initial split should be accepted");

        state = split.State;
        var visiblePanels = state.VisiblePanels;
        var target = visiblePanels[0];
        source = visiblePanels[1];
        var sourceTab = state.FindPanel(source.Id).ActiveTab!.Id;

        var result = service.DockTab(state, source.Id, sourceTab, target.Id, DockDirection.Center);

        TestAssert.True(result.Accepted, "Center drop should be accepted");
        TestAssert.Equal(DockLayoutKind.OneByOne, result.State.LayoutKind, "Moving the last source tab into another panel should collapse to 1x1");
        TestAssert.Equal(1, result.State.VisiblePanels.Count, "Empty source panel should be removed from the visible layout");
        TestAssert.False(result.State.FindPanel(source.Id).IsVisible, "The emptied source panel should be hidden");
        TestAssert.Equal(2, result.State.FindPanel(target.Id).Tabs.Count, "The target panel should contain both tabs");
    }

    public static void CenterDropsCanCollapseFromTwoByTwoBackToOneByOne()
    {
        var service = new DockLayoutService();
        var state = CreateTwoByTwoFromTabDrags(service);
        TestAssert.Equal(DockLayoutKind.TwoByTwo, state.LayoutKind, "Setup should create 2x2");
        TestAssert.Equal(4, state.VisiblePanels.Count, "Setup should create four visible panels");

        var target = state.VisiblePanels[0];

        foreach (var source in state.VisiblePanels.Skip(1).Reverse().ToList())
        {
            var tabId = state.FindPanel(source.Id).ActiveTab!.Id;
            var result = service.DockTab(state, source.Id, tabId, target.Id, DockDirection.Center);
            TestAssert.True(result.Accepted, "Center drop into the target panel should be accepted");
            state = result.State;
        }

        TestAssert.Equal(DockLayoutKind.OneByOne, state.LayoutKind, "Center drops should be able to collapse a 2x2 layout back to 1x1");
        TestAssert.Equal(1, state.VisiblePanels.Count, "Only the target panel should remain visible");
        TestAssert.Equal(4, state.FindPanel(target.Id).Tabs.Count, "All moved tabs should be preserved in the target panel");
    }

    public static void MovingTabsWithinPanelReordersTabState()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var panel = state.VisiblePanels[0];

        state = service.AddTab(state, panel.Id, @"C:\Second");
        state = service.AddTab(state, panel.Id, @"C:\Third");
        var currentPanel = state.FindPanel(panel.Id);
        var thirdTab = currentPanel.Tabs[2];

        state = service.MoveTabWithinPanel(state, panel.Id, thirdTab.Id, 0);
        var reorderedPanel = state.FindPanel(panel.Id);

        TestAssert.Equal(thirdTab.Id, reorderedPanel.Tabs[0].Id, "Moved tab should be placed at the requested index");
        TestAssert.Equal(thirdTab.Id, reorderedPanel.ActiveTabId, "Moved tab should become active");
        TestAssert.Equal(3, reorderedPanel.Tabs.Count, "Reordering should not add or remove tabs");
    }

    public static void PanelModelDoesNotExposeRenameSurface()
    {
        TestAssert.Equal(null, typeof(PanelState).GetProperty("DisplayName"), "Panel state should not expose display names");
        TestAssert.Equal(null, typeof(DockLayoutService).GetMethod("RenamePanel"), "Dock layout service should not expose panel renaming");
    }

    public static void ActivatingPanelUpdatesActivePanelWithoutChangingTabs()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 2);
        var targetPanel = state.VisiblePanels[1];
        var targetTab = targetPanel.ActiveTab!;

        var activated = service.ActivatePanel(state, targetPanel.Id);
        var activatedPanel = activated.FindPanel(targetPanel.Id);

        TestAssert.Equal(targetPanel.Id, activated.ActivePanelId, "Activated panel should become the active panel");
        TestAssert.Equal(targetTab.Id, activatedPanel.ActiveTabId, "Activating a panel should not change its active tab");
        TestAssert.Equal(targetPanel.Tabs.Count, activatedPanel.Tabs.Count, "Activating a panel should not add or remove tabs");
    }

    public static void NavigateToSearchTurnsActiveTabIntoSearchLocation()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\Work");
        var panel = state.VisiblePanels[0];

        state = service.NavigateToSearch(
            state,
            panel.Id,
            "report",
            [@"C:\Work"],
            @"search-ms:query=report&crumb=location:C%3A%5CWork,include,recursive");
        var tab = state.FindPanel(panel.Id).ActiveTab!;

        TestAssert.Equal(TabLocationKind.Search, tab.LocationKind, "Search should mark the active tab as a search location");
        TestAssert.Equal("검색: report", tab.Title, "Search tab title should show the query");
        TestAssert.Equal("report", tab.SearchQuery, "Search query should be stored on the tab");
        TestAssert.Equal(@"C:\Work", tab.SearchOriginPath, "Search origin should be the folder that was searched");
        TestAssert.Equal(1, tab.SearchRoots.Count, "Search roots should be stored on the tab");
        TestAssert.Equal(@"C:\Work", tab.BackStack[^1], "Back should return to the searched folder");
    }

    public static void ClearingSearchRestoresOriginFolder()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\Work");
        var panel = state.VisiblePanels[0];

        state = service.NavigateToSearch(
            state,
            panel.Id,
            "report",
            [@"C:\Work"],
            @"search-ms:query=report&crumb=location:C%3A%5CWork,include,recursive");
        state = service.ClearSearch(state, panel.Id);
        var tab = state.FindPanel(panel.Id).ActiveTab!;

        TestAssert.Equal(TabLocationKind.Folder, tab.LocationKind, "Clearing search should restore a folder tab");
        TestAssert.Equal(@"C:\Work", tab.CurrentPath, "Clearing search should restore the search origin folder");
        TestAssert.Equal(null, tab.SearchQuery, "Clearing search should remove the query metadata");
        TestAssert.Equal(null, tab.SearchOriginPath, "Clearing search should remove the origin metadata");
        TestAssert.Equal(0, tab.SearchRoots.Count, "Clearing search should remove the search roots");
    }

    public static void ClosingLastTabRemovesPanelFromLayout()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 2);
        var closingPanel = state.VisiblePanels[1];
        var tabId = state.FindPanel(closingPanel.Id).ActiveTab!.Id;

        state = service.CloseTab(state, closingPanel.Id, tabId);

        TestAssert.Equal(1, state.VisiblePanels.Count, "Closing the only tab in a panel should hide that panel");
        TestAssert.Equal(DockLayoutKind.OneByOne, state.LayoutKind, "Layout should collapse after closing a panel");
        TestAssert.False(state.FindPanel(closingPanel.Id).IsVisible, "Closed panel should not remain visible");
        TestAssert.Equal(0, state.FindPanel(closingPanel.Id).Tabs.Count, "Closed panel should not be reset with a fallback tab");
    }

    public static void ClosingEveryPanelCreatesEmptyLayout()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var panel = state.VisiblePanels[0];
        var tabId = state.FindPanel(panel.Id).ActiveTab!.Id;

        state = service.CloseTab(state, panel.Id, tabId);

        TestAssert.Equal(DockLayoutKind.Empty, state.LayoutKind, "Closing the final panel should create an empty layout");
        TestAssert.Equal(0, state.VisiblePanels.Count, "Closing the final panel should leave no visible panels");
        TestAssert.Equal(null, state.ActivePanelId, "Empty layout should not have an active panel");
        TestAssert.Equal(null, state.Grid, "Empty layout should not have a grid root");
        TestAssert.Equal(0, state.FindPanel(panel.Id).Tabs.Count, "Closed final panel should keep no tabs");
    }

    public static void NavigationHistorySupportsBackForwardAndParent()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\Root");
        var panel = state.VisiblePanels[0];

        state = service.NavigateTo(state, panel.Id, @"C:\Root\Child");
        state = service.NavigateTo(state, panel.Id, @"C:\Root\Child\Grandchild");
        TestAssert.Equal(@"C:\Root\Child\Grandchild", state.FindPanel(panel.Id).ActiveTab?.CurrentPath, "Navigation should update current path");

        state = service.NavigateBack(state, panel.Id);
        TestAssert.Equal(@"C:\Root\Child", state.FindPanel(panel.Id).ActiveTab?.CurrentPath, "Back should restore the previous path");

        state = service.NavigateForward(state, panel.Id);
        TestAssert.Equal(@"C:\Root\Child\Grandchild", state.FindPanel(panel.Id).ActiveTab?.CurrentPath, "Forward should restore the next path");

        state = service.NavigateUp(state, panel.Id);
        TestAssert.Equal(@"C:\Root\Child", state.FindPanel(panel.Id).ActiveTab?.CurrentPath, "Up should navigate to the parent path");
    }

    public static void NavigateUpFromDriveRootOpensThisPc()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var panel = state.VisiblePanels[0];

        state = service.NavigateUp(state, panel.Id);
        var tab = state.FindPanel(panel.Id).ActiveTab!;

        TestAssert.Equal(ShellFolderPaths.ThisPc, tab.CurrentPath, "Up from a drive root should navigate to This PC");
        TestAssert.Equal(ShellFolderPaths.ThisPcDisplayName, tab.Title, "This PC should have a readable tab title");
        TestAssert.Equal(@"C:\", tab.BackStack[^1], "Drive root should remain in the back stack");
    }

    private static DockLayoutState CreateTwoByTwoFromTabDrags(DockLayoutService service)
    {
        var state = service.CreateDefault(@"C:\");
        var sourcePanel = state.VisiblePanels[0];

        state = service.AddTab(state, sourcePanel.Id, @"C:\Right");
        var result = service.DockTab(state, sourcePanel.Id, state.FindPanel(sourcePanel.Id).ActiveTab!.Id, sourcePanel.Id, DockDirection.Right);
        state = result.State;

        var activePanel = state.FindPanel(state.ActivePanelId!.Value);
        state = service.AddTab(state, activePanel.Id, @"C:\Bottom");
        result = service.DockTab(state, activePanel.Id, state.FindPanel(activePanel.Id).ActiveTab!.Id, activePanel.Id, DockDirection.Bottom);
        state = result.State;

        activePanel = state.FindPanel(state.ActivePanelId!.Value);
        state = service.AddTab(state, activePanel.Id, @"C:\Fourth");
        result = service.DockTab(state, activePanel.Id, state.FindPanel(activePanel.Id).ActiveTab!.Id, activePanel.Id, DockDirection.Left);

        return result.State;
    }
}
