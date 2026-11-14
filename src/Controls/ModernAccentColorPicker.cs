using System.Drawing.Drawing2D;
using ZSnaper.Models;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class ModernAccentColorPicker : Control
{
    public record ColorPreset(string Name, string HexColor);

    private static readonly ColorPreset[] Presets = [
        new("黑白", AppConfig.DefaultAccentColorHex),
        new("翡翠绿", "#10B981"),
        new("晴空蓝", "#0EA5E9"),
        new("极光紫", "#8B5CF6"),
        new("活力橙", "#F97316")
    ];

    private int _hoveredIndex = -1;

    public ModernAccentColorPicker()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(160, 26);
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;

        ThemeManager.ThemeChanged += Invalidate;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int itemWidth = Width / Presets.Length;
        int idx = Math.Clamp(e.X / itemWidth, 0, Presets.Length - 1);
        if (_hoveredIndex != idx)
        {
            _hoveredIndex = idx;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            int itemWidth = Width / Presets.Length;
            int idx = Math.Clamp(e.X / itemWidth, 0, Presets.Length - 1);
            SelectPreset(idx);
        }
    }

    private int SelectedPreset => Array.FindIndex(Presets, preset =>
        string.Equals(preset.HexColor, ConfigService.Current.AccentColorHex, StringComparison.OrdinalIgnoreCase));
    private void SelectPreset(int index)
    {
        ThemeManager.AccentColor = ColorTranslator.FromHtml(Presets[Math.Clamp(index, 0, Presets.Length - 1)].HexColor);
        AccessibilityNotifyClients(AccessibleEvents.Selection, SelectedPreset + 1);
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int current = Math.Max(0, SelectedPreset);
        int next = e.KeyCode switch { Keys.Left => current - 1, Keys.Right => current + 1, Keys.Home => 0, Keys.End => Presets.Length - 1, _ => current };
        if (next == current) return;
        SelectPreset(next);
        e.Handled = e.SuppressKeyPress = true;
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedControlAccessibility(
        this, Presets.Select(preset => preset.Name).ToArray(), () => SelectedPreset, SelectPreset);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var palette = ThemeManager.Palette;
        var isDark = palette.Mode == ThemeMode.Dark;

        int itemWidth = Width / Presets.Length;
        int circleSize = GraphicsHelper.Dip(this, 18);
        int y = (Height - circleSize) / 2;

        for (int i = 0; i < Presets.Length; i++)
        {
            int x = i * itemWidth + (itemWidth - circleSize) / 2;
            var preset = Presets[i];
            var baseColor = ColorTranslator.FromHtml(preset.HexColor);

            if (preset.HexColor == AppConfig.DefaultAccentColorHex)
            {
                baseColor = isDark ? Color.White : Color.Black;
            }

            bool isSelected = IsColorMatching(palette.AccentColor, baseColor);
            bool isHovered = _hoveredIndex == i;

            var rect = new Rectangle(x, y, circleSize, circleSize);

            // 填充圆球
            using (var brush = new SolidBrush(baseColor))
            {
                g.FillEllipse(brush, rect);
            }

            // 选中的外圈光环 / 白色圆点
            if (isSelected)
            {
                // 绘制外圈高亮环
                var ringRect = new Rectangle(x - GraphicsHelper.Dip(this, 2), y - GraphicsHelper.Dip(this, 2), circleSize + GraphicsHelper.Dip(this, 4), circleSize + GraphicsHelper.Dip(this, 4));
                using var ringPen = new Pen(baseColor, 1.8f);
                g.DrawEllipse(ringPen, ringRect);

                // 绘制中心小白点/小黑点
                var centerDotRect = new Rectangle(x + GraphicsHelper.Dip(this, 5), y + GraphicsHelper.Dip(this, 5), circleSize - GraphicsHelper.Dip(this, 10), circleSize - GraphicsHelper.Dip(this, 10));
                Color dotColor = (baseColor.R * 0.299 + baseColor.G * 0.587 + baseColor.B * 0.114) > 186 ? Color.FromArgb(30, 30, 30) : Color.White;
                using var dotBrush = new SolidBrush(dotColor);
                g.FillEllipse(dotBrush, centerDotRect);
            }
            else if (isHovered || (Focused && ShowFocusCues && i == Math.Max(0, SelectedPreset)))
            {
                // Hover 微光外圈
                var hoverRingRect = new Rectangle(x - GraphicsHelper.Dip(this, 2), y - GraphicsHelper.Dip(this, 2), circleSize + GraphicsHelper.Dip(this, 4), circleSize + GraphicsHelper.Dip(this, 4));
                using var hoverPen = new Pen(Color.FromArgb(100, baseColor), 1.5f);
                g.DrawEllipse(hoverPen, hoverRingRect);
            }
        }
    }

    private static bool IsColorMatching(Color c1, Color c2)
    {
        return Math.Abs(c1.R - c2.R) < 15 && Math.Abs(c1.G - c2.G) < 15 && Math.Abs(c1.B - c2.B) < 15;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeManager.ThemeChanged -= Invalidate;
        base.Dispose(disposing);
    }
}
