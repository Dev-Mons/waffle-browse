namespace Waffle.Browse.App.Shell;

public enum NativeShellKeyboardInputHandling
{
    None,
    ForwardAndHandle
}

public static class NativeShellKeyboardInputClassifier
{
    public const int WmKeyDown = 0x0100;
    public const int WmSysKeyDown = 0x0104;
    public const int VkBack = 0x08;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkDelete = 0x2E;

    public static NativeShellKeyboardInputHandling Resolve(int message, IntPtr wParam)
    {
        if (message is not WmKeyDown and not WmSysKeyDown)
        {
            return NativeShellKeyboardInputHandling.None;
        }

        var virtualKey = unchecked((int)wParam.ToInt64());

        if (message == WmKeyDown && virtualKey == VkBack)
        {
            return NativeShellKeyboardInputHandling.None;
        }

        if (message == WmSysKeyDown && virtualKey is VkLeft or VkRight)
        {
            return NativeShellKeyboardInputHandling.None;
        }

        return NativeShellKeyboardInputHandling.ForwardAndHandle;
    }
}
