namespace Waffle.Browse.Core.Docking;

public sealed class DockDropTargetResolver
{
    public DockDropTarget? Resolve(IEnumerable<DockDropTarget> targets, DockPoint pointer)
    {
        return targets.LastOrDefault(target => Contains(target.Bounds, pointer));
    }

    private static bool Contains(DockRect bounds, DockPoint pointer)
    {
        return pointer.X >= bounds.Left
            && pointer.X <= bounds.Right
            && pointer.Y >= bounds.Top
            && pointer.Y <= bounds.Bottom;
    }
}
