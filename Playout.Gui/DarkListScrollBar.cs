using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Playout.Gui;

internal sealed class DarkListScrollBar : Control
{
    private const int MinimumThumbHeight = 28;

    private readonly ListView listView;
    private readonly System.Windows.Forms.Timer syncTimer = new() { Interval = 30 };
    private Rectangle thumbBounds;
    private bool dragging;
    private bool hovered;
    private int dragOffset;
    private int itemCount;
    private int visibleItemCount;
    private int topIndex;
    private int maximumTopIndex;

    public DarkListScrollBar(ListView listView)
    {
        this.listView = listView;
        Width = 20;
        BackColor = Color.FromArgb(18, 31, 49);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        listView.HandleCreated += (_, _) => Synchronize();
        listView.SizeChanged += (_, _) => Synchronize();
        syncTimer.Tick += (_, _) => Synchronize();
        syncTimer.Start();
        Synchronize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            syncTimer.Stop();
            syncTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.Clear(BackColor);
        thumbBounds = GetThumbBounds();
        if (thumbBounds.Height <= 0)
            return;

        Color thumbColor = hovered || dragging
            ? Color.FromArgb(65, 119, 180)
            : Color.FromArgb(38, 79, 125);
        using SolidBrush thumbBrush = new(thumbColor);
        using GraphicsPath thumbPath = CreateRoundedRectangle(thumbBounds, 5);
        eventArgs.Graphics.FillPath(thumbBrush, thumbPath);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || !Visible)
            return;

        thumbBounds = GetThumbBounds();
        if (thumbBounds.Contains(eventArgs.Location))
        {
            dragging = true;
            dragOffset = eventArgs.Y - thumbBounds.Top;
            Capture = true;
            Invalidate();
            return;
        }

        SetTopIndex(topIndex + (eventArgs.Y < thumbBounds.Top ? -visibleItemCount : visibleItemCount));
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        hovered = true;
        if (dragging)
        {
            Rectangle track = ClientRectangle;
            int travel = track.Height - thumbBounds.Height;
            if (travel > 0 && maximumTopIndex > 0)
            {
                int thumbTop = Math.Clamp(eventArgs.Y - dragOffset, 0, travel);
                SetTopIndex((int)Math.Round((double)thumbTop / travel * maximumTopIndex));
            }
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button == MouseButtons.Left && dragging)
        {
            int travel = ClientRectangle.Height - thumbBounds.Height;
            if (travel > 0 && maximumTopIndex > 0)
            {
                int thumbTop = Math.Clamp(eventArgs.Y - dragOffset, 0, travel);
                SetTopIndex((int)Math.Round((double)thumbTop / travel * maximumTopIndex));
            }
            dragging = false;
            Capture = false;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        hovered = false;
        Invalidate();
    }

    private void Synchronize()
    {
        if (listView.IsDisposed || !listView.IsHandleCreated)
            return;

        itemCount = listView.Items.Count;
        int itemHeight = Math.Max(1, listView.Font.Height + 4);
        ListViewItem? topItem = null;
        if (itemCount > 0)
        {
            try
            {
                topItem = listView.TopItem;
                if (topItem is not null)
                {
                    int measuredHeight = listView.GetItemRect(topItem.Index).Height;
                    if (measuredHeight > 0)
                        itemHeight = measuredHeight;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // The native ListView can temporarily lack bounds during an update.
            }
        }

        visibleItemCount = Math.Max(1, listView.ClientSize.Height / itemHeight);
        maximumTopIndex = Math.Max(0, itemCount - visibleItemCount);
        topIndex = itemCount > 0 ? Math.Clamp(topItem?.Index ?? 0, 0, maximumTopIndex) : 0;
        Visible = maximumTopIndex > 0;
        Invalidate();
    }

    private Rectangle GetThumbBounds()
    {
        Rectangle track = ClientRectangle;
        if (track.Height <= 0)
            return Rectangle.Empty;

        if (itemCount <= 0 || maximumTopIndex <= 0)
            return Rectangle.Empty;

        int thumbHeight = Math.Min(track.Height, Math.Max(MinimumThumbHeight, (int)((long)track.Height * visibleItemCount / itemCount)));
        int travel = track.Height - thumbHeight;
        int thumbTop = (int)((long)travel * topIndex / maximumTopIndex);
        return new Rectangle(2, thumbTop, Math.Max(0, track.Width - 4), thumbHeight);
    }

    private void SetTopIndex(int index)
    {
        if (itemCount == 0 || !listView.IsHandleCreated)
            return;

        index = Math.Clamp(index, 0, maximumTopIndex);
        if (listView.TopItem?.Index != index)
            listView.TopItem = listView.Items[index];
        Synchronize();
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        Rectangle arc = new(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

}

internal sealed class DarkScrollableListView : ListView
{
    private const int ScrollBarVertical = 1;
    private const int WmSize = 0x0005;
    private const int WmPaint = 0x000F;
    private const int WmSetRedraw = 0x000B;
    private const int WmNcPaint = 0x0085;
    private const int WmStyleChanged = 0x007D;
    private const int WmVScroll = 0x0115;
    private const int WmMouseWheel = 0x020A;
    private bool hidingScrollBar;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        HideVerticalScrollBar();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcPaint)
            HideVerticalScrollBar();

        base.WndProc(ref message);

        if (message.Msg is WmSize or WmPaint or WmSetRedraw or WmNcPaint or WmStyleChanged or WmVScroll or WmMouseWheel)
            HideVerticalScrollBar();
    }

    private void HideVerticalScrollBar()
    {
        if (!IsHandleCreated || IsDisposed || hidingScrollBar)
            return;

        hidingScrollBar = true;
        try
        {
            ShowScrollBar(Handle, ScrollBarVertical, false);
        }
        finally
        {
            hidingScrollBar = false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowScrollBar(IntPtr hwnd, int bar, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool show);
}
