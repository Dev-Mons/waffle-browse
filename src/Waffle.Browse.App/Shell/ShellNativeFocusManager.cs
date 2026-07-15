using System.Runtime.InteropServices;

namespace Waffle.Browse.App.Shell;

public interface IShellNativeFocusApi
{
    bool IsChild(IntPtr parentWindow, IntPtr childWindow);

    void SetFocus(IntPtr window);
}

public sealed class ShellNativeFocusManager
{
    private readonly IShellNativeFocusApi nativeApi;

    public ShellNativeFocusManager()
        : this(new WindowsShellNativeFocusApi())
    {
    }

    public ShellNativeFocusManager(IShellNativeFocusApi nativeApi)
    {
        this.nativeApi = nativeApi;
    }

    public bool ContainsNativeWindow(IntPtr hostWindow, IntPtr window)
    {
        return window != IntPtr.Zero
            && hostWindow != IntPtr.Zero
            && (window == hostWindow || nativeApi.IsChild(hostWindow, window));
    }

    public bool FocusNativeWindow(IntPtr hostWindow, IntPtr window)
    {
        return FocusNativeWindow(hostWindow, window, IntPtr.Zero);
    }

    public bool FocusNativeWindow(IntPtr hostWindow, IntPtr window, IntPtr preferredFocusWindow)
    {
        if (!ContainsNativeWindow(hostWindow, window))
        {
            return false;
        }

        nativeApi.SetFocus(ResolveFocusWindow(hostWindow, window, preferredFocusWindow));
        return true;
    }

    private IntPtr ResolveFocusWindow(IntPtr hostWindow, IntPtr window, IntPtr preferredFocusWindow)
    {
        return window == hostWindow && ContainsNativeWindow(hostWindow, preferredFocusWindow)
            ? preferredFocusWindow
            : window;
    }

    private sealed class WindowsShellNativeFocusApi : IShellNativeFocusApi
    {
        public bool IsChild(IntPtr parentWindow, IntPtr childWindow)
        {
            return NativeIsChild(parentWindow, childWindow);
        }

        public void SetFocus(IntPtr window)
        {
            _ = NativeSetFocus(window);
        }

        [DllImport("user32.dll", EntryPoint = "IsChild")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "SetFocus", SetLastError = true)]
        private static extern IntPtr NativeSetFocus(IntPtr hWnd);
    }
}
