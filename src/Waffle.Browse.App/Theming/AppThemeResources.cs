using System.Windows;
using System.Windows.Media;
using Waffle.Browse.App.Settings;

namespace Waffle.Browse.App.Theming;

public static class AppThemeResources
{
    public const string WindowBackgroundBrushKey = "WindowBackgroundBrush";
    public const string PanelBackgroundBrushKey = "PanelBackgroundBrush";
    public const string PanelBorderBrushKey = "PanelBorderBrush";
    public const string ActivePanelBorderBrushKey = "ActivePanelBorderBrush";
    public const string ToolbarBackgroundBrushKey = "ToolbarBackgroundBrush";
    public const string PanelHeaderBackgroundBrushKey = "PanelHeaderBackgroundBrush";
    public const string DividerBrushKey = "DividerBrush";
    public const string SubtleTextBrushKey = "SubtleTextBrush";
    public const string PrimaryTextBrushKey = "PrimaryTextBrush";
    public const string ControlBackgroundBrushKey = "ControlBackgroundBrush";
    public const string ControlBorderBrushKey = "ControlBorderBrush";
    public const string ControlForegroundBrushKey = "ControlForegroundBrush";
    public const string ControlHoverBackgroundBrushKey = "ControlHoverBackgroundBrush";
    public const string ControlPressedBackgroundBrushKey = "ControlPressedBackgroundBrush";
    public const string TabHoverBackgroundBrushKey = "TabHoverBackgroundBrush";
    public const string TabSelectedBackgroundBrushKey = "TabSelectedBackgroundBrush";
    public const string TabSelectedForegroundBrushKey = "TabSelectedForegroundBrush";
    public const string ShellHostBackgroundBrushKey = "ShellHostBackgroundBrush";
    public const string DockPreviewBackgroundBrushKey = "DockPreviewBackgroundBrush";
    public const string DockPreviewBorderBrushKey = "DockPreviewBorderBrush";

    private static readonly AppThemePalette LightPalette = new(
        WindowBackground: "#F5F0E7",
        PanelBackground: "#FFFCF7",
        PanelBorder: "#D8CFC1",
        ActivePanelBorder: "#B86F08",
        ToolbarBackground: "#F0E9DE",
        PanelHeaderBackground: "#F7F1E8",
        Divider: "#E7DFD4",
        SubtleText: "#756D62",
        PrimaryText: "#25211D",
        ControlBackground: "#FFFFFF",
        ControlBorder: "#D8CFC1",
        ControlForeground: "#25211D",
        ControlHoverBackground: "#F4ECDF",
        ControlPressedBackground: "#EADCC8",
        TabHoverBackground: "#F4ECDF",
        TabSelectedBackground: "#F8E6B8",
        TabSelectedForeground: "#5A3505",
        ShellHostBackground: "#FFFFFF",
        DockPreviewBackground: "#55B86F08",
        DockPreviewBorder: "#CCB86F08");

    private static readonly AppThemePalette DarkPalette = new(
        WindowBackground: "#1C1A17",
        PanelBackground: "#24211C",
        PanelBorder: "#494136",
        ActivePanelBorder: "#D99018",
        ToolbarBackground: "#211F1B",
        PanelHeaderBackground: "#28251F",
        Divider: "#38332C",
        SubtleText: "#BFB5A6",
        PrimaryText: "#F5EFE4",
        ControlBackground: "#2E2A24",
        ControlBorder: "#494136",
        ControlForeground: "#F5EFE4",
        ControlHoverBackground: "#383229",
        ControlPressedBackground: "#453A2A",
        TabHoverBackground: "#383229",
        TabSelectedBackground: "#4A3516",
        TabSelectedForeground: "#FFD98A",
        ShellHostBackground: "#171612",
        DockPreviewBackground: "#55D99018",
        DockPreviewBorder: "#CCD99018");

    public static void Apply(ResourceDictionary resources, UiTheme theme)
    {
        var palette = theme == UiTheme.Dark ? DarkPalette : LightPalette;

        SetBrush(resources, WindowBackgroundBrushKey, palette.WindowBackground);
        SetBrush(resources, PanelBackgroundBrushKey, palette.PanelBackground);
        SetBrush(resources, PanelBorderBrushKey, palette.PanelBorder);
        SetBrush(resources, ActivePanelBorderBrushKey, palette.ActivePanelBorder);
        SetBrush(resources, ToolbarBackgroundBrushKey, palette.ToolbarBackground);
        SetBrush(resources, PanelHeaderBackgroundBrushKey, palette.PanelHeaderBackground);
        SetBrush(resources, DividerBrushKey, palette.Divider);
        SetBrush(resources, SubtleTextBrushKey, palette.SubtleText);
        SetBrush(resources, PrimaryTextBrushKey, palette.PrimaryText);
        SetBrush(resources, ControlBackgroundBrushKey, palette.ControlBackground);
        SetBrush(resources, ControlBorderBrushKey, palette.ControlBorder);
        SetBrush(resources, ControlForegroundBrushKey, palette.ControlForeground);
        SetBrush(resources, ControlHoverBackgroundBrushKey, palette.ControlHoverBackground);
        SetBrush(resources, ControlPressedBackgroundBrushKey, palette.ControlPressedBackground);
        SetBrush(resources, TabHoverBackgroundBrushKey, palette.TabHoverBackground);
        SetBrush(resources, TabSelectedBackgroundBrushKey, palette.TabSelectedBackground);
        SetBrush(resources, TabSelectedForegroundBrushKey, palette.TabSelectedForeground);
        SetBrush(resources, ShellHostBackgroundBrushKey, palette.ShellHostBackground);
        SetBrush(resources, DockPreviewBackgroundBrushKey, palette.DockPreviewBackground);
        SetBrush(resources, DockPreviewBorderBrushKey, palette.DockPreviewBorder);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color)
    {
        resources[key] = new SolidColorBrush(ParseColor(color));
    }

    private static Color ParseColor(string color)
    {
        return (Color)(ColorConverter.ConvertFromString(color)
            ?? throw new InvalidOperationException($"Invalid theme color '{color}'."));
    }

    private sealed record AppThemePalette(
        string WindowBackground,
        string PanelBackground,
        string PanelBorder,
        string ActivePanelBorder,
        string ToolbarBackground,
        string PanelHeaderBackground,
        string Divider,
        string SubtleText,
        string PrimaryText,
        string ControlBackground,
        string ControlBorder,
        string ControlForeground,
        string ControlHoverBackground,
        string ControlPressedBackground,
        string TabHoverBackground,
        string TabSelectedBackground,
        string TabSelectedForeground,
        string ShellHostBackground,
        string DockPreviewBackground,
        string DockPreviewBorder);
}
