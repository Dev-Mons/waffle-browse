using System.Text.Json.Serialization;

namespace Waffle.Browse.Core.Docking;

public sealed record PanelState
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public bool IsVisible { get; init; }

    public Guid? ActiveTabId { get; init; }

    public List<TabState> Tabs { get; init; } = [];

    [JsonIgnore]
    public TabState? ActiveTab => Tabs.FirstOrDefault(tab => tab.Id == ActiveTabId) ?? Tabs.FirstOrDefault();
}
