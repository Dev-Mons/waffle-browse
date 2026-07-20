namespace Waffle.Browse.App.Settings;

public sealed record WindowPlacementSettings
{
    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public bool IsMaximized { get; init; }
}
