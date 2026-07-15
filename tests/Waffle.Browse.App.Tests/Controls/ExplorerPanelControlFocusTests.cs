using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waffle.Browse.App.Controls;
using Waffle.Browse.App.Settings;
using Waffle.Browse.App.Theming;
using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.App.Tests.Controls;

internal static class ExplorerPanelControlFocusTests
{
    public static void ShellHostAreaClickDoesNotRequestWpfPanelFocus()
    {
        RunOnStaThread(() =>
        {
            ApplyTestResources();
            var control = new ExplorerPanelControl(CreatePanel(), isActive: true);
            var shellHostArea = (Grid)control.FindName("ShellHostArea");
            var focusRequested = false;
            control.PanelFocusRequested += (_, _) => focusRequested = true;

            shellHostArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent,
                Source = shellHostArea
            });

            if (focusRequested)
            {
                throw new InvalidOperationException("Shell clicks should leave focus handling to the native Explorer host.");
            }
        });
    }

    public static void ActiveStateUpdateDoesNotChangeLayoutThickness()
    {
        RunOnStaThread(() =>
        {
            ApplyTestResources();
            var panel = CreatePanel();
            var control = new ExplorerPanelControl(panel, isActive: false);
            var rootBorder = (Border)control.FindName("RootBorder");
            var initialThickness = rootBorder.BorderThickness;

            control.UpdatePanel(panel, isActive: true);

            if (rootBorder.BorderThickness != initialThickness)
            {
                throw new InvalidOperationException("Activating a panel must not resize the native shell host area.");
            }
        });
    }

    public static void ActiveStateUpdateDoesNotRewritePanelContent()
    {
        RunOnStaThread(() =>
        {
            ApplyTestResources();
            var panel = CreatePanel();
            var control = new ExplorerPanelControl(panel, isActive: false);
            var addressBox = (TextBox)control.FindName("AddressBox");
            addressBox.Text = "user is editing";

            control.UpdatePanel(panel, isActive: true);

            if (addressBox.Text != "user is editing")
            {
                throw new InvalidOperationException("Active-only updates must not rewrite panel content or disturb shell interaction.");
            }
        });
    }

    public static void TabsListBoxDoesNotAcceptKeyboardFocus()
    {
        RunOnStaThread(() =>
        {
            ApplyTestResources();
            var control = new ExplorerPanelControl(CreatePanel(), isActive: true);
            var tabsListBox = (ListBox)control.FindName("TabsListBox");

            if (tabsListBox.Focusable)
            {
                throw new InvalidOperationException("Tabs list should not keep keyboard focus after shell clicks.");
            }

            var itemFocusableSetter = tabsListBox.ItemContainerStyle.Setters
                .OfType<Setter>()
                .FirstOrDefault(setter => setter.Property == UIElement.FocusableProperty);
            if (!Equals(itemFocusableSetter?.Value, false))
            {
                throw new InvalidOperationException("Tab items should not receive keyboard focus.");
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
