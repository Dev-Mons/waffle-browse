namespace Waffle.Browse.Core.Docking;

public sealed record DockDropOptions(
    int CurrentVisiblePanelCount,
    int MaxVisiblePanels = DockLayoutService.MaxPanels,
    double EdgeThresholdRatio = 0.10,
    DockOrientation PreferredOrientation = DockOrientation.Horizontal,
    bool SplitOnDragAndDrop = true);
