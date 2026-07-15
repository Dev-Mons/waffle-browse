using System.Text.Json.Serialization;

namespace Waffle.Browse.Core.Docking;

public sealed record DockLayoutState
{
    public DockLayoutKind LayoutKind { get; init; } = DockLayoutKind.OneByOne;

    public Guid? ActivePanelId { get; init; }

    public DockGridState? Grid { get; init; }

    public List<PanelState> Panels { get; init; } = [];

    [JsonIgnore]
    public List<PanelState> VisiblePanels
    {
        get
        {
            var visiblePanels = Panels.Where(panel => panel.IsVisible).ToList();
            if (Grid is null)
            {
                return visiblePanels;
            }

            var visibleById = visiblePanels.ToDictionary(panel => panel.Id);
            var ordered = new DockGridService()
                .GetLeafPanelIds(Grid)
                .Where(visibleById.ContainsKey)
                .Select(id => visibleById[id])
                .ToList();

            return ordered.Count == visiblePanels.Count ? ordered : visiblePanels;
        }
    }

    public PanelState FindPanel(Guid panelId)
    {
        return Panels.First(panel => panel.Id == panelId);
    }
}
