using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class ModernCard : Panel
{
    public int CornerRadius { get; set; } = 8;
    public bool UseSidebarStyle { get; set; } = false;

    public ModernCard()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var palette = ThemeManager.Palette;
        var fillBg = UseSidebarStyle ? palette.SidebarBg : palette.CardBg;
        var borderColor = UseSidebarStyle ? palette.SidebarBorder : palette.CardBorder;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using var path = GraphicsHelper.GetRoundedRectangle(rect, CornerRadius);

        // WinUI 3 风格：平面填充，仅通过细边框表达层级。
        using (var brush = new SolidBrush(fillBg))
        {
            g.FillPath(brush, path);
        }

        // 绘制微细边框
        using (var pen = new Pen(borderColor, 1f))
        {
            g.DrawPath(pen, path);
        }

    }
}
