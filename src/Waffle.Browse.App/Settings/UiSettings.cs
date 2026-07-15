namespace Waffle.Browse.App.Settings;

public sealed record UiSettings
{
    public UiTheme Theme { get; init; } = UiTheme.Light;
}
