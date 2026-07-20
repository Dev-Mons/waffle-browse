using System.IO;
using Waffle.Browse.App.Settings;

namespace Waffle.Browse.App.Tests.Settings;

internal static class UiSettingsStoreTests
{
    public static void UiSettingsStoreRoundTripsTheme()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new UiSettingsStore(tempFile);

        store.Save(new UiSettings { Theme = UiTheme.Dark });
        var loaded = store.Load();

        if (loaded.Theme != UiTheme.Dark)
        {
            throw new InvalidOperationException($"Expected dark theme, got {loaded.Theme}.");
        }
    }

    public static void UiSettingsStoreRoundTripsIndexRoots()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new UiSettingsStore(tempFile);

        store.Save(new UiSettings
        {
            Theme = UiTheme.Dark,
            IndexedLocalRoots = [@"D:\Projects"],
            IndexedNetworkRoots = [@"\\server\share"]
        });
        var loaded = store.Load();

        if (loaded.IndexedLocalRoots.Count != 1
            || loaded.IndexedLocalRoots[0] != @"D:\Projects")
        {
            throw new InvalidOperationException("Explicitly configured local index roots should be persisted.");
        }

        if (loaded.IndexedNetworkRoots.Count != 1
            || loaded.IndexedNetworkRoots[0] != @"\\server\share")
        {
            throw new InvalidOperationException("Explicitly configured network index roots should be persisted.");
        }
    }

    public static void UiSettingsStoreRoundTripsWindowPlacement()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new UiSettingsStore(tempFile);

        store.Save(new UiSettings
        {
            WindowPlacement = new WindowPlacementSettings
            {
                Left = -1200,
                Top = 80,
                Width = 1100,
                Height = 720,
                IsMaximized = true
            }
        });
        var loaded = store.Load().WindowPlacement;

        if (loaded is null
            || loaded.Left != -1200
            || loaded.Top != 80
            || loaded.Width != 1100
            || loaded.Height != 720
            || !loaded.IsMaximized)
        {
            throw new InvalidOperationException("Window placement should be persisted without losing multi-monitor coordinates or maximized state.");
        }
    }

    public static void WindowPlacementIsMovedBackOntoVirtualScreen()
    {
        var placement = new WindowPlacementSettings
        {
            Left = 5000,
            Top = 3000,
            Width = 1200,
            Height = 800
        };

        if (!WindowPlacementValidator.TryNormalize(placement, new System.Windows.Rect(0, 0, 1920, 1080), out var bounds))
        {
            throw new InvalidOperationException("Valid window placement should be restorable.");
        }

        if (bounds.Left > 1820 || bounds.Top > 980)
        {
            throw new InvalidOperationException("At least 100 pixels of an off-screen window should be moved back onto the virtual screen.");
        }
    }

}
