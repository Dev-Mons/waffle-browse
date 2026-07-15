using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class NativeShellActivationClassifierTests
{
    private const int WmMouseActivate = 0x0021;

    public static void NativeShellMouseFocusActivatesPanelThenRestoresFocus()
    {
        AssertActivatePanelThenRestoreFocus(WmMouseActivate);
        AssertActivatePanelThenRestoreFocus(NativeShellActivationClassifier.WmLButtonDown);
        AssertActivatePanelThenRestoreFocus(NativeShellActivationClassifier.WmRButtonDown);
        AssertActivatePanelThenRestoreFocus(NativeShellActivationClassifier.WmMButtonDown);
    }

    public static void NativeShellFocusActivatesPanelThenRestoresFocus()
    {
        AssertActivatePanelThenRestoreFocus(NativeShellActivationClassifier.WmSetFocus);
    }

    public static void OtherMessagesDoNotActivatePanel()
    {
        if (NativeShellActivationClassifier.Resolve(0x0100) != NativeShellActivationTiming.None)
        {
            throw new InvalidOperationException("Keyboard messages should not activate panels through the native shell mouse/focus path.");
        }
    }

    private static void AssertActivatePanelThenRestoreFocus(int message)
    {
        if (NativeShellActivationClassifier.Resolve(message) != NativeShellActivationTiming.ActivatePanelThenRestoreFocus)
        {
            throw new InvalidOperationException($"Expected message 0x{message:X} to activate the panel and restore native shell focus.");
        }
    }
}
