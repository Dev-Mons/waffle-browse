namespace Waffle.Browse.Core.Docking;

public sealed record DockDropPreview(
    DockDropOperation Operation,
    Guid TargetPanelId,
    DockDirection? SplitDirection,
    DockRect PreviewBounds,
    bool Accepted,
    string? RejectionReason = null)
{
    public static DockDropPreview None(Guid targetPanelId, DockRect bounds)
    {
        return new DockDropPreview(
            DockDropOperation.None,
            targetPanelId,
            null,
            bounds,
            false,
            "No drop operation is available.");
    }
}
