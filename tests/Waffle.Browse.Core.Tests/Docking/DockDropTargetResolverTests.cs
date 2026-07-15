using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockDropTargetResolverTests
{
    public static void PicksPanelContainingWorkspacePointer()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var resolver = new DockDropTargetResolver();
        var targets = new[]
        {
            new DockDropTarget(first, new DockRect(0, 0, 400, 600)),
            new DockDropTarget(second, new DockRect(400, 0, 400, 600))
        };

        var target = resolver.Resolve(targets, new DockPoint(650, 120));

        TestAssert.NotNull(target, "Pointer inside second panel should resolve a target");
        TestAssert.Equal(second, target!.PanelId, "Pointer should resolve the panel whose workspace bounds contain it");
    }

    public static void ReturnsNullOutsideAllPanels()
    {
        var resolver = new DockDropTargetResolver();
        var targets = new[]
        {
            new DockDropTarget(Guid.NewGuid(), new DockRect(0, 0, 400, 600))
        };

        var target = resolver.Resolve(targets, new DockPoint(450, 120));

        TestAssert.Equal(null, target, "Pointer outside every panel should not resolve a target");
    }
}
