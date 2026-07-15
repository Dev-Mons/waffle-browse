using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockLayoutCommitTests
{
    public static void CommittingSplitPreviewUpdatesGridAndPanelState()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var sourcePanel = state.VisiblePanels[0];
        state = service.AddTab(state, sourcePanel.Id, @"C:\Split");
        var tabId = state.FindPanel(sourcePanel.Id).ActiveTab!.Id;

        var preview = new DockDropPreview(
            DockDropOperation.SplitPanel,
            sourcePanel.Id,
            DockDirection.Right,
            new DockRect(500, 0, 500, 800),
            true);

        var result = service.CommitDrop(state, new DockDragPayload(sourcePanel.Id, tabId), preview);

        TestAssert.True(result.Accepted, "Split preview should commit");
        TestAssert.Equal(DockLayoutKind.OneByTwo, result.State.LayoutKind, "Right split should derive 1x2");
        TestAssert.Equal(2, result.State.VisiblePanels.Count, "Split should create second visible panel");
        TestAssert.NotNull(result.State.Grid, "Committed layout should have grid state");
    }

    public static void CommittingSplitPreviewRejectsLastTabSplitIntoSamePanel()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var sourcePanel = state.VisiblePanels[0];
        var tabId = state.FindPanel(sourcePanel.Id).ActiveTab!.Id;

        var result = service.CommitDrop(
            state,
            new DockDragPayload(sourcePanel.Id, tabId),
            new DockDropPreview(
                DockDropOperation.SplitPanel,
                sourcePanel.Id,
                DockDirection.Right,
                new DockRect(500, 0, 500, 800),
                true));

        TestAssert.False(result.Accepted, "Last tab should not split into its own panel");
        TestAssert.Equal("Cannot split the only tab in a panel into itself.", result.Reason, "Rejection reason should explain the self split");
        TestAssert.Equal(DockLayoutKind.OneByOne, result.State.LayoutKind, "Rejected split should preserve layout kind");
        TestAssert.Equal(1, result.State.VisiblePanels.Count, "Rejected split should keep a single visible panel");
        TestAssert.Equal(1, result.State.FindPanel(sourcePanel.Id).Tabs.Count, "Rejected split should not duplicate the tab");
    }

    public static void CommittingMovePreviewCollapsesEmptySourcePanelThroughGrid()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var first = state.VisiblePanels[0];
        state = service.AddTab(state, first.Id, @"C:\Second");

        var split = service.CommitDrop(
            state,
            new DockDragPayload(first.Id, state.FindPanel(first.Id).ActiveTab!.Id),
            new DockDropPreview(DockDropOperation.SplitPanel, first.Id, DockDirection.Right, new DockRect(500, 0, 500, 800), true));

        state = split.State;
        var target = state.VisiblePanels[0];
        var source = state.VisiblePanels[1];
        var sourceTabId = state.FindPanel(source.Id).ActiveTab!.Id;

        var move = service.CommitDrop(
            state,
            new DockDragPayload(source.Id, sourceTabId),
            new DockDropPreview(DockDropOperation.MoveIntoPanel, target.Id, null, new DockRect(0, 0, 1000, 800), true));

        TestAssert.True(move.Accepted, "Move preview should commit");
        TestAssert.Equal(DockLayoutKind.OneByOne, move.State.LayoutKind, "Moving last source tab should collapse grid to 1x1");
        TestAssert.Equal(1, move.State.VisiblePanels.Count, "Only target panel should remain visible");
        TestAssert.False(move.State.FindPanel(source.Id).IsVisible, "Source panel should be hidden");
    }

    public static void RejectedPreviewDoesNotChangeLayoutState()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 4);
        var source = state.VisiblePanels[0];
        var tabId = state.FindPanel(source.Id).ActiveTab!.Id;

        var result = service.CommitDrop(
            state,
            new DockDragPayload(source.Id, tabId),
            new DockDropPreview(DockDropOperation.SplitPanel, source.Id, DockDirection.Right, new DockRect(500, 0, 500, 800), false, "The layout already has four visible panels."));

        TestAssert.False(result.Accepted, "Rejected preview should not commit");
        TestAssert.Equal(4, result.State.VisiblePanels.Count, "Rejected preview should preserve visible panel count");
        TestAssert.Equal(state.LayoutKind, result.State.LayoutKind, "Rejected preview should preserve layout kind");
    }
}
