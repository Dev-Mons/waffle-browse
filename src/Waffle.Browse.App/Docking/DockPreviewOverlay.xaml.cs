using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Waffle.Browse.App.Controls;
using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.App.Docking;

public partial class DockPreviewOverlay : UserControl
{
    public DockPreviewOverlay()
    {
        InitializeComponent();
    }

    public event EventHandler<DockOverlayDragEventArgs>? DragPointerMoved;

    public event EventHandler<DockOverlayDragEventArgs>? DragDropped;

    public event EventHandler? DragPointerLeft;

    public void BeginDragCapture()
    {
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        PreviewBorder.Visibility = Visibility.Collapsed;
    }

    public void ShowPreview(DockDropPreview preview)
    {
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        PreviewBorder.Visibility = Visibility.Visible;
        PreviewBorder.BorderBrush = preview.Accepted
            ? (Brush)FindResource("DockPreviewBorderBrush")
            : Brushes.IndianRed;
        Canvas.SetLeft(PreviewBorder, preview.PreviewBounds.X);
        Canvas.SetTop(PreviewBorder, preview.PreviewBounds.Y);
        PreviewBorder.Width = preview.PreviewBounds.Width;
        PreviewBorder.Height = preview.PreviewBounds.Height;
    }

    public void ClearPreview()
    {
        PreviewBorder.Visibility = Visibility.Collapsed;
    }

    public void EndDragCapture()
    {
        PreviewBorder.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        HandlePointerMove(e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        HandlePointerMove(e);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!TryGetPayload(e, out var payload))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        DragDropped?.Invoke(this, new DockOverlayDragEventArgs(payload, e.GetPosition(this)));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabDragPayload.Format))
        {
            return;
        }

        ClearPreview();
        DragPointerLeft?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void HandlePointerMove(DragEventArgs e)
    {
        if (!TryGetPayload(e, out var payload))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        DragPointerMoved?.Invoke(this, new DockOverlayDragEventArgs(payload, e.GetPosition(this)));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static bool TryGetPayload(DragEventArgs e, out DockDragPayload payload)
    {
        if (e.Data.GetData(TabDragPayload.Format) is TabDragPayload dragPayload)
        {
            payload = new DockDragPayload(dragPayload.SourcePanelId, dragPayload.TabId);
            return true;
        }

        payload = new DockDragPayload(Guid.Empty, Guid.Empty);
        return false;
    }
}

public sealed record DockOverlayDragEventArgs(DockDragPayload Payload, Point PointerInOverlay);
