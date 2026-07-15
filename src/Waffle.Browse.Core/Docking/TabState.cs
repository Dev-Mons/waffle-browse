namespace Waffle.Browse.Core.Docking;

public sealed record TabState
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; init; } = string.Empty;

    public string CurrentPath { get; init; } = string.Empty;

    public TabLocationKind LocationKind { get; init; }

    public string? SearchQuery { get; init; }

    public string? SearchOriginPath { get; init; }

    public List<string> SearchRoots { get; init; } = [];

    public List<string> BackStack { get; init; } = [];

    public List<string> ForwardStack { get; init; } = [];
}
