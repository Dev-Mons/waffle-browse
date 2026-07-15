using System.Runtime.InteropServices;
using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class ShellViewActivationManagerTests
{
    public static void ActivateWithFocusUsesInPlaceThenFocusedShellViewState()
    {
        var manager = new ShellViewActivationManager();
        var activatedStates = new List<ShellViewActivationState>();

        if (!manager.ActivateWithFocus(state =>
            {
                activatedStates.Add(state);
                return 0;
            }))
        {
            throw new InvalidOperationException("Shell view activation should report success.");
        }

        if (activatedStates.Count != 2
            || activatedStates[0] != ShellViewActivationState.InPlaceActivate
            || activatedStates[1] != ShellViewActivationState.ActivateFocus)
        {
            throw new InvalidOperationException("Shell view should be activated in-place before taking focus.");
        }
    }

    public static void ActivateWithFocusIgnoresComFailures()
    {
        var manager = new ShellViewActivationManager();

        if (manager.ActivateWithFocus(_ => throw new COMException("No active shell view.")))
        {
            throw new InvalidOperationException("COM activation failures should be ignored.");
        }
    }

    public static void ActivateWithFocusTreatsFailedHresultAsFailure()
    {
        var manager = new ShellViewActivationManager();

        if (manager.ActivateWithFocus(_ => unchecked((int)0x80004005)))
        {
            throw new InvalidOperationException("Failed HRESULT values should report activation failure.");
        }
    }
}
