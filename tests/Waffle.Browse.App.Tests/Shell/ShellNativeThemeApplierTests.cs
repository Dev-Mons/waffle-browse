using Waffle.Browse.App.Settings;
using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class ShellNativeThemeApplierTests
{
    public static void ApplyDarkThemeThemesRootAndDescendants()
    {
        var root = new IntPtr(100);
        var child = new IntPtr(200);
        var api = new RecordingNativeThemeApi([root, child]);
        var applier = new ShellNativeThemeApplier(api);

        applier.Apply(root, UiTheme.Dark);

        api.AssertProcessDarkMode(true);
        api.AssertThemeApplied(root, "DarkMode_Explorer", true);
        api.AssertThemeApplied(child, "DarkMode_Explorer", true);
    }

    public static void ApplyLightThemeRestoresExplorerTheme()
    {
        var root = new IntPtr(300);
        var child = new IntPtr(400);
        var api = new RecordingNativeThemeApi([root, child]);
        var applier = new ShellNativeThemeApplier(api);

        applier.Apply(root, UiTheme.Light);

        api.AssertProcessDarkMode(false);
        api.AssertThemeApplied(root, "Explorer", false);
        api.AssertThemeApplied(child, "Explorer", false);
    }

    public static void ApplyLightThemeForcesShellWindowsToRefreshTheme()
    {
        var root = new IntPtr(300);
        var api = new RecordingNativeThemeApi([root]);
        var applier = new ShellNativeThemeApplier(api);

        applier.Apply(root, UiTheme.Light);

        api.AssertThemeRefreshForced(root);
    }

    public static void ApplyCanSkipDescendantShellWindows()
    {
        var root = new IntPtr(500);
        var child = new IntPtr(600);
        var api = new RecordingNativeThemeApi([root, child]);
        var applier = new ShellNativeThemeApplier(api, includeDescendants: false);

        applier.Apply(root, UiTheme.Dark);

        api.AssertThemeApplied(root, "DarkMode_Explorer", true);
        api.AssertThemeNotApplied(child);
    }

    private sealed class RecordingNativeThemeApi : IShellNativeThemeApi
    {
        private readonly IReadOnlyList<IntPtr> windows;
        private readonly List<bool> preferredAppModeCalls = [];
        private readonly List<bool> refreshCalls = [];
        private readonly List<(IntPtr Window, string ThemeName)> themeCalls = [];
        private readonly List<(IntPtr Window, bool Enabled)> allowDarkModeCalls = [];
        private readonly List<(IntPtr Window, bool Enabled)> immersiveCalls = [];
        private readonly List<IntPtr> redrawCalls = [];
        private readonly List<IntPtr> themeChangedCalls = [];
        private readonly List<IntPtr> invalidateCalls = [];

        public RecordingNativeThemeApi(IReadOnlyList<IntPtr> windows)
        {
            this.windows = windows;
        }

        public IReadOnlyList<IntPtr> EnumerateSelfAndDescendants(IntPtr root)
        {
            return windows;
        }

        public void SetPreferredAppMode(bool allowDarkMode)
        {
            preferredAppModeCalls.Add(allowDarkMode);
        }

        public void RefreshImmersiveColorPolicy()
        {
            refreshCalls.Add(true);
        }

        public void SetWindowTheme(IntPtr window, string themeName)
        {
            themeCalls.Add((window, themeName));
        }

        public void AllowDarkModeForWindow(IntPtr window, bool enabled)
        {
            allowDarkModeCalls.Add((window, enabled));
        }

        public void SetImmersiveDarkMode(IntPtr window, bool enabled)
        {
            immersiveCalls.Add((window, enabled));
        }

        public void RedrawWindow(IntPtr window)
        {
            redrawCalls.Add(window);
        }

        public void NotifyThemeChanged(IntPtr window)
        {
            themeChangedCalls.Add(window);
        }

        public void InvalidateWindow(IntPtr window)
        {
            invalidateCalls.Add(window);
        }

        public void AssertProcessDarkMode(bool expected)
        {
            if (!preferredAppModeCalls.Contains(expected))
            {
                throw new InvalidOperationException($"Expected SetPreferredAppMode({expected}).");
            }

            if (refreshCalls.Count == 0)
            {
                throw new InvalidOperationException("Expected RefreshImmersiveColorPolicy().");
            }
        }

        public void AssertThemeApplied(IntPtr window, string themeName, bool immersiveEnabled)
        {
            if (!allowDarkModeCalls.Contains((window, immersiveEnabled)))
            {
                throw new InvalidOperationException($"Expected AllowDarkModeForWindow({window}, {immersiveEnabled}).");
            }

            if (!themeCalls.Contains((window, themeName)))
            {
                throw new InvalidOperationException($"Expected SetWindowTheme({window}, {themeName}).");
            }

            if (!immersiveCalls.Contains((window, immersiveEnabled)))
            {
                throw new InvalidOperationException($"Expected SetImmersiveDarkMode({window}, {immersiveEnabled}).");
            }

            if (!redrawCalls.Contains(window))
            {
                throw new InvalidOperationException($"Expected RedrawWindow({window}).");
            }
        }

        public void AssertThemeRefreshForced(IntPtr window)
        {
            if (!themeChangedCalls.Contains(window))
            {
                throw new InvalidOperationException($"Expected NotifyThemeChanged({window}).");
            }

            if (!invalidateCalls.Contains(window))
            {
                throw new InvalidOperationException($"Expected InvalidateWindow({window}).");
            }
        }

        public void AssertThemeNotApplied(IntPtr window)
        {
            if (allowDarkModeCalls.Any(call => call.Window == window)
                || themeCalls.Any(call => call.Window == window)
                || immersiveCalls.Any(call => call.Window == window)
                || redrawCalls.Contains(window))
            {
                throw new InvalidOperationException($"Expected no theme calls for {window}.");
            }
        }
    }
}
