using System.Drawing.Drawing2D;

namespace Playout.Gui;

internal sealed class AudioLevelMeter : Control
{
    private float level;

    public AudioLevelMeter()
    {
        DoubleBuffered = true;
        MinimumSize = new Size(12, 60);
        BackColor = Color.FromArgb(21, 27, 38);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Audiopegel";
    }

    public float Level
    {
        get => level;
        set
        {
            float normalized = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            if (Math.Abs(level - normalized) < 0.005f)
                return;
            level = normalized;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Inflate(-2, -2);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using (GraphicsPath path = RoundedRectangle(bounds, Math.Min(5, bounds.Width / 2)))
        using (SolidBrush background = new(BackColor))
        using (Pen outline = new(Color.FromArgb(55, 68, 87)))
        {
            e.Graphics.FillPath(background, path);
            e.Graphics.DrawPath(outline, path);
        }

        int activeHeight = (int)Math.Round(bounds.Height * level);
        if (activeHeight <= 0)
            return;

        int innerX = bounds.X + 3;
        int innerWidth = Math.Max(1, bounds.Width - 6);
        int segmentHeight = Math.Max(2, (int)(DeviceDpi / 48f));
        int gap = Math.Max(1, (int)(DeviceDpi / 96f));
        int segmentStep = segmentHeight + gap;
        int segmentCount = bounds.Height / segmentStep;
        int activeSegments = Math.Min(segmentCount, (int)Math.Ceiling((double)activeHeight / segmentStep));

        for (int i = 0; i < activeSegments; i++)
        {
            float position = segmentCount <= 1 ? 0 : (float)i / (segmentCount - 1);
            Color color = position >= 0.9f ? Color.FromArgb(255, 75, 82) :
                position >= 0.72f ? Color.FromArgb(255, 190, 65) : Color.FromArgb(57, 210, 135);
            int y = bounds.Bottom - 3 - segmentHeight - i * segmentStep;
            using SolidBrush segment = new(color);
            e.Graphics.FillRectangle(segment, innerX, y, innerWidth, segmentHeight);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
