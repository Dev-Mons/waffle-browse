using System.Runtime.InteropServices;

namespace Waffle.Browse.App.Shell;

public enum ShellViewActivationState : uint
{
    Deactivate = 0,
    ActivateNoFocus = 1,
    ActivateFocus = 2,
    InPlaceActivate = 3
}

public sealed class ShellViewActivationManager
{
    public bool ActivateWithFocus(Func<ShellViewActivationState, int> activate)
    {
        try
        {
            var inPlaceResult = activate(ShellViewActivationState.InPlaceActivate);
            if (inPlaceResult < 0)
            {
                return false;
            }

            var focusResult = activate(ShellViewActivationState.ActivateFocus);
            return focusResult >= 0;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }
}
