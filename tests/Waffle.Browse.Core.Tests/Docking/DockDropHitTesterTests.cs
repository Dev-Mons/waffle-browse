using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockDropHitTesterTests
{
    public static void CenterAreaReturnsMoveIntoPanel()
    {
        var targetPanelId = Guid.NewGuid();
        var payload = new DockDragPayload(Guid.NewGuid(), Guid.NewGuid());
        var preview = new DockDropHitTester().HitTest(
            new DockRect(0, 0, 1000, 800),
            new DockPoint(500, 400),
            targetPanelId,
            payload,
            new DockDropOptions(CurrentVisiblePanelCount: 2));

        TestAssert.Equal(DockDropOperation.MoveIntoPanel, preview.Operation, "Center area should move into target panel");
        TestAssert.Equal(targetPanelId, preview.TargetPanelId, "Preview should target the panel under the pointer");
        TestAssert.Equal(null, preview.SplitDirection, "Move operation should not have a split direction");
        TestAssert.True(preview.Accepted, "Center move should be accepted");
        TestAssert.Equal(new DockRect(0, 0, 1000, 800), preview.PreviewBounds, "Center move should preview the whole panel");
    }

    public static void EdgeAreasReturnSplitPreview()
    {
        var targetPanelId = Guid.NewGuid();
        var payload = new DockDragPayload(Guid.NewGuid(), Guid.NewGuid());
        var hitTester = new DockDropHitTester();
        var options = new DockDropOptions(CurrentVisiblePanelCount: 1);
        var bounds = new DockRect(0, 0, 1000, 800);

        var left = hitTester.HitTest(bounds, new DockPoint(30, 400), targetPanelId, payload, options);
        TestAssert.Equal(DockDropOperation.SplitPanel, left.Operation, "Left edge should split");
        TestAssert.Equal(DockDirection.Left, left.SplitDirection, "Left edge should split left");
        TestAssert.Equal(new DockRect(0, 0, 500, 800), left.PreviewBounds, "Left preview should be left half");

        var right = hitTester.HitTest(bounds, new DockPoint(970, 400), targetPanelId, payload, options);
        TestAssert.Equal(DockDirection.Right, right.SplitDirection, "Right edge should split right");
        TestAssert.Equal(new DockRect(500, 0, 500, 800), right.PreviewBounds, "Right preview should be right half");

        var top = hitTester.HitTest(bounds, new DockPoint(500, 30), targetPanelId, payload, options);
        TestAssert.Equal(DockDirection.Top, top.SplitDirection, "Top edge should split top");
        TestAssert.Equal(new DockRect(0, 0, 1000, 400), top.PreviewBounds, "Top preview should be top half");

        var bottom = hitTester.HitTest(bounds, new DockPoint(500, 770), targetPanelId, payload, options);
        TestAssert.Equal(DockDirection.Bottom, bottom.SplitDirection, "Bottom edge should split bottom");
        TestAssert.Equal(new DockRect(0, 400, 1000, 400), bottom.PreviewBounds, "Bottom preview should be bottom half");
    }

    public static void EdgeSplitRejectedWhenMaxPanelsReached()
    {
        var targetPanelId = Guid.NewGuid();
        var preview = new DockDropHitTester().HitTest(
            new DockRect(0, 0, 1000, 800),
            new DockPoint(20, 400),
            targetPanelId,
            new DockDragPayload(Guid.NewGuid(), Guid.NewGuid()),
            new DockDropOptions(CurrentVisiblePanelCount: 4, MaxVisiblePanels: 4));

        TestAssert.Equal(DockDropOperation.SplitPanel, preview.Operation, "Edge still represents split intent");
        TestAssert.False(preview.Accepted, "Split should be rejected at max panel count");
        TestAssert.Equal("The layout already has four visible panels.", preview.RejectionReason, "Rejection reason should be explicit");
    }

    public static void EdgeAreaReturnsMoveWhenSplitIsDisabled()
    {
        var targetPanelId = Guid.NewGuid();
        var preview = new DockDropHitTester().HitTest(
            new DockRect(0, 0, 1000, 800),
            new DockPoint(20, 400),
            targetPanelId,
            new DockDragPayload(Guid.NewGuid(), Guid.NewGuid()),
            new DockDropOptions(CurrentVisiblePanelCount: 1, SplitOnDragAndDrop: false));

        TestAssert.Equal(DockDropOperation.MoveIntoPanel, preview.Operation, "Disabled split should treat edge as center move");
        TestAssert.Equal(null, preview.SplitDirection, "Disabled split should not carry split direction");
        TestAssert.Equal(new DockRect(0, 0, 1000, 800), preview.PreviewBounds, "Disabled split should preview whole panel");
    }
}
