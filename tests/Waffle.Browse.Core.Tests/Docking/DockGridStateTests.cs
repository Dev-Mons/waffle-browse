using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockGridStateTests
{
    public static void DockGridDerivesLayoutKindFromLeafTree()
    {
        var service = new DockGridService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var fourth = Guid.NewGuid();

        var grid = DockGridState.Single(first);
        TestAssert.Equal(DockLayoutKind.OneByOne, service.GetLayoutKind(grid), "Single leaf should derive 1x1");

        grid = service.Split(grid, first, second, DockDirection.Right);
        TestAssert.Equal(DockLayoutKind.OneByTwo, service.GetLayoutKind(grid), "Right split should derive 1x2");

        grid = service.Split(grid, second, third, DockDirection.Bottom);
        TestAssert.Equal(DockLayoutKind.ThreePanelPrimaryLeft, service.GetLayoutKind(grid), "Third leaf should derive three-panel layout");

        grid = service.Split(grid, third, fourth, DockDirection.Left);
        TestAssert.Equal(DockLayoutKind.TwoByTwo, service.GetLayoutKind(grid), "Fourth leaf should derive 2x2");
    }

    public static void DockGridPreservesLeafOrderForRendering()
    {
        var service = new DockGridService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var grid = DockGridState.Single(first);

        grid = service.Split(grid, first, second, DockDirection.Right);
        grid = service.Split(grid, second, third, DockDirection.Bottom);

        var leaves = service.GetLeafPanelIds(grid).ToList();
        TestAssert.Equal(3, leaves.Count, "Grid should contain three leaves");
        TestAssert.Equal(first, leaves[0], "First leaf should stay first");
        TestAssert.True(leaves.Contains(second), "Second leaf should be present");
        TestAssert.True(leaves.Contains(third), "Third leaf should be present");
    }

    public static void DockGridRejectsFifthLeaf()
    {
        var service = new DockGridService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var fourth = Guid.NewGuid();
        var fifth = Guid.NewGuid();
        var grid = DockGridState.Single(first);

        grid = service.Split(grid, first, second, DockDirection.Right);
        grid = service.Split(grid, second, third, DockDirection.Bottom);
        grid = service.Split(grid, third, fourth, DockDirection.Left);

        TestAssert.Equal(4, service.GetLeafPanelIds(grid).Count, "Setup should have four leaves");

        var threw = false;
        try
        {
            service.Split(grid, fourth, fifth, DockDirection.Right);
        }
        catch (InvalidOperationException ex)
        {
            threw = ex.Message == "The layout already has four visible panels.";
        }

        TestAssert.True(threw, "Fifth split should throw the max panel error");
    }

    public static void DockGridProjectsNormalizedLeafBounds()
    {
        var service = new DockGridService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var grid = DockGridState.Single(first);

        grid = service.Split(grid, first, second, DockDirection.Right);
        grid = service.Split(grid, second, third, DockDirection.Bottom);

        var bounds = service.GetNormalizedLeafBounds(grid);

        TestAssert.Equal(new DockRect(0, 0, 0.5, 1), bounds[first], "First leaf should occupy the left half");
        TestAssert.Equal(new DockRect(0.5, 0, 0.5, 0.5), bounds[second], "Second leaf should occupy the top-right quarter");
        TestAssert.Equal(new DockRect(0.5, 0.5, 0.5, 0.5), bounds[third], "Third leaf should occupy the bottom-right quarter");
    }
}
