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

    public static void UiSettingsStoreSavesThemeWithoutSearchScope()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "waffle-browse-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new UiSettingsStore(tempFile);

        store.Save(new UiSettings { Theme = UiTheme.Dark });
        var savedJson = File.ReadAllText(tempFile);

        if (savedJson.Contains("LastSelectedSearchScope", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Search scope should not be persisted after the toolbar selector is removed.");
        }
    }
}
