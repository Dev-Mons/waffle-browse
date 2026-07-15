using System.Text;

namespace Waffle.Browse.App.Diagnostics;

internal sealed record FocusTraceEntry(
    DateTimeOffset Timestamp,
    string Stage,
    string MessageName,
    int Message,
    IntPtr MessageWindow,
    string MessageWindowClass,
    IntPtr WParam,
    IntPtr CurrentFocus,
    string CurrentFocusClass,
    string WpfFocus,
    Guid? MessagePanelId,
    Guid? FocusPanelId,
    bool Handled,
    string? Details)
{
    public string Format()
    {
        var builder = new StringBuilder();
        builder.Append(Timestamp.ToString("O"));
        builder.Append(" stage=").Append(Stage);
        builder.Append(" msg=").Append(MessageName).Append("(0x").Append(Message.ToString("X4")).Append(')');
        builder.Append(" hwnd=").Append(FormatHandle(MessageWindow)).Append('[').Append(MessageWindowClass).Append(']');
        builder.Append(" wParam=").Append(FormatHandle(WParam));
        builder.Append(" focus=").Append(FormatHandle(CurrentFocus)).Append('[').Append(CurrentFocusClass).Append(']');
        builder.Append(" wpfFocus=").Append(WpfFocus);
        builder.Append(" msgPanel=").Append(MessagePanelId?.ToString("N") ?? "-");
        builder.Append(" focusPanel=").Append(FocusPanelId?.ToString("N") ?? "-");
        builder.Append(" handled=").Append(Handled ? "true" : "false");

        if (!string.IsNullOrWhiteSpace(Details))
        {
            builder.Append(" details=").Append(Details);
        }

        return builder.ToString();
    }

    private static string FormatHandle(IntPtr handle)
    {
        return "0x" + handle.ToInt64().ToString("X");
    }
}
