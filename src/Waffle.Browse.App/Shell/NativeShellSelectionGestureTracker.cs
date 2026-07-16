namespace Waffle.Browse.App.Shell;

public sealed class NativeShellSelectionGestureTracker
{
    private GestureStart? gestureStart;

    public void Begin(Guid panelId, int screenX, int screenY)
    {
        gestureStart = new GestureStart(panelId, screenX, screenY);
    }

    public bool Complete(
        Guid panelId,
        int screenX,
        int screenY,
        double minimumHorizontalDragDistance,
        double minimumVerticalDragDistance)
    {
        var start = gestureStart;
        gestureStart = null;

        return start is not null
            && start.PanelId == panelId
            && Math.Abs(screenX - start.ScreenX) < minimumHorizontalDragDistance
            && Math.Abs(screenY - start.ScreenY) < minimumVerticalDragDistance;
    }

    public void Cancel()
    {
        gestureStart = null;
    }

    private sealed record GestureStart(Guid PanelId, int ScreenX, int ScreenY);
}
