using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class NativeShellSelectionGestureTrackerTests
{
    public static void ClickRequestsSelectionSync()
    {
        var tracker = new NativeShellSelectionGestureTracker();
        var panelId = Guid.NewGuid();

        tracker.Begin(panelId, 100, 200);

        if (!tracker.Complete(panelId, 102, 202, 4, 4))
        {
            throw new InvalidOperationException("A native shell click should request focused-item selection sync.");
        }
    }

    public static void DragSkipsSelectionSync()
    {
        var tracker = new NativeShellSelectionGestureTracker();
        var panelId = Guid.NewGuid();

        tracker.Begin(panelId, 100, 200);

        if (tracker.Complete(panelId, 104, 200, 4, 4))
        {
            throw new InvalidOperationException("A native shell drag must not re-add the previously focused item to the drag selection.");
        }
    }

    public static void ReleaseInAnotherPanelSkipsSelectionSync()
    {
        var tracker = new NativeShellSelectionGestureTracker();

        tracker.Begin(Guid.NewGuid(), 100, 200);

        if (tracker.Complete(Guid.NewGuid(), 100, 200, 4, 4))
        {
            throw new InvalidOperationException("A gesture released in another panel must not sync selection.");
        }
    }
}
