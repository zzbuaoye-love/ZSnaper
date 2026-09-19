using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class NavMenuButton : Control
{
    private bool _isActive;
    private bool _isHovered;
    private LucideIcon _icon = LucideIcon.Camera;
    private string _labelText = "菜单项";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                Invalidate();
            }
        }
    }

    public LucideIcon Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Invalidate();
        }
    }

    public string LabelText
    {
        get => _labelText;
        set
        {
            _labelText = value;
            Invalidate();
        }
    }

    public int CornerRadius { get; set; } = 8;

    public NavMenuButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        Size = new Size(136, 36);
        Font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var palette = ThemeManager.Palette;
        var isDark = palette.Mode == ThemeMode.Dark;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using var path = GraphicsHelper.GetRoundedRectangle(rect, CornerRadius);

        Color iconColor;
        Color textColor;

        if (_isActive)
        {
            // 激活态：轻量整行底色 + 小尺寸强调色图标底板。
            Color activeBackground = isDark
                ? Color.FromArgb(45, 45, 45)
                : Color.FromArgb(230, 230, 230);

            using (var brush = new SolidBrush(activeBackground))
            {
                g.FillPath(brush, path);
            }

            var iconSurfaceRect = new Rectangle(1, (Height - 16) / 2, 3, 16);
            using (var iconSurfacePath = GraphicsHelper.GetRoundedRectangle(iconSurfaceRect, 1))
            using (var iconSurfaceBrush = new SolidBrush(Color.FromArgb(
                255,
                palette.AccentColor)))
            {
                g.FillPath(iconSurfaceBrush, iconSurfacePath);
            }

            iconColor = palette.TextPrimary;
            textColor = palette.TextPrimary;
        }
        else if (_isHovered)
        {
            using (var hoverBrush = new SolidBrush(palette.NavItemHover))
            {
                g.FillPath(hoverBrush, path);
            }

            iconColor = palette.TextPrimary;
            textColor = palette.TextPrimary;
        }
        else
        {
            iconColor = palette.TextMuted;
            textColor = palette.TextSecondary;
        }

        // 1. 绘制高清 Lucide 矢量图标 (16px)
        float iconSize = 16f;
        float iconX = 11f;
        float iconY = (Height - iconSize) / 2f;
        LucideRenderer.Draw(g, _icon, iconX, iconY, iconSize, iconColor);

        // 2. 绘制菜单文字
        using var labelFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
        using var textBrush = new SolidBrush(textColor);
        var labelSize = g.MeasureString(_labelText, labelFont);
        g.DrawString(_labelText, labelFont, textBrush, 40, (Height - labelSize.Height) / 2);
        if (Focused && ShowFocusCues)
        {
            using var focusPen = new Pen(palette.TextSecondary) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focusPen, 3, 3, Width - 7, Height - 7);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
