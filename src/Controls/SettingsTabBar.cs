using System.Drawing.Drawing2D;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class SettingsTabBar : Control
{
    private static readonly string[] TabLabels = ["外观", "截图", "OCR", "工具栏", "系统"];
    private int _selectedIndex;
    private int _hoveredIndex = -1;

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
        Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
        Size = new Size(420, 34);
        TabStop = true;
        ThemeManager.ThemeChanged += HandleThemeChanged;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hovered = HitTest(e.Location);
        if (_hoveredIndex != hovered)
        {
            _hoveredIndex = hovered;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            SetSelectedIndex(HitTest(e.Location), raiseChanged: true);
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
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        ThemePalette palette = ThemeManager.Palette;

        Rectangle outer = new(0, 0, Width - 1, Height - 1);
        using (GraphicsPath outerPath = Helpers.GraphicsHelper.GetRoundedRectangle(outer, 9))
        using (var outerBrush = new SolidBrush(palette.Mode == ThemeMode.Dark
                   ? Color.FromArgb(0, 255, 255, 255)
                   : Color.FromArgb(0, 0, 0, 0)))
        {
            graphics.FillPath(outerBrush, outerPath);
        }

        int gap = 4;
        int segmentWidth = Math.Max(1, (Width - gap * (TabLabels.Length - 1)) / TabLabels.Length);
        for (int index = 0; index < TabLabels.Length; index++)
        {
            int x = index * (segmentWidth + gap);
            if (index == TabLabels.Length - 1) x = Width - segmentWidth;
            Rectangle bounds = new(x, 3, segmentWidth, Math.Max(1, Height - 7));

            bool selected = index == _selectedIndex;
            bool hovered = index == _hoveredIndex;
            if (selected || hovered)
            {
                Color fill = hovered ? palette.NavItemHover : Color.Transparent;
                using GraphicsPath tabPath = Helpers.GraphicsHelper.GetRoundedRectangle(bounds, 7);
                using var tabBrush = new SolidBrush(fill);
                graphics.FillPath(tabBrush, tabPath);
            }

            TextRenderer.DrawText(
                graphics,
                TabLabels[index],
                Font,
                bounds,
                selected ? palette.TextPrimary : palette.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (selected)
            {
                using var indicator = new SolidBrush(palette.AccentColor);
                using GraphicsPath indicatorPath = Helpers.GraphicsHelper.GetRoundedRectangle(
                    new Rectangle(bounds.Left + bounds.Width / 2 - 10, Height - 4, 20, 3), 1);
                graphics.FillPath(indicator, indicatorPath);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= HandleThemeChanged;
        }

        base.Dispose(disposing);
    }

    private void SetSelectedIndex(int index, bool raiseChanged)
    {
        int normalized = Math.Clamp(index, 0, TabLabels.Length - 1);
        if (_selectedIndex == normalized) return;

        _selectedIndex = normalized;
        Invalidate();
        if (raiseChanged) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private int HitTest(Point point)
    {
        if (point.Y < 0 || point.Y >= Height) return -1;

        int gap = 4;
        int segmentWidth = Math.Max(1, (Width - gap * (TabLabels.Length - 1)) / TabLabels.Length);
        for (int index = 0; index < TabLabels.Length; index++)
        {
            int x = index * (segmentWidth + gap);
            if (index == TabLabels.Length - 1) x = Width - segmentWidth;
            if (new Rectangle(x, 3, segmentWidth, Math.Max(1, Height - 7)).Contains(point)) return index;
        }

        return -1;
    }

    private void HandleThemeChanged() => Invalidate();
}
