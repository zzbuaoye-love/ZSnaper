using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class HeroActionCard : Control
{
    private readonly SmoothValueAnimator _hoverAnimator;
    private readonly SmoothValueAnimator _pressAnimator;
    private LucideIcon _icon = LucideIcon.Camera;
    private string _title = "功能标题";
    private string _description = "功能说明";
    private string _shortcutText = string.Empty;
    private bool _isPrimary = true;
    private int _cornerRadius = 12;
    private readonly Font _shortcutFont = new("Segoe UI", 8.5f);
    private readonly Font _titleFont = new("Microsoft YaHei UI", 14f, FontStyle.Regular);
    private readonly Font _descriptionFont = new("Microsoft YaHei UI", 9f);

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
        AccessibleRole = AccessibleRole.PushButton;
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 130);
        _pressAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 85);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hoverAnimator.SetTarget(1f);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverAnimator.SetTarget(0f);
        _pressAnimator.SetTarget(0f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _pressAnimator.SetTarget(1f);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressAnimator.SetTarget(0f);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _pressAnimator.SetTarget(0f);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        ThemePalette palette = ThemeManager.Palette;
        int D(int value) => GraphicsHelper.Dip(this, value);
        float hover = Math.Clamp(_hoverAnimator.Value, 0f, 1f);
        float press = Math.Clamp(_pressAnimator.Value, 0f, 1f);
        float yShift = press;
        var boundsF = new RectangleF(1f, 1f + yShift, Width - 3f, Height - 3f - yShift);
        using var path = GraphicsHelper.GetRoundedRectangle(boundsF, D(_cornerRadius));
        bool isLight = palette.Mode == ThemeMode.Light;
        Color primarySurface = isLight
            ? GraphicsHelper.BlendColor(palette.CardBg, palette.AccentColor, 0.055f)
            : GraphicsHelper.BlendColor(palette.CardBg, palette.AccentColor, 0.14f);
        Color secondarySurface = isLight ? Color.White : palette.CardBg;
        Color fill = _isPrimary ? primarySurface : secondarySurface;
        using (var surface = new SolidBrush(fill)) g.FillPath(surface, path);
        Color content = palette.TextPrimary;
        int stateAlpha = (int)Math.Round(16f * hover + 10f * press);
        if (stateAlpha > 0)
        {
            using var state = new SolidBrush(Color.FromArgb(stateAlpha, content));
            g.FillPath(state, path);
        }

        Color defaultBorder = _isPrimary
            ? GraphicsHelper.BlendColor(palette.CardBorder, palette.AccentColor, isLight ? 0.24f : 0.30f)
            : palette.CardBorder;
        Color borderColor = Focused && ShowFocusCues
            ? palette.AccentColor
            : GraphicsHelper.BlendColor(defaultBorder, palette.AccentColor, 0.35f * hover);
        using (var outline = new Pen(borderColor, Focused && ShowFocusCues ? 2 : 1))
        {
            g.DrawPath(outline, path);
        }

        int iconSize = D(42);
        int iconTop = (Height - iconSize) / 2;
        using var iconPath = GraphicsHelper.GetRoundedRectangle(new Rectangle(D(20), iconTop, iconSize, iconSize), D(13));
        using var iconFill = new SolidBrush(_isPrimary ? palette.AccentColor : palette.SurfaceContainerHigh);
        g.FillPath(iconFill, iconPath);
        LucideRenderer.Draw(g, _icon, D(29), iconTop + D(9), D(24),
            _isPrimary ? palette.AccentForeground : palette.TextPrimary, 1.75f);

        string shortcut = string.IsNullOrWhiteSpace(_shortcutText) ? "未设置" : _shortcutText;
        Size measuredShortcut = TextRenderer.MeasureText(
            g,
            shortcut,
            _shortcutFont,
            new Size(D(140), D(24)),
            TextFormatFlags.NoPadding);
        int badgeWidth = Math.Clamp(measuredShortcut.Width + D(18), D(52), D(128));
        int badgeHeight = D(24);
        int arrowLeft = Width - D(44);
        int badgeRight = arrowLeft - D(14);
        var badgeRect = new Rectangle(badgeRight - badgeWidth, (Height - badgeHeight) / 2, badgeWidth, badgeHeight);

        int textLeft = D(78);
        int textWidth = Math.Max(1, badgeRect.Left - D(14) - textLeft);
        int titleTop = (Height - D(48)) / 2;
        TextRenderer.DrawText(g, _title, _titleFont,
            new Rectangle(textLeft, titleTop, textWidth, D(28)), content,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _description, _descriptionFont,
            new Rectangle(textLeft, titleTop + D(30), textWidth, D(20)), palette.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        Color badgeBg = isLight
            ? GraphicsHelper.BlendColor(Color.White, palette.SurfaceContainerHigh, _isPrimary ? 0.45f : 0.7f)
            : palette.SurfaceContainerHigh;
        using var badgePath = GraphicsHelper.GetRoundedRectangle(badgeRect, D(7));
        using (var badgeBrush = new SolidBrush(badgeBg)) g.FillPath(badgeBrush, badgePath);
        using (var badgePen = new Pen(palette.CardBorder, 1f)) g.DrawPath(badgePen, badgePath);
        TextRenderer.DrawText(g, shortcut, _shortcutFont, badgeRect, palette.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        LucideRenderer.Draw(g, LucideIcon.ArrowUpRight, arrowLeft, (Height - D(20)) / 2f, D(20),
            GraphicsHelper.BlendColor(palette.TextSecondary, palette.AccentColor, hover), 1.75f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverAnimator.Dispose();
            _pressAnimator.Dispose();
            _shortcutFont.Dispose();
            _titleFont.Dispose();
            _descriptionFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
