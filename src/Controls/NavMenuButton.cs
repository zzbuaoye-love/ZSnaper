using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class NavMenuButton : Control
{
    private readonly SmoothValueAnimator _activeAnimator;
    private readonly SmoothValueAnimator _hoverAnimator;
    private bool _isActive;
    private bool _showLabel = true;
    private bool _useParentActivePill;
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
                _activeAnimator.SetTarget(value ? 1f : 0f);
                Invalidate();
            }
        }
    }

    public bool ShowLabel
    {
        get => _showLabel;
        set
        {
            if (_showLabel != value)
            {
                _showLabel = value;
                Invalidate();
            }
        }
    }

    internal bool UseParentActivePill
    {
        get => _useParentActivePill;
        set
        {
            if (_useParentActivePill != value)
            {
                _useParentActivePill = value;
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

    public NavMenuButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
        Size = new Size(72, 54);
        Font = new Font("Microsoft YaHei UI", 8.25f, FontStyle.Regular);

        _activeAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 175);
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 115);
    }

    internal void SnapActiveState()
    {
        _activeAnimator.SnapTo(_isActive ? 1f : 0f);
    }

    internal RectangleF GetPillBounds()
    {
        float D(float value) => value * (DeviceDpi / 96f);
        if (_showLabel)
        {
            float pillWidth = Math.Min(Width - D(10f), D(56f));
            float pillHeight = D(31f);
            return new RectangleF((Width - pillWidth) / 2f, D(2f), pillWidth, pillHeight);
        }

        float compactSize = Math.Clamp(Math.Min(Width - D(10f), Height - D(6f)), D(34f), D(42f));
        return new RectangleF((Width - compactSize) / 2f, (Height - compactSize) / 2f, compactSize, compactSize);
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
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        ThemePalette palette = ThemeManager.Palette;
        float D(float value) => value * (DeviceDpi / 96f);
        float active = Math.Clamp(_activeAnimator.Value, 0f, 1f);
        float hover = Math.Clamp(_hoverAnimator.Value, 0f, 1f);

        RectangleF pill = GetPillBounds();
        float corner = D(12f);
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(pill, corner);

        if (!_useParentActivePill && active > 0.01f)
        {
            Color activeFill = Color.FromArgb((int)Math.Round(palette.PrimaryContainer.A * active), palette.PrimaryContainer);
            using var activeBrush = new SolidBrush(activeFill);
            g.FillPath(activeBrush, path);
        }

        float residualHover = hover * (1f - active * 0.78f);
        if (residualHover > 0.01f)
        {
            Color hoverFill = Color.FromArgb((int)Math.Round(palette.NavItemHover.A * residualHover), palette.NavItemHover);
            using var hoverBrush = new SolidBrush(hoverFill);
            g.FillPath(hoverBrush, path);
        }

        float iconSize = D(_showLabel ? 18.5f : 20f);
        Color idleIcon = GraphicsHelper.BlendColor(palette.TextSecondary, palette.TextPrimary, 0.55f * hover);
        Color activeIcon = palette.AccentColor;
        Color iconColor = GraphicsHelper.BlendColor(idleIcon, activeIcon, active);
        float iconX = pill.Left + (pill.Width - iconSize) / 2f;
        float iconY = pill.Top + (pill.Height - iconSize) / 2f;
        LucideRenderer.Draw(g, _icon, iconX, iconY, iconSize, iconColor, active > 0.5f ? 1.9f : 1.7f);

        if (_showLabel)
        {
            Color idleText = GraphicsHelper.BlendColor(palette.TextSecondary, palette.TextPrimary, 0.45f * hover);
            Color labelColor = GraphicsHelper.BlendColor(idleText, palette.TextPrimary, active);
            Font? boldFont = active > 0.5f ? new Font(Font, FontStyle.Bold) : null;
            var textRect = new Rectangle(0, (int)MathF.Round(pill.Bottom + D(2f)), Width, Math.Max(1, Height - (int)MathF.Round(pill.Bottom + D(2f))));
            try
            {
                TextRenderer.DrawText(
                    g,
                    _labelText,
                    boldFont ?? Font,
                    textRect,
                    labelColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            finally
            {
                boldFont?.Dispose();
            }
        }

        if (Focused && ShowFocusCues)
        {
            using var focusPen = new Pen(palette.AccentColor, 1.8f);
            g.DrawPath(focusPen, path);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activeAnimator.Dispose();
            _hoverAnimator.Dispose();
        }
        base.Dispose(disposing);
    }
}
