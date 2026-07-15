using Waffle.Browse.App.Settings;

namespace Waffle.Browse.App.Shell;

public sealed class WindowNativeThemeApplier
{
    private readonly INativeThemeApplier nativeThemeApplier;

    public WindowNativeThemeApplier()
        : this(new ShellNativeThemeApplier(includeDescendants: false))
    {
    }

    public WindowNativeThemeApplier(INativeThemeApplier nativeThemeApplier)
    {
        this.nativeThemeApplier = nativeThemeApplier;
    }

    public void Apply(IntPtr windowHandle, UiTheme theme)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        nativeThemeApplier.Apply(windowHandle, theme);
    }
}
