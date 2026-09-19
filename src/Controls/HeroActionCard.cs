using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class HeroActionCard : Control
{
    private bool _isHovered;
    private bool _isPressed;
    private LucideIcon _icon = LucideIcon.Camera;
    private string _title = "功能标题";
    private string _description = "功能说明";
    private string _shortcutText = string.Empty;
    private bool _isPrimary = true;
    private int _cornerRadius = 12;

    public LucideIcon Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Invalidate();
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            Invalidate();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            _description = value ?? string.Empty;
            Invalidate();
        }
    }

    public string ShortcutText
    {
        get => _shortcutText;
        set
        {
            _shortcutText = value ?? string.Empty;
            Invalidate();
        }
    }

    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            _isPrimary = value;
            Invalidate();
        }
    }

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = value;
            Invalidate();
        }
    }

    public HeroActionCard()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(230, 84);
        TabStop = true;
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
        _isPressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _isPressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _isPressed = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        ThemePalette palette = ThemeManager.Palette;
        bool dark = palette.Mode == ThemeMode.Dark;
        Rectangle bounds = new(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 8);
        Color fill = _isPressed
            ? palette.InputBg
            : _isHovered
                ? (dark ? Color.FromArgb(57, 57, 57) : Color.FromArgb(250, 250, 250))
                : palette.CardBg;
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        using (var pen = new Pen(Focused ? palette.AccentColor : palette.CardBorder, Focused ? 2 : 1))
            g.DrawPath(pen, path);

        int inset = 20;
        LucideRenderer.Draw(g, _icon, inset, 20, 24,
            _isPrimary ? palette.AccentColor : palette.TextSecondary, 1.65f);

        using Font shortcutFont = new("Segoe UI", 8.5f);
        string shortcut = string.IsNullOrWhiteSpace(_shortcutText) ? "未设置快捷键" : _shortcutText;
        TextRenderer.DrawText(g, shortcut, shortcutFont,
            new Rectangle(58, 21, Math.Max(1, Width - 78), 22), palette.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        using Font titleFont = new("Microsoft YaHei UI", 12f, FontStyle.Bold);
        TextRenderer.DrawText(g, _title, titleFont,
            new Rectangle(inset, 60, Width - inset * 2, 28), palette.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using Font descriptionFont = new("Microsoft YaHei UI", 9f);
        TextRenderer.DrawText(g, _description, descriptionFont,
            new Rectangle(inset, 94, Width - inset * 2, Math.Max(26, Height - 104)),
            palette.TextSecondary, TextFormatFlags.Left | TextFormatFlags.WordBreak);
    }
}
