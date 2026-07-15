namespace Waffle.Browse.Core.Docking;

public sealed class DockDropHitTester
{
    public DockDropPreview HitTest(
        DockRect targetPanelBounds,
        DockPoint pointer,
        Guid targetPanelId,
        DockDragPayload payload,
        DockDropOptions options)
    {
        if (targetPanelBounds.Width <= 0 || targetPanelBounds.Height <= 0)
        {
            return DockDropPreview.None(targetPanelId, targetPanelBounds);
        }

        if (!options.SplitOnDragAndDrop)
        {
            return MovePreview(targetPanelId, targetPanelBounds);
        }

        var leftDistance = Math.Max(0, pointer.X - targetPanelBounds.Left);
        var rightDistance = Math.Max(0, targetPanelBounds.Right - pointer.X);
        var topDistance = Math.Max(0, pointer.Y - targetPanelBounds.Top);
        var bottomDistance = Math.Max(0, targetPanelBounds.Bottom - pointer.Y);
        var horizontalThreshold = targetPanelBounds.Width * options.EdgeThresholdRatio;
        var verticalThreshold = targetPanelBounds.Height * options.EdgeThresholdRatio;

        var direction = GetSplitDirection(
            leftDistance,
            rightDistance,
            topDistance,
            bottomDistance,
            horizontalThreshold,
            verticalThreshold,
            options.PreferredOrientation);

        if (direction is null)
        {
            return MovePreview(targetPanelId, targetPanelBounds);
        }

        var accepted = options.CurrentVisiblePanelCount < options.MaxVisiblePanels;
        return new DockDropPreview(
            DockDropOperation.SplitPanel,
            targetPanelId,
            direction,
            PreviewForDirection(targetPanelBounds, direction.Value),
            accepted,
            accepted ? null : "The layout already has four visible panels.");
    }

    private static DockDropPreview MovePreview(Guid targetPanelId, DockRect targetPanelBounds)
    {
        return new DockDropPreview(DockDropOperation.MoveIntoPanel, targetPanelId, null, targetPanelBounds, true);
    }

    private static DockDirection? GetSplitDirection(
        double leftDistance,
        double rightDistance,
        double topDistance,
        double bottomDistance,
        double horizontalThreshold,
        double verticalThreshold,
        DockOrientation preferredOrientation)
    {
        var horizontalCandidate = leftDistance <= horizontalThreshold || rightDistance <= horizontalThreshold;
        var verticalCandidate = topDistance <= verticalThreshold || bottomDistance <= verticalThreshold;

        if (!horizontalCandidate && !verticalCandidate)
        {
            return null;
        }

        if (horizontalCandidate && verticalCandidate)
        {
            return preferredOrientation == DockOrientation.Horizontal
                ? (leftDistance <= rightDistance ? DockDirection.Left : DockDirection.Right)
                : (topDistance <= bottomDistance ? DockDirection.Top : DockDirection.Bottom);
        }

        if (horizontalCandidate)
        {
            return leftDistance <= rightDistance ? DockDirection.Left : DockDirection.Right;
        }

        return topDistance <= bottomDistance ? DockDirection.Top : DockDirection.Bottom;
    }

    private static DockRect PreviewForDirection(DockRect bounds, DockDirection direction)
    {
        return direction switch
        {
            DockDirection.Left => new DockRect(bounds.X, bounds.Y, bounds.Width / 2, bounds.Height),
            DockDirection.Right => new DockRect(bounds.X + bounds.Width / 2, bounds.Y, bounds.Width / 2, bounds.Height),
            DockDirection.Top => new DockRect(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2),
            DockDirection.Bottom => new DockRect(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height / 2),
            _ => bounds
        };
    }
}
