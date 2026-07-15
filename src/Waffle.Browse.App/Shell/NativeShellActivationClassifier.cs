namespace Waffle.Browse.App.Shell;

public enum NativeShellActivationTiming
{
    None,
    ActivatePanelThenRestoreFocus
}

public static class NativeShellActivationClassifier
{
    public const int WmSetFocus = 0x0007;
    public const int WmMouseActivate = 0x0021;
    public const int WmLButtonDown = 0x0201;
    public const int WmRButtonDown = 0x0204;
    public const int WmMButtonDown = 0x0207;

    public static NativeShellActivationTiming Resolve(int message)
    {
        return message is WmSetFocus or WmMouseActivate or WmLButtonDown or WmRButtonDown or WmMButtonDown
            ? NativeShellActivationTiming.ActivatePanelThenRestoreFocus
            : NativeShellActivationTiming.None;
    }
}
