using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class ShellNativeFocusManagerTests
{
    public static void FocusNativeWindowFocusesHostWindow()
    {
        var api = new FakeShellNativeFocusApi();
        var manager = new ShellNativeFocusManager(api);
        var hostWindow = new IntPtr(100);

        if (!manager.FocusNativeWindow(hostWindow, hostWindow))
        {
            throw new InvalidOperationException("Host window should be focusable.");
        }

        if (api.FocusedWindow != hostWindow)
        {
            throw new InvalidOperationException("Host window should receive native focus.");
        }
    }

    public static void FocusNativeWindowRedirectsHostWindowToPreferredFocusWindow()
    {
        var api = new FakeShellNativeFocusApi
        {
            ChildWindow = new IntPtr(200)
        };
        var manager = new ShellNativeFocusManager(api);
        var hostWindow = new IntPtr(100);

        if (!manager.FocusNativeWindow(hostWindow, hostWindow, api.ChildWindow))
        {
            throw new InvalidOperationException("Host window should be focusable through preferred shell view window.");
        }

        if (api.FocusedWindow != api.ChildWindow)
        {
            throw new InvalidOperationException("Preferred shell view window should receive native focus instead of the host window.");
        }
    }

    public static void FocusNativeWindowSkipsPreferredFocusOutsideHost()
    {
        var api = new FakeShellNativeFocusApi
        {
            ChildWindow = new IntPtr(200)
        };
        var manager = new ShellNativeFocusManager(api);
        var hostWindow = new IntPtr(100);
        var outsidePreferredWindow = new IntPtr(300);

        if (!manager.FocusNativeWindow(hostWindow, hostWindow, outsidePreferredWindow))
        {
            throw new InvalidOperationException("Host window should still be focusable when preferred shell view window is invalid.");
        }

        if (api.FocusedWindow != hostWindow)
        {
            throw new InvalidOperationException("Invalid preferred shell view window must not receive native focus.");
        }
    }

    public static void FocusNativeWindowFocusesChildWindow()
    {
        var api = new FakeShellNativeFocusApi
        {
            ChildWindow = new IntPtr(200)
        };
        var manager = new ShellNativeFocusManager(api);
        var hostWindow = new IntPtr(100);

        if (!manager.FocusNativeWindow(hostWindow, api.ChildWindow))
        {
            throw new InvalidOperationException("Child window should be focusable.");
        }

        if (api.FocusedWindow != api.ChildWindow)
        {
            throw new InvalidOperationException("Child window should receive native focus.");
        }
    }

    public static void FocusNativeWindowSkipsOutsideWindow()
    {
        var api = new FakeShellNativeFocusApi
        {
            ChildWindow = new IntPtr(200)
        };
        var manager = new ShellNativeFocusManager(api);
        var outsideWindow = new IntPtr(300);

        if (manager.FocusNativeWindow(new IntPtr(100), outsideWindow))
        {
            throw new InvalidOperationException("Outside windows must not receive native focus.");
        }

        if (api.FocusedWindow != IntPtr.Zero)
        {
            throw new InvalidOperationException("Outside windows should not be focused.");
        }
    }

    public static void DefaultNativeApiResolvesWin32EntryPoints()
    {
        var manager = new ShellNativeFocusManager();

        _ = manager.ContainsNativeWindow(new IntPtr(1), new IntPtr(2));
        _ = manager.FocusNativeWindow(new IntPtr(1), new IntPtr(1));
    }

    private sealed class FakeShellNativeFocusApi : IShellNativeFocusApi
    {
        public IntPtr ChildWindow { get; set; }

        public IntPtr FocusedWindow { get; private set; }

        public bool IsChild(IntPtr parentWindow, IntPtr childWindow)
        {
            return childWindow == ChildWindow;
        }

        public void SetFocus(IntPtr window)
        {
            FocusedWindow = window;
        }
    }
}
