namespace Waffle.Browse.Core.Docking;

public sealed class DockGridService
{
    public IReadOnlyList<Guid> GetLeafPanelIds(DockGridState grid)
    {
        var leaves = new List<Guid>();
        CollectLeaves(grid.Root, leaves);
        return leaves;
    }

    public DockGridState Split(DockGridState grid, Guid targetPanelId, Guid newPanelId, DockDirection direction)
    {
        if (GetLeafPanelIds(grid).Count >= DockLayoutService.MaxPanels)
        {
            throw new InvalidOperationException("The layout already has four visible panels.");
        }

        var (root, changed) = SplitNode(grid.Root, targetPanelId, newPanelId, direction);
        if (!changed)
        {
            throw new InvalidOperationException("The target panel does not exist in the layout grid.");
        }

        return grid with { Root = root };
    }

    public DockGridState RemoveLeaf(DockGridState grid, Guid panelId)
    {
        var (root, changed) = RemoveNode(grid.Root, panelId);
        return changed && root is not null
            ? grid with { Root = root }
            : grid;
    }

    public DockLayoutKind GetLayoutKind(DockGridState grid)
    {
        var leaves = GetLeafPanelIds(grid);
        return leaves.Count switch
        {
            <= 1 => DockLayoutKind.OneByOne,
            2 => GetTwoLeafKind(grid.Root),
            3 => DockLayoutKind.ThreePanelPrimaryLeft,
            _ => DockLayoutKind.TwoByTwo
        };
    }

    public IReadOnlyDictionary<Guid, DockRect> GetNormalizedLeafBounds(DockGridState grid)
    {
        var bounds = new Dictionary<Guid, DockRect>();
        AssignBounds(grid.Root, new DockRect(0, 0, 1, 1), bounds);
        return bounds;
    }

    private static DockLayoutKind GetTwoLeafKind(DockNode root)
    {
        return root is DockSplit { Orientation: DockOrientation.Vertical }
            ? DockLayoutKind.TwoByOne
            : DockLayoutKind.OneByTwo;
    }

    private static (DockNode Node, bool Changed) SplitNode(
        DockNode node,
        Guid targetPanelId,
        Guid newPanelId,
        DockDirection direction)
    {
        if (node is DockLeaf leaf)
        {
            if (leaf.PanelId != targetPanelId)
            {
                return (node, false);
            }

            var newLeaf = new DockLeaf(newPanelId);
            var targetLeaf = new DockLeaf(targetPanelId);
            var orientation = direction is DockDirection.Left or DockDirection.Right
                ? DockOrientation.Horizontal
                : DockOrientation.Vertical;

            return direction is DockDirection.Left or DockDirection.Top
                ? (new DockSplit(orientation, newLeaf, targetLeaf), true)
                : (new DockSplit(orientation, targetLeaf, newLeaf), true);
        }

        if (node is DockSplit split)
        {
            var (first, firstChanged) = SplitNode(split.First, targetPanelId, newPanelId, direction);
            if (firstChanged)
            {
                return (split with { First = first }, true);
            }

            var (second, secondChanged) = SplitNode(split.Second, targetPanelId, newPanelId, direction);
            return secondChanged
                ? (split with { Second = second }, true)
                : (node, false);
        }

        return (node, false);
    }

    private static (DockNode? Node, bool Changed) RemoveNode(DockNode node, Guid panelId)
    {
        if (node is DockLeaf leaf)
        {
            return leaf.PanelId == panelId
                ? (null, true)
                : (leaf, false);
        }

        if (node is not DockSplit split)
        {
            return (node, false);
        }

        var (first, firstChanged) = RemoveNode(split.First, panelId);
        var (second, secondChanged) = RemoveNode(split.Second, panelId);

        if (!firstChanged && !secondChanged)
        {
            return (node, false);
        }

        return (first, second) switch
        {
            (null, null) => (null, true),
            (not null, null) => (first, true),
            (null, not null) => (second, true),
            _ => (split with { First = first!, Second = second! }, true)
        };
    }

    private static void CollectLeaves(DockNode node, List<Guid> leaves)
    {
        switch (node)
        {
            case DockLeaf leaf:
                leaves.Add(leaf.PanelId);
                break;
            case DockSplit split:
                CollectLeaves(split.First, leaves);
                CollectLeaves(split.Second, leaves);
                break;
        }
    }

    private static void AssignBounds(DockNode node, DockRect rect, Dictionary<Guid, DockRect> bounds)
    {
        switch (node)
        {
            case DockLeaf leaf:
                bounds[leaf.PanelId] = rect;
                break;
            case DockSplit { Orientation: DockOrientation.Horizontal } split:
                AssignBounds(split.First, new DockRect(rect.X, rect.Y, rect.Width * split.Ratio, rect.Height), bounds);
                AssignBounds(split.Second, new DockRect(
                    rect.X + rect.Width * split.Ratio,
                    rect.Y,
                    rect.Width * (1 - split.Ratio),
                    rect.Height), bounds);
                break;
            case DockSplit { Orientation: DockOrientation.Vertical } split:
                AssignBounds(split.First, new DockRect(rect.X, rect.Y, rect.Width, rect.Height * split.Ratio), bounds);
                AssignBounds(split.Second, new DockRect(
                    rect.X,
                    rect.Y + rect.Height * split.Ratio,
                    rect.Width,
                    rect.Height * (1 - split.Ratio)), bounds);
                break;
        }
    }
}
