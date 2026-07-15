using Waffle.Browse.App.Settings;
using Waffle.Browse.App.Shell;

namespace Waffle.Browse.App.Tests.Shell;

internal static class WindowNativeThemeApplierTests
{
    public static void ApplyThemesResolvedWindowHandle()
    {
        var handle = new IntPtr(500);
        var nativeThemeApplier = new RecordingNativeThemeApplier();
        var applier = new WindowNativeThemeApplier(nativeThemeApplier);

        applier.Apply(handle, UiTheme.Dark);

        nativeThemeApplier.AssertApplied(handle, UiTheme.Dark);
    }

    public static void ApplySkipsWindowWithoutHandle()
    {
        var nativeThemeApplier = new RecordingNativeThemeApplier();
        var applier = new WindowNativeThemeApplier(nativeThemeApplier);

        applier.Apply(IntPtr.Zero, UiTheme.Dark);

        nativeThemeApplier.AssertNotApplied();
    }

    private sealed class RecordingNativeThemeApplier : INativeThemeApplier
    {
        private readonly List<(IntPtr Root, UiTheme Theme)> calls = [];

        public void Apply(IntPtr root, UiTheme theme)
        {
            calls.Add((root, theme));
        }

        public void AssertApplied(IntPtr root, UiTheme theme)
        {
            if (!calls.Contains((root, theme)))
            {
                throw new InvalidOperationException($"Expected native theme apply for {root} with {theme}.");
            }
        }

        public void AssertNotApplied()
        {
            if (calls.Count != 0)
            {
                throw new InvalidOperationException("Expected no native theme apply calls.");
            }
        }
    }
}
