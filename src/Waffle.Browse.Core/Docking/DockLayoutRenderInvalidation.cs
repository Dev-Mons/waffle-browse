namespace Waffle.Browse.Core.Docking;

public static class DockLayoutRenderInvalidation
{
    public static bool RequiresLayoutRender(DockLayoutState before, DockLayoutState after)
    {
        if (before.LayoutKind != after.LayoutKind)
        {
            return true;
        }

        if (!VisiblePanelOrderEquals(before, after))
        {
            return true;
        }

        return !EqualityComparer<DockGridState?>.Default.Equals(before.Grid, after.Grid);
    }

    private static bool VisiblePanelOrderEquals(DockLayoutState before, DockLayoutState after)
    {
        var beforeIds = before.VisiblePanels.Select(panel => panel.Id);
        var afterIds = after.VisiblePanels.Select(panel => panel.Id);
        return beforeIds.SequenceEqual(afterIds);
    }
}
