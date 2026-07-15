namespace Waffle.Browse.Core.Docking;

public sealed record DockGridState
{
    public DockNode Root { get; init; } = new DockLeaf(Guid.Empty);

    public static DockGridState Single(Guid panelId)
    {
        return new DockGridState { Root = new DockLeaf(panelId) };
    }
}
