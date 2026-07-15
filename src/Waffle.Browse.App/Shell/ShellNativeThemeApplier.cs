using System.Runtime.InteropServices;
using Waffle.Browse.App.Settings;

namespace Waffle.Browse.App.Shell;

public interface INativeThemeApplier
{
    void Apply(IntPtr root, UiTheme theme);
}

public interface IShellNativeThemeApi
{
    IReadOnlyList<IntPtr> EnumerateSelfAndDescendants(IntPtr root);

    void SetPreferredAppMode(bool allowDarkMode);

    void RefreshImmersiveColorPolicy();

    void SetWindowTheme(IntPtr window, string themeName);

    void AllowDarkModeForWindow(IntPtr window, bool enabled);

    void SetImmersiveDarkMode(IntPtr window, bool enabled);

    void RedrawWindow(IntPtr window);

    void NotifyThemeChanged(IntPtr window);

    void InvalidateWindow(IntPtr window);
}

public sealed class ShellNativeThemeApplier : INativeThemeApplier
{
    private const string DarkExplorerThemeName = "DarkMode_Explorer";
    private const string LightExplorerThemeName = "Explorer";

    private readonly IShellNativeThemeApi nativeApi;
    private readonly bool includeDescendants;

    public ShellNativeThemeApplier()
        : this(new WindowsShellNativeThemeApi())
    {
    }

    public ShellNativeThemeApplier(bool includeDescendants)
        : this(new WindowsShellNativeThemeApi(), includeDescendants)
    {
    }

    public ShellNativeThemeApplier(IShellNativeThemeApi nativeApi, bool includeDescendants = true)
    {
        this.nativeApi = nativeApi;
        this.includeDescendants = includeDescendants;
    }

    public void Apply(IntPtr root, UiTheme theme)
    {
        if (root == IntPtr.Zero)
        {
            return;
        }

        var themeName = theme == UiTheme.Dark ? DarkExplorerThemeName : LightExplorerThemeName;
        var immersiveDarkModeEnabled = theme == UiTheme.Dark;

        TryApply(() => nativeApi.SetPreferredAppMode(immersiveDarkModeEnabled));
        TryApply(nativeApi.RefreshImmersiveColorPolicy);

        var windows = includeDescendants
            ? nativeApi.EnumerateSelfAndDescendants(root)
            : [root];

        foreach (var window in windows)
        {
            if (window == IntPtr.Zero)
            {
                continue;
            }

            TryApply(() => nativeApi.AllowDarkModeForWindow(window, immersiveDarkModeEnabled));
            TryApply(() => nativeApi.SetWindowTheme(window, themeName));
            TryApply(() => nativeApi.SetImmersiveDarkMode(window, immersiveDarkModeEnabled));
            TryApply(() => nativeApi.RedrawWindow(window));
            if (!immersiveDarkModeEnabled)
            {
                TryApply(() => nativeApi.NotifyThemeChanged(window));
                TryApply(() => nativeApi.InvalidateWindow(window));
            }
        }
    }

    private static void TryApply(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException)
        {
            // Shell dark mode support is best-effort and varies by Windows version.
        }
    }

    private sealed class WindowsShellNativeThemeApi : IShellNativeThemeApi
    {
        private const int DwmWindowAttributeUseImmersiveDarkMode = 20;
        private const int DwmWindowAttributeUseImmersiveDarkModeBefore20H1 = 19;
        private const uint WmThemeChanged = 0x031A;
        private const uint SendMessageTimeoutAbortIfHung = 0x0002;
        private const uint ThemeMessageTimeoutMilliseconds = 100;
        private const uint RedrawFlags = 0x0001 | 0x0080 | 0x0100 | 0x0400;

        public IReadOnlyList<IntPtr> EnumerateSelfAndDescendants(IntPtr root)
        {
            var windows = new List<IntPtr> { root };
            EnumChildWindows(root, (window, _) =>
            {
                windows.Add(window);
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        public void SetPreferredAppMode(bool allowDarkMode)
        {
            _ = NativeSetPreferredAppMode(allowDarkMode
                ? PreferredAppMode.ForceDark
                : PreferredAppMode.ForceLight);
        }

        public void RefreshImmersiveColorPolicy()
        {
            NativeRefreshImmersiveColorPolicyState();
        }

        public void SetWindowTheme(IntPtr window, string themeName)
        {
            _ = NativeSetWindowTheme(window, themeName, null);
        }

        public void AllowDarkModeForWindow(IntPtr window, bool enabled)
        {
            _ = NativeAllowDarkModeForWindow(window, enabled);
        }

        public void SetImmersiveDarkMode(IntPtr window, bool enabled)
        {
            var value = enabled ? 1 : 0;
            var result = DwmSetWindowAttribute(
                window,
                DwmWindowAttributeUseImmersiveDarkMode,
                ref value,
                Marshal.SizeOf<int>());

            if (result != 0)
            {
                _ = DwmSetWindowAttribute(
                    window,
                    DwmWindowAttributeUseImmersiveDarkModeBefore20H1,
                    ref value,
                    Marshal.SizeOf<int>());
            }
        }

        public void RedrawWindow(IntPtr window)
        {
            _ = NativeRedrawWindow(window, IntPtr.Zero, IntPtr.Zero, RedrawFlags);
        }

        public void NotifyThemeChanged(IntPtr window)
        {
            _ = SendMessageTimeout(
                window,
                WmThemeChanged,
                UIntPtr.Zero,
                IntPtr.Zero,
                SendMessageTimeoutAbortIfHung,
                ThemeMessageTimeoutMilliseconds,
                out _);
        }

        public void InvalidateWindow(IntPtr window)
        {
            _ = InvalidateRect(window, IntPtr.Zero, erase: true);
            _ = UpdateWindow(window);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern PreferredAppMode NativeSetPreferredAppMode(PreferredAppMode appMode);

        [DllImport("uxtheme.dll", EntryPoint = "#104")]
        private static extern void NativeRefreshImmersiveColorPolicyState();

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeAllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "SetWindowTheme", CharSet = CharSet.Unicode)]
        private static extern int NativeSetWindowTheme(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszSubAppName,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszSubIdList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", EntryPoint = "RedrawWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeRedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            UIntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateWindow(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private enum PreferredAppMode
        {
            Default,
            AllowDark,
            ForceDark,
            ForceLight,
            Max
        }
    }
}
