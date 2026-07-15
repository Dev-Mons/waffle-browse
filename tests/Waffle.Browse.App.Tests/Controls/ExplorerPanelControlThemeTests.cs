using System.Windows;
using System.Windows.Controls;
using System.IO;
using Waffle.Browse.App.Controls;
using Waffle.Browse.App.Settings;
using Waffle.Browse.App.Theming;
using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.App.Tests.Controls;

internal static class ExplorerPanelControlThemeTests
{
    public static void ApplyThemeRecreatesShellHostWhenThemeChanges()
    {
        RunOnStaThread(() =>
        {
            ApplyTestResources();
            var panel = CreatePanel();
            var control = new ExplorerPanelControl(panel, isActive: true);
            var container = (ContentControl)control.FindName("ShellHostContainer");
            var initialHost = container.Content;

            control.ApplyTheme(UiTheme.Dark);

            if (ReferenceEquals(initialHost, container.Content))
            {
                throw new InvalidOperationException("Shell host should be recreated when the theme changes.");
            }
        });
    }

    public static void ApplyThemeKeepsShellHostWhenThemeStaysSame()
    {
        RunOnStaThread(() =>
        {
            ApplyTestResources();
            var panel = CreatePanel();
            var control = new ExplorerPanelControl(panel, isActive: true);
            var container = (ContentControl)control.FindName("ShellHostContainer");

            control.ApplyTheme(UiTheme.Dark);
            var darkHost = container.Content;
            control.ApplyTheme(UiTheme.Dark);

            if (!ReferenceEquals(darkHost, container.Content))
            {
                throw new InvalidOperationException("Shell host should not be recreated when the theme stays the same.");
            }
        });
    }

    private static PanelState CreatePanel()
    {
        var tab = new TabState
        {
            Title = "Temp",
            CurrentPath = Path.GetTempPath(),
            LocationKind = TabLocationKind.Folder
        };

        return new PanelState
        {
            IsVisible = true,
            ActiveTabId = tab.Id,
            Tabs = [tab]
        };
    }

    private static void ApplyTestResources()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            var app = new global::Waffle.Browse.App.App();
            app.InitializeComponent();
            resources = app.Resources;
        }

        AppThemeResources.Apply(resources, UiTheme.Light);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }
}
