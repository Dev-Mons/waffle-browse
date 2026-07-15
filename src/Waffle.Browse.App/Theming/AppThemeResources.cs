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
    public const string SubtleTextBrushKey = "SubtleTextBrush";
    public const string PrimaryTextBrushKey = "PrimaryTextBrush";
    public const string ControlBackgroundBrushKey = "ControlBackgroundBrush";
    public const string ControlBorderBrushKey = "ControlBorderBrush";
    public const string ControlForegroundBrushKey = "ControlForegroundBrush";
    public const string TabHoverBackgroundBrushKey = "TabHoverBackgroundBrush";
    public const string TabSelectedBackgroundBrushKey = "TabSelectedBackgroundBrush";
    public const string TabSelectedForegroundBrushKey = "TabSelectedForegroundBrush";
    public const string ShellHostBackgroundBrushKey = "ShellHostBackgroundBrush";

    private static readonly AppThemePalette LightPalette = new(
        WindowBackground: "#F5F7FA",
        PanelBackground: "#FFFFFF",
        PanelBorder: "#CCD3DD",
        ActivePanelBorder: "#2F6FED",
        ToolbarBackground: "#E8EDF3",
        SubtleText: "#5F6B7A",
        PrimaryText: "#1F2933",
        ControlBackground: "#FFFFFF",
        ControlBorder: "#B8C2CC",
        ControlForeground: "#1F2933",
        TabHoverBackground: "#DDE6F1",
        TabSelectedBackground: "#D5E4FF",
        TabSelectedForeground: "#153A70",
        ShellHostBackground: "#FFFFFF");

    private static readonly AppThemePalette DarkPalette = new(
        WindowBackground: "#101820",
        PanelBackground: "#151F2A",
        PanelBorder: "#314154",
        ActivePanelBorder: "#69A1FF",
        ToolbarBackground: "#1E2A36",
        SubtleText: "#A8B3C3",
        PrimaryText: "#EEF3F8",
        ControlBackground: "#243241",
        ControlBorder: "#425265",
        ControlForeground: "#F4F7FA",
        TabHoverBackground: "#2A3948",
        TabSelectedBackground: "#2F6FED",
        TabSelectedForeground: "#FFFFFF",
        ShellHostBackground: "#0B1117");

    public static void Apply(ResourceDictionary resources, UiTheme theme)
    {
        var palette = theme == UiTheme.Dark ? DarkPalette : LightPalette;

        SetBrush(resources, WindowBackgroundBrushKey, palette.WindowBackground);
        SetBrush(resources, PanelBackgroundBrushKey, palette.PanelBackground);
        SetBrush(resources, PanelBorderBrushKey, palette.PanelBorder);
        SetBrush(resources, ActivePanelBorderBrushKey, palette.ActivePanelBorder);
        SetBrush(resources, ToolbarBackgroundBrushKey, palette.ToolbarBackground);
        SetBrush(resources, SubtleTextBrushKey, palette.SubtleText);
        SetBrush(resources, PrimaryTextBrushKey, palette.PrimaryText);
        SetBrush(resources, ControlBackgroundBrushKey, palette.ControlBackground);
        SetBrush(resources, ControlBorderBrushKey, palette.ControlBorder);
        SetBrush(resources, ControlForegroundBrushKey, palette.ControlForeground);
        SetBrush(resources, TabHoverBackgroundBrushKey, palette.TabHoverBackground);
        SetBrush(resources, TabSelectedBackgroundBrushKey, palette.TabSelectedBackground);
        SetBrush(resources, TabSelectedForegroundBrushKey, palette.TabSelectedForeground);
        SetBrush(resources, ShellHostBackgroundBrushKey, palette.ShellHostBackground);
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
        string SubtleText,
        string PrimaryText,
        string ControlBackground,
        string ControlBorder,
        string ControlForeground,
        string TabHoverBackground,
        string TabSelectedBackground,
        string TabSelectedForeground,
        string ShellHostBackground);
}
