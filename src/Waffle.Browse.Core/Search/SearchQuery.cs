namespace Waffle.Browse.Core.Search;

public sealed record SearchQuery(string Text, SearchScope Scope, int MaxResults);
