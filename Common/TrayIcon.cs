using System.Drawing;
using System.Runtime.InteropServices;

internal static class TrayIcon
{
    public static Icon Create(char label, Color backgroundColor)
    {
        using Bitmap bitmap = new(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (Font font = new("Segoe UI", 21, FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush background = new(backgroundColor))
        using (SolidBrush foreground = new(Color.White))
        using (StringFormat format = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillEllipse(background, 1, 1, 30, 30);
            graphics.DrawString(label.ToString(), font, foreground, new RectangleF(0, 0, 32, 32), format);
        }

        nint handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
