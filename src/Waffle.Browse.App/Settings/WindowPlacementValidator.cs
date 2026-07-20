using System.Windows;

namespace Waffle.Browse.App.Settings;

internal static class WindowPlacementValidator
{
    private const double MinimumVisibleLength = 100;

    public static bool TryNormalize(
        WindowPlacementSettings? placement,
        Rect virtualScreen,
        out Rect bounds)
    {
        bounds = Rect.Empty;
        if (placement is null
            || !IsFinite(placement.Left)
            || !IsFinite(placement.Top)
            || !IsFinite(placement.Width)
            || !IsFinite(placement.Height)
            || placement.Width <= 0
            || placement.Height <= 0
            || virtualScreen.IsEmpty
            || virtualScreen.Width <= 0
            || virtualScreen.Height <= 0)
        {
            return false;
        }

        var width = Math.Min(placement.Width, virtualScreen.Width);
        var height = Math.Min(placement.Height, virtualScreen.Height);
        var visibleWidth = Math.Min(MinimumVisibleLength, width);
        var visibleHeight = Math.Min(MinimumVisibleLength, height);
        var left = Math.Clamp(
            placement.Left,
            virtualScreen.Left - width + visibleWidth,
            virtualScreen.Right - visibleWidth);
        var top = Math.Clamp(
            placement.Top,
            virtualScreen.Top,
            virtualScreen.Bottom - visibleHeight);

        bounds = new Rect(left, top, width, height);
        return true;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
