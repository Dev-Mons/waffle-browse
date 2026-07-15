using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockLayoutRenderInvalidationTests
{
    public static void NavigationChangesDoNotRequireLayoutRender()
    {
        var service = new DockLayoutService();
        var before = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 2);
        var panel = before.VisiblePanels[0];

        var after = service.NavigateTo(before, panel.Id, @"C:\Changed");

        TestAssert.False(
            DockLayoutRenderInvalidation.RequiresLayoutRender(before, after),
            "Changing a panel path should update existing panel controls without recreating the layout");
    }

    public static void VisiblePanelChangesRequireLayoutRender()
    {
        var service = new DockLayoutService();
        var before = service.CreateDefault(@"C:\");
        var after = service.SetVisiblePanelCount(before, 2);

        TestAssert.True(
            DockLayoutRenderInvalidation.RequiresLayoutRender(before, after),
            "Changing visible panels should rebuild the workspace layout");
    }

    public static void ActivePanelChangesDoNotRequireLayoutRender()
    {
        var service = new DockLayoutService();
        var before = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 2);
        var after = before with { ActivePanelId = before.VisiblePanels[1].Id };

        TestAssert.False(
            DockLayoutRenderInvalidation.RequiresLayoutRender(before, after),
            "Changing active panel should refresh panel chrome without recreating shell hosts");
    }
}
