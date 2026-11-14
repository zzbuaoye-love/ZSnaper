using System.Drawing.Drawing2D;

namespace ZSnaper.Helpers;

public static class GraphicsHelper
{
    public static int Dip(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96f);

    public static GraphicsPath GetRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        if (radius <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        int d = radius * 2;
        var arc = new Rectangle(rect.X, rect.Y, d, d);

        // 左上角
        path.AddArc(arc, 180, 90);

        // 右上角
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);

        // 右下角
        arc.Y = rect.Bottom - d;
        path.AddArc(arc, 0, 90);

        // 左下角
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }

    public static GraphicsPath GetRoundedRectangle(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float maxRadius = Math.Min(rect.Width, rect.Height) / 2f;
        float clampedRadius = Math.Min(radius, maxRadius);
        if (clampedRadius <= 0.1f)
        {
            path.AddRectangle(rect);
            return path;
        }

        float diameter = clampedRadius * 2f;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180f, 90f);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270f, 90f);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    public static Color BlendColor(Color source, Color target, float amount)
    {
        float t = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(source.A + (target.A - source.A) * t),
            (int)Math.Round(source.R + (target.R - source.R) * t),
            (int)Math.Round(source.G + (target.G - source.G) * t),
            (int)Math.Round(source.B + (target.B - source.B) * t));
    }
}

