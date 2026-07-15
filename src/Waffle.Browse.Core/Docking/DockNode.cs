using System.Text.Json.Serialization;

namespace Waffle.Browse.Core.Docking;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(DockLeaf), "leaf")]
[JsonDerivedType(typeof(DockSplit), "split")]
public abstract record DockNode;

public sealed record DockLeaf(Guid PanelId) : DockNode;

public sealed record DockSplit(
    DockOrientation Orientation,
    DockNode First,
    DockNode Second,
    double Ratio = 0.5) : DockNode;
