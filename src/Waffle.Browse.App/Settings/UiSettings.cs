namespace Waffle.Browse.App.Settings;

public sealed record UiSettings
{
    public UiTheme Theme { get; init; } = UiTheme.Light;

    public WindowPlacementSettings? WindowPlacement { get; init; }

    public IReadOnlyList<string> IndexedLocalRoots { get; init; } = [];

    public IReadOnlyList<string> IndexedNetworkRoots { get; init; } = [];
}
