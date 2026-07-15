using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Settings;

public sealed record UiSettings
{
    public UiTheme Theme { get; init; } = UiTheme.Light;

    public SearchScope LastSelectedSearchScope { get; init; } = SearchScope.GlobalIndex;
}
