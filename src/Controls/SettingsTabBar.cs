using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class SettingsTabBar : Control
{
    private static readonly string[] TabLabels = ["外观", "截图", "OCR", "工具栏", "系统"];
    private readonly SmoothValueAnimator _indicatorAnimator;
    private readonly SmoothValueAnimator _hoverAnimator;
    private int _selectedIndex;
    private int _hoveredIndex = -1;
    private int _paintHoveredIndex = -1;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetSelectedIndex(value, raiseChanged: true);
    }

    public event EventHandler? SelectedIndexChanged;

    public SettingsTabBar()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        Size = new Size(420, 34);
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "设置分类";

        _indicatorAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 185);
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 110);

        ThemeManager.ThemeChanged += HandleThemeChanged;
    }

    internal void CompleteAnimations() => _indicatorAnimator.Complete();

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedControlAccessibility(
        this, TabLabels, () => _selectedIndex, index => SelectedIndex = index, AccessibleRole.PageTab);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hovered = HitTest(e.Location);
        if (_hoveredIndex != hovered)
        {
            _hoveredIndex = hovered;
            if (hovered >= 0) _paintHoveredIndex = hovered;
            _hoverAnimator.SetTarget(hovered >= 0 ? 1f : 0f);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            _hoverAnimator.SetTarget(0f);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            int hit = HitTest(e.Location);
            if (hit >= 0) SetSelectedIndex(hit, raiseChanged: true);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int next = e.KeyCode switch
        {
            Keys.Left => _selectedIndex - 1,
            Keys.Right => _selectedIndex + 1,
            Keys.Home => 0,
            Keys.End => TabLabels.Length - 1,
            _ => _selectedIndex
        };

        if (next == _selectedIndex) return;
        SetSelectedIndex(next, raiseChanged: true);
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        ThemePalette palette = ThemeManager.Palette;

        using var divider = new Pen(palette.CardBorder);
        graphics.DrawLine(divider, 0, Height - 1, Width, Height - 1);

        int gap = GraphicsHelper.Dip(this, 4);
        int segmentWidth = Math.Max(1, (Width - gap * (TabLabels.Length - 1)) / TabLabels.Length);
        float sliderPos = Math.Clamp(_indicatorAnimator.Value, 0f, TabLabels.Length - 1);
        float hoverOpacity = Math.Clamp(_hoverAnimator.Value, 0f, 1f);

        for (int index = 0; index < TabLabels.Length; index++)
        {
            Rectangle bounds = GetTabBounds(index, gap, segmentWidth);
            if (_paintHoveredIndex == index && hoverOpacity > 0.01f)
            {
                Color hoverBase = palette.NavItemHover;
                Color fill = Color.FromArgb((int)Math.Round(hoverBase.A * hoverOpacity), hoverBase);
                using GraphicsPath tabPath = GraphicsHelper.GetRoundedRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), 8f);
                using var tabBrush = new SolidBrush(fill);
                graphics.FillPath(tabBrush, tabPath);
            }

            float activeWeight = Math.Clamp(1f - Math.Abs(sliderPos - index), 0f, 1f);
            Color labelColor = GraphicsHelper.BlendColor(palette.TextSecondary, palette.AccentColor, activeWeight);
            TextRenderer.DrawText(
                graphics,
                TabLabels[index],
                Font,
                bounds,
                labelColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Smooth subpixel sliding bottom pill indicator
        float indicatorLeft = InterpolateTabLeft(sliderPos, gap, segmentWidth) + GraphicsHelper.Dip(this, 14);
        float indicatorWidth = Math.Max(8f, segmentWidth - GraphicsHelper.Dip(this, 28));
        float indicatorHeight = Math.Max(2.5f, GraphicsHelper.Dip(this, 3));
        var indicatorRect = new RectangleF(indicatorLeft, Height - indicatorHeight - 1.5f, indicatorWidth, indicatorHeight);
        using (var indicatorBrush = new SolidBrush(palette.AccentColor))
        using (GraphicsPath indicatorPath = GraphicsHelper.GetRoundedRectangle(indicatorRect, indicatorHeight / 2f))
        {
            graphics.FillPath(indicatorBrush, indicatorPath);
        }
    }

    private Rectangle GetTabBounds(int index, int gap, int segmentWidth)
    {
        int x = index * (segmentWidth + gap);
        if (index == TabLabels.Length - 1) x = Width - segmentWidth;
        return new Rectangle(x, 3, segmentWidth, Math.Max(1, Height - 8));
    }

    private float InterpolateTabLeft(float sliderPos, int gap, int segmentWidth)
    {
        int low = Math.Clamp((int)MathF.Floor(sliderPos), 0, TabLabels.Length - 1);
        int high = Math.Clamp((int)MathF.Ceiling(sliderPos), 0, TabLabels.Length - 1);
        float t = sliderPos - low;
        int leftA = GetTabBounds(low, gap, segmentWidth).Left;
        int leftB = GetTabBounds(high, gap, segmentWidth).Left;
        return leftA + (leftB - leftA) * t;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= HandleThemeChanged;
            _indicatorAnimator.Dispose();
            _hoverAnimator.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SetSelectedIndex(int index, bool raiseChanged)
    {
        int normalized = Math.Clamp(index, 0, TabLabels.Length - 1);
        if (_selectedIndex == normalized) return;

        _selectedIndex = normalized;
        _indicatorAnimator.SetTarget(normalized);
        AccessibilityNotifyClients(AccessibleEvents.Selection, normalized + 1);
        Invalidate();
        if (raiseChanged) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private int HitTest(Point point)
    {
        if (point.Y < 0 || point.Y >= Height) return -1;

        int gap = GraphicsHelper.Dip(this, 4);
        int segmentWidth = Math.Max(1, (Width - gap * (TabLabels.Length - 1)) / TabLabels.Length);
        for (int index = 0; index < TabLabels.Length; index++)
        {
            if (GetTabBounds(index, gap, segmentWidth).Contains(point)) return index;
        }

        return -1;
    }

    private void HandleThemeChanged() => Invalidate();
}
