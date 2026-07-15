namespace Waffle.Browse.Core.Search;

public enum SearchProviderStatusKind
{
    Ready,
    Initializing,
    Rebuilding,
    Unavailable,
    AccessDenied,
    CorruptIndex,
    Error
}

public sealed record SearchProviderStatus(
    SearchProviderStatusKind Kind,
    string Message,
    bool CanSearch)
{
    public static SearchProviderStatus Ready(string message) =>
        new(SearchProviderStatusKind.Ready, message, true);
}
