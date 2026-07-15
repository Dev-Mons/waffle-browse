using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class NativeShellKeyboardInputClassifierTests
{
    public static void DirectionalKeyDownIsForwardedToNativeShell()
    {
        foreach (var key in new[]
                 {
                     NativeShellKeyboardInputClassifier.VkLeft,
                     NativeShellKeyboardInputClassifier.VkUp,
                     NativeShellKeyboardInputClassifier.VkRight,
                     NativeShellKeyboardInputClassifier.VkDown
                 })
        {
            var handling = NativeShellKeyboardInputClassifier.Resolve(
                NativeShellKeyboardInputClassifier.WmKeyDown,
                new IntPtr(key));

            if (handling != NativeShellKeyboardInputHandling.ForwardAndHandle)
            {
                throw new InvalidOperationException("Directional key down should be forwarded to the native shell.");
            }
        }
    }

    public static void DeleteKeyDownIsForwardedToNativeShell()
    {
        var handling = NativeShellKeyboardInputClassifier.Resolve(
            NativeShellKeyboardInputClassifier.WmKeyDown,
            new IntPtr(NativeShellKeyboardInputClassifier.VkDelete));

        if (handling != NativeShellKeyboardInputHandling.ForwardAndHandle)
        {
            throw new InvalidOperationException("Delete key down should be forwarded to the native shell.");
        }
    }

    public static void DirectionalSysKeyDownIsForwardedToNativeShell()
    {
        var handling = NativeShellKeyboardInputClassifier.Resolve(
            NativeShellKeyboardInputClassifier.WmSysKeyDown,
            new IntPtr(NativeShellKeyboardInputClassifier.VkDown));

        if (handling != NativeShellKeyboardInputHandling.ForwardAndHandle)
        {
            throw new InvalidOperationException("Directional system key down should be forwarded to the native shell.");
        }
    }

    public static void LetterKeyDownIsForwardedToNativeShell()
    {
        var handling = NativeShellKeyboardInputClassifier.Resolve(
            NativeShellKeyboardInputClassifier.WmKeyDown,
            new IntPtr(0x43));

        if (handling != NativeShellKeyboardInputHandling.ForwardAndHandle)
        {
            throw new InvalidOperationException("Letter key down should be forwarded so shell shortcuts like Ctrl+C work.");
        }
    }

    public static void FunctionKeyDownIsForwardedToNativeShell()
    {
        var handling = NativeShellKeyboardInputClassifier.Resolve(
            NativeShellKeyboardInputClassifier.WmKeyDown,
            new IntPtr(0x71));

        if (handling != NativeShellKeyboardInputHandling.ForwardAndHandle)
        {
            throw new InvalidOperationException("Function key down (F2) should be forwarded so shell rename works.");
        }
    }

    public static void BackspaceKeyDownIsNotForwardedToNativeShell()
    {
        var handling = NativeShellKeyboardInputClassifier.Resolve(
            NativeShellKeyboardInputClassifier.WmKeyDown,
            new IntPtr(NativeShellKeyboardInputClassifier.VkBack));

        if (handling != NativeShellKeyboardInputHandling.None)
        {
            throw new InvalidOperationException("Backspace is reserved for panel-level navigation and must not be forwarded.");
        }
    }

    public static void AltLeftRightSysKeyDownIsNotForwardedToNativeShell()
    {
        foreach (var key in new[]
                 {
                     NativeShellKeyboardInputClassifier.VkLeft,
                     NativeShellKeyboardInputClassifier.VkRight
                 })
        {
            var handling = NativeShellKeyboardInputClassifier.Resolve(
                NativeShellKeyboardInputClassifier.WmSysKeyDown,
                new IntPtr(key));

            if (handling != NativeShellKeyboardInputHandling.None)
            {
                throw new InvalidOperationException("Alt+Left and Alt+Right are reserved for panel-level navigation.");
            }
        }
    }

    public static void NonKeyMessagesAreNotForwardedToNativeShell()
    {
        var handling = NativeShellKeyboardInputClassifier.Resolve(
            0x0201,
            new IntPtr(NativeShellKeyboardInputClassifier.VkDown));

        if (handling != NativeShellKeyboardInputHandling.None)
        {
            throw new InvalidOperationException("Non-key messages should not be forwarded by the native shell classifier.");
        }
    }
}
