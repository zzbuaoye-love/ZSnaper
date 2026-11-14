using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

/// <summary>
/// 设置页中的截图工具栏编辑器。所有选项都在当前卡片内即时应用，
/// 避免通过模态窗打断设置流程。
/// </summary>
public sealed class ToolbarCustomizationPanel : Control
{
    private const int HorizontalPadding = 16;
    private const int ChipHeight = 28;
    private const int ChipGap = 6;

    private static readonly CaptureToolbarItem[] DefaultOrder = CaptureToolbarDefaults.CreateItems().ToArray();
    private static readonly Color[] AnnotationColors =
    [
        Color.FromArgb(255, 59, 48),
        Color.FromArgb(255, 149, 0),
        Color.FromArgb(255, 204, 0),
        Color.FromArgb(52, 199, 89),
        Color.FromArgb(50, 215, 225),
        Color.FromArgb(0, 122, 255),
        Color.FromArgb(88, 86, 214),
        Color.FromArgb(175, 82, 222),
        Color.FromArgb(255, 45, 85),
        Color.White,
        Color.FromArgb(142, 142, 147),
        Color.Black
    ];

    private readonly List<CaptureToolbarItem> _order;
    private readonly HashSet<CaptureToolbarItem> _enabled;
    private readonly List<Rectangle> _chipBounds = [];
    private readonly ComboBox _fontFamily = new();
    private readonly ComboBox _fontSize = new();
    private readonly ToolTip _toolTip = new();

    private int _pressedChipIndex = -1;
    private int _draggingChipIndex = -1;
    private Point _mouseDownPoint;
    private Rectangle _behaviorStickyBounds;
    private Rectangle _behaviorSingleBounds;
    private readonly Rectangle[] _layoutBounds = new Rectangle[5];
    private readonly Rectangle[] _completionBounds = new Rectangle[5];
    private readonly List<Rectangle> _colorBounds = [];
    private Rectangle _penMinusBounds;
    private Rectangle _penPlusBounds;
    private Rectangle _mosaicMinusBounds;
    private Rectangle _mosaicPlusBounds;
    private Rectangle _pixelMinusBounds;
    private Rectangle _pixelPlusBounds;
    private readonly Rectangle[] _arrowBounds = new Rectangle[3];
    private Rectangle _boldBounds;
    private Rectangle _italicBounds;

    public ToolbarCustomizationPanel()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Default;

        _order = BuildOrder();
        _enabled = (ConfigService.Current.CaptureToolbarItems ?? [])
            .Where(Enum.IsDefined)
            .ToHashSet();
        if (_enabled.Count == 0) _enabled.Add(CaptureToolbarItem.Confirm);
        NormalizeConfiguredLayout();

        Font = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Regular);
        Size = new Size(480, 398);
        MinimumSize = new Size(390, 398);

        ConfigureCombo(_fontFamily, 196);
        ConfigureCombo(_fontSize, 68);
        PopulateFontFamilies();
        _fontSize.Items.AddRange(["8", "10", "12", "14", "16", "18", "20", "24", "28", "32", "40", "48", "56", "64", "72"]);

        string configuredFamily = ConfigService.Current.AnnotationFontFamily;
        _fontFamily.SelectedItem = _fontFamily.Items.Cast<string>()
            .FirstOrDefault(name => string.Equals(name, configuredFamily, StringComparison.OrdinalIgnoreCase));
        if (_fontFamily.SelectedIndex < 0)
        {
            _fontFamily.Items.Insert(0, configuredFamily);
            _fontFamily.SelectedIndex = 0;
        }

        string configuredSize = Math.Clamp(ConfigService.Current.AnnotationFontSize, 8f, 72f).ToString("0.#");
        if (!_fontSize.Items.Contains(configuredSize)) _fontSize.Items.Add(configuredSize);
        _fontSize.SelectedItem = configuredSize;

        _fontFamily.SelectedIndexChanged += (_, _) =>
        {
            if (_fontFamily.SelectedItem is not string family) return;
            ConfigService.Current.AnnotationFontFamily = family;
            SaveAndRefresh();
        };
        _fontSize.SelectedIndexChanged += (_, _) =>
        {
            if (!float.TryParse(_fontSize.SelectedItem?.ToString(), out float size)) return;
            ConfigService.Current.AnnotationFontSize = Math.Clamp(size, 8f, 72f);
            SaveAndRefresh();
        };

        Controls.Add(_fontFamily);
        Controls.Add(_fontSize);
        ThemeManager.ThemeChanged += ApplyTheme;
        ApplyTheme();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int right = Math.Max(374, Width - HorizontalPadding);
        _fontFamily.SetBounds(106, 358, Math.Max(150, right - 106 - 124), 28);
        _fontSize.SetBounds(right - 116, 358, 68, 28);
        _boldBounds = new Rectangle(right - 42, 358, 20, 28);
        _italicBounds = new Rectangle(right - 20, 358, 20, 28);
        RebuildHitTargets();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _mouseDownPoint = e.Location;
        _pressedChipIndex = HitTest(_chipBounds, e.Location);
        if (_pressedChipIndex >= 0) Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int chipIndex = HitTest(_chipBounds, e.Location);
        bool overInteractive = chipIndex >= 0 || HitAnyAction(e.Location);
        Cursor = _pressedChipIndex >= 0 ? Cursors.SizeAll : overInteractive ? Cursors.Hand : Cursors.Default;

        if (_pressedChipIndex < 0 || e.Button != MouseButtons.Left) return;
        if (_draggingChipIndex < 0 &&
            (Math.Abs(e.X - _mouseDownPoint.X) > 4 || Math.Abs(e.Y - _mouseDownPoint.Y) > 4))
        {
            _draggingChipIndex = _pressedChipIndex;
        }

        if (_draggingChipIndex < 0 || chipIndex < 0 || chipIndex == _draggingChipIndex) return;
        CaptureToolbarItem item = _order[_draggingChipIndex];
        _order.RemoveAt(_draggingChipIndex);
        _order.Insert(chipIndex, item);
        _draggingChipIndex = chipIndex;
        RebuildHitTargets();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        int pressedChip = _pressedChipIndex;
        bool dragged = _draggingChipIndex >= 0;
        _pressedChipIndex = -1;
        _draggingChipIndex = -1;
        Cursor = Cursors.Default;

        if (dragged)
        {
            SaveToolbarState();
            Invalidate();
            return;
        }

        int releasedChip = HitTest(_chipBounds, e.Location);
        if (pressedChip >= 0 && releasedChip == pressedChip)
        {
            CaptureToolbarItem item = _order[pressedChip];
            if (_enabled.Contains(item))
            {
                if (_enabled.Count > 1) _enabled.Remove(item);
            }
            else
            {
                _enabled.Add(item);
            }
            SaveToolbarState();
            Invalidate();
            return;
        }

        ExecuteAction(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_pressedChipIndex < 0) Cursor = Cursors.Default;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        ThemePalette palette = ThemeManager.Palette;

        var surfaceBounds = new Rectangle(8, 4, Math.Max(0, Width - 17), Height - 10);
        if (surfaceBounds.Width <= 0) return;
        using (GraphicsPath surface = GraphicsHelper.GetRoundedRectangle(surfaceBounds, 9))
        using (var fill = new SolidBrush(palette.Mode == ThemeMode.Dark
                   ? Color.FromArgb(23, 25, 31)
                   : Color.FromArgb(248, 250, 252)))
        using (var border = new Pen(palette.InputBorder))
        {
            g.FillPath(fill, surface);
            g.DrawPath(border, surface);
        }

        DrawHeader(g, palette);
        DrawRowLabel(g, palette, "布局方案", 50);
        string[] layoutLabels = ["极简", "批注", "识别", "完整", "我的"];
        for (int i = 0; i < _layoutBounds.Length; i++)
        {
            DrawSegment(g, palette, _layoutBounds[i], layoutLabels[i], (int)ConfigService.Current.CaptureToolbarLayout == i);
        }

        DrawToolbarTitle(g, palette);
        DrawToolbarChips(g, palette);
        DrawDivider(g, palette, 169);

        DrawRowLabel(g, palette, "完成按钮", 188);
        string[] completionLabels = ["复制", "保存", "复制+存", "仅完成", "跟随"];
        for (int i = 0; i < _completionBounds.Length; i++)
        {
            DrawSegment(g, palette, _completionBounds[i], completionLabels[i], (int)ConfigService.Current.ConfirmButtonBehavior == i);
        }

        DrawRowLabel(g, palette, "图标行为", 224);
        DrawSegment(g, palette, _behaviorStickyBounds, "连续", ConfigService.Current.AnnotationToolBehavior == AnnotationToolBehavior.Sticky);
        DrawSegment(g, palette, _behaviorSingleBounds, "单次", ConfigService.Current.AnnotationToolBehavior == AnnotationToolBehavior.SingleUse);

        DrawRowLabel(g, palette, "默认颜色", 260);
        DrawColors(g, palette);

        DrawRowLabel(g, palette, "笔刷精度", 296);
        DrawStepper(g, palette, "画笔", ConfigService.Current.AnnotationPenWidth.ToString("0.#"), _penMinusBounds, _penPlusBounds);
        DrawStepper(g, palette, "马赛克", ConfigService.Current.AnnotationMosaicSize.ToString("0"), _mosaicMinusBounds, _mosaicPlusBounds);
        DrawStepper(g, palette, "颗粒", ConfigService.Current.AnnotationMosaicPixelSize.ToString(), _pixelMinusBounds, _pixelPlusBounds);

        DrawRowLabel(g, palette, "箭头样式", 332);
        string[] arrowLabels = ["空心", "实心", "双向"];
        for (int i = 0; i < _arrowBounds.Length; i++)
        {
            DrawSegment(g, palette, _arrowBounds[i], arrowLabels[i], (int)ConfigService.Current.AnnotationArrowStyle == i);
        }

        DrawRowLabel(g, palette, "文字样式", 372);
        bool bold = ((FontStyle)ConfigService.Current.AnnotationFontStyle).HasFlag(FontStyle.Bold);
        bool italic = ((FontStyle)ConfigService.Current.AnnotationFontStyle).HasFlag(FontStyle.Italic);
        DrawCompactToggle(g, palette, _boldBounds, "B", bold, FontStyle.Bold);
        DrawCompactToggle(g, palette, _italicBounds, "I", italic, FontStyle.Italic);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= ApplyTheme;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void DrawHeader(Graphics g, ThemePalette palette)
    {
        using var titleFont = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold);
        using var titleBrush = new SolidBrush(palette.TextSecondary);
        using var hintFont = new Font("Microsoft YaHei UI", 7.7f);
        using var hintBrush = new SolidBrush(palette.TextMuted);
        g.DrawString("布局与操作", titleFont, titleBrush, 16, 12);
        g.DrawString("切换预设后仍可继续微调", hintFont, hintBrush, Math.Max(160, Width - 166), 14);
    }

    private void DrawToolbarTitle(Graphics g, ThemePalette palette)
    {
        using var titleFont = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
        using var titleBrush = new SolidBrush(palette.TextSecondary);
        using var hintFont = new Font("Microsoft YaHei UI", 7.7f);
        using var hintBrush = new SolidBrush(palette.TextMuted);
        g.DrawString("工具显隐与顺序", titleFont, titleBrush, 16, 76);
        g.DrawString("点击显隐 · 拖动排序", hintFont, hintBrush, Math.Max(160, Width - 132), 78);
    }

    private void DrawToolbarChips(Graphics g, ThemePalette palette)
    {
        for (int i = 0; i < _order.Count && i < _chipBounds.Count; i++)
        {
            CaptureToolbarItem item = _order[i];
            Rectangle bounds = _chipBounds[i];
            bool active = _enabled.Contains(item);
            bool dragging = i == _draggingChipIndex;
            Color baseColor = palette.Mode == ThemeMode.Dark
                ? Color.FromArgb(23, 25, 31)
                : Color.FromArgb(248, 250, 252);
            Color fillColor = active
                ? Blend(baseColor, palette.AccentColor, palette.Mode == ThemeMode.Dark ? 0.18f : 0.10f)
                : Blend(baseColor, palette.TextPrimary, palette.Mode == ThemeMode.Dark ? 0.035f : 0.025f);
            Color borderColor = active ? Color.FromArgb(145, palette.AccentColor) : palette.InputBorder;
            if (dragging) fillColor = Blend(baseColor, palette.AccentColor, 0.28f);

            using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 7);
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(borderColor, dragging ? 1.5f : 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            Color iconColor = active ? palette.AccentColor : palette.TextMuted;
            LucideRenderer.Draw(g, IconFor(item), bounds.X + 7, bounds.Y + 7, 14, iconColor, 1.8f);
            TextRenderer.DrawText(
                g,
                ShortName(item),
                Font,
                new Rectangle(bounds.X + 25, bounds.Y, bounds.Width - 29, bounds.Height),
                active ? palette.TextPrimary : palette.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private static void DrawDivider(Graphics g, ThemePalette palette, int y)
    {
        using var pen = new Pen(Color.FromArgb(palette.Mode == ThemeMode.Dark ? 24 : 18, palette.TextPrimary));
        g.DrawLine(pen, 16, y, Math.Max(16, g.VisibleClipBounds.Width - 16), y);
    }

    private static void DrawRowLabel(Graphics g, ThemePalette palette, string text, int centerY)
    {
        using var font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
        TextRenderer.DrawText(
            g,
            text,
            font,
            new Rectangle(16, centerY - 14, 84, 28),
            palette.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static void DrawSegment(Graphics g, ThemePalette palette, Rectangle bounds, string text, bool active)
    {
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 7);
        Color baseColor = palette.Mode == ThemeMode.Dark ? Color.FromArgb(23, 25, 31) : Color.FromArgb(248, 250, 252);
        using var fill = new SolidBrush(active ? palette.AccentColor : Blend(baseColor, palette.TextPrimary, 0.035f));
        using var border = new Pen(active ? palette.AccentColor : palette.InputBorder);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        using var font = new Font("Microsoft YaHei UI", 8f);
        TextRenderer.DrawText(
            g,
            text,
            font,
            bounds,
            active ? palette.AccentForeground : palette.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawColors(Graphics g, ThemePalette palette)
    {
        Color selected = ParseColor(ConfigService.Current.AnnotationColorHex, AnnotationColors[0]);
        for (int i = 0; i < AnnotationColors.Length && i < _colorBounds.Count; i++)
        {
            Rectangle bounds = _colorBounds[i];
            Color color = AnnotationColors[i];
            bool active = color.ToArgb() == selected.ToArgb();
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, bounds);
            using var edge = new Pen(active ? palette.AccentColor : Color.FromArgb(90, palette.TextMuted), active ? 2f : 1f);
            g.DrawEllipse(edge, bounds);
            if (!active) continue;
            Rectangle inner = Rectangle.Inflate(bounds, -4, -4);
            using var innerPen = new Pen(color.GetBrightness() > 0.7f ? Color.Black : Color.White, 1.5f);
            g.DrawEllipse(innerPen, inner);
        }
    }

    private static void DrawStepper(
        Graphics g,
        ThemePalette palette,
        string label,
        string value,
        Rectangle minusBounds,
        Rectangle plusBounds)
    {
        var whole = Rectangle.FromLTRB(minusBounds.Left, minusBounds.Top, plusBounds.Right, plusBounds.Bottom);
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(whole, 7);
        Color baseColor = palette.Mode == ThemeMode.Dark ? Color.FromArgb(23, 25, 31) : Color.FromArgb(248, 250, 252);
        using var fill = new SolidBrush(Blend(baseColor, palette.TextPrimary, 0.035f));
        using var border = new Pen(palette.InputBorder);
        using var symbolFont = new Font("Segoe UI", 9f);
        using var valueFont = new Font("Microsoft YaHei UI", 7.7f);
        g.FillPath(fill, path);
        g.DrawPath(border, path);

        TextRenderer.DrawText(g, "−", symbolFont, minusBounds, palette.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "+", symbolFont, plusBounds, palette.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var valueBounds = Rectangle.FromLTRB(minusBounds.Right, whole.Top, plusBounds.Left, whole.Bottom);
        TextRenderer.DrawText(g, $"{label} {value}", valueFont, valueBounds, palette.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static void DrawCompactToggle(
        Graphics g,
        ThemePalette palette,
        Rectangle bounds,
        string text,
        bool active,
        FontStyle style)
    {
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 6);
        using var fill = new SolidBrush(active ? palette.AccentColor : palette.InputBg);
        using var border = new Pen(active ? palette.AccentColor : palette.InputBorder);
        using var font = new Font("Segoe UI", 8.4f, style);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        TextRenderer.DrawText(g, text, font, bounds,
            active ? palette.AccentForeground : palette.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void RebuildHitTargets()
    {
        _chipBounds.Clear();
        int x = HorizontalPadding;
        int y = 100;
        int right = Math.Max(HorizontalPadding + 200, Width - HorizontalPadding);
        using Graphics graphics = CreateGraphics();
        foreach (CaptureToolbarItem item in _order)
        {
            int textWidth = TextRenderer.MeasureText(graphics, ShortName(item), Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int width = Math.Clamp(textWidth + 31, 48, 68);
            if (x + width > right)
            {
                x = HorizontalPadding;
                y += ChipHeight + ChipGap;
            }
            _chipBounds.Add(new Rectangle(x, y, width, ChipHeight));
            x += width + 5;
        }

        int contentRight = Math.Max(374, Width - HorizontalPadding);
        int fiveSegmentGap = 5;
        int fiveSegmentWidth = Math.Max(52, (contentRight - 106 - fiveSegmentGap * 4) / 5);
        for (int i = 0; i < 5; i++)
        {
            _layoutBounds[i] = new Rectangle(106 + i * (fiveSegmentWidth + fiveSegmentGap), 36, fiveSegmentWidth, 28);
            _completionBounds[i] = new Rectangle(106 + i * (fiveSegmentWidth + fiveSegmentGap), 174, fiveSegmentWidth, 28);
        }

        _behaviorStickyBounds = new Rectangle(106, 210, 86, 28);
        _behaviorSingleBounds = new Rectangle(198, 210, 86, 28);

        _colorBounds.Clear();
        for (int i = 0; i < AnnotationColors.Length; i++)
        {
            _colorBounds.Add(new Rectangle(108 + i * 27, 250, 20, 20));
        }

        int stepperGap = 6;
        int stepperWidth = Math.Max(82, (contentRight - 106 - stepperGap * 2) / 3);
        SetStepperBounds(106, 282, stepperWidth, out _penMinusBounds, out _penPlusBounds);
        SetStepperBounds(106 + stepperWidth + stepperGap, 282, stepperWidth, out _mosaicMinusBounds, out _mosaicPlusBounds);
        SetStepperBounds(106 + (stepperWidth + stepperGap) * 2, 282, stepperWidth, out _pixelMinusBounds, out _pixelPlusBounds);

        int arrowWidth = Math.Max(66, Math.Min(86, (contentRight - 106 - 12) / 3));
        for (int i = 0; i < _arrowBounds.Length; i++)
        {
            _arrowBounds[i] = new Rectangle(106 + i * (arrowWidth + 6), 318, arrowWidth, 28);
        }
    }

    private static void SetStepperBounds(int x, int y, int width, out Rectangle minus, out Rectangle plus)
    {
        minus = new Rectangle(x, y, 24, 28);
        plus = new Rectangle(x + width - 24, y, 24, 28);
    }

    private void ExecuteAction(Point point)
    {
        int layoutIndex = Array.FindIndex(_layoutBounds, bounds => bounds.Contains(point));
        if (layoutIndex >= 0)
        {
            ApplyLayout((CaptureToolbarLayout)layoutIndex);
            return;
        }

        int completionIndex = Array.FindIndex(_completionBounds, bounds => bounds.Contains(point));
        if (completionIndex >= 0)
        {
            ConfigService.Current.ConfirmButtonBehavior = (ConfirmButtonBehavior)completionIndex;
        }
        else if (_behaviorStickyBounds.Contains(point))
        {
            ConfigService.Current.AnnotationToolBehavior = AnnotationToolBehavior.Sticky;
        }
        else if (_behaviorSingleBounds.Contains(point))
        {
            ConfigService.Current.AnnotationToolBehavior = AnnotationToolBehavior.SingleUse;
        }
        else
        {
            int colorIndex = HitTest(_colorBounds, point);
            if (colorIndex >= 0)
            {
                Color color = AnnotationColors[colorIndex];
                ConfigService.Current.AnnotationColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            else if (_penMinusBounds.Contains(point)) AdjustPen(-0.5f);
            else if (_penPlusBounds.Contains(point)) AdjustPen(0.5f);
            else if (_mosaicMinusBounds.Contains(point)) AdjustMosaic(-2f);
            else if (_mosaicPlusBounds.Contains(point)) AdjustMosaic(2f);
            else if (_pixelMinusBounds.Contains(point)) AdjustPixel(-2);
            else if (_pixelPlusBounds.Contains(point)) AdjustPixel(2);
            else
            {
                int arrowIndex = Array.FindIndex(_arrowBounds, bounds => bounds.Contains(point));
                if (arrowIndex >= 0) ConfigService.Current.AnnotationArrowStyle = (AnnotationArrowStyle)arrowIndex;
                else if (_boldBounds.Contains(point)) ToggleFontStyle(FontStyle.Bold);
                else if (_italicBounds.Contains(point)) ToggleFontStyle(FontStyle.Italic);
                else return;
            }
        }

        SaveAndRefresh();
    }

    private bool HitAnyAction(Point point) =>
        _layoutBounds.Any(bounds => bounds.Contains(point)) ||
        _completionBounds.Any(bounds => bounds.Contains(point)) ||
        _behaviorStickyBounds.Contains(point) ||
        _behaviorSingleBounds.Contains(point) ||
        _colorBounds.Any(bounds => bounds.Contains(point)) ||
        _penMinusBounds.Contains(point) || _penPlusBounds.Contains(point) ||
        _mosaicMinusBounds.Contains(point) || _mosaicPlusBounds.Contains(point) ||
        _pixelMinusBounds.Contains(point) || _pixelPlusBounds.Contains(point) ||
        _arrowBounds.Any(bounds => bounds.Contains(point)) ||
        _boldBounds.Contains(point) || _italicBounds.Contains(point);

    private void AdjustPen(float delta) =>
        ConfigService.Current.AnnotationPenWidth = Math.Clamp(ConfigService.Current.AnnotationPenWidth + delta, 0.5f, 512f);

    private void AdjustMosaic(float delta) =>
        ConfigService.Current.AnnotationMosaicSize = Math.Clamp(ConfigService.Current.AnnotationMosaicSize + delta, 8f, 80f);

    private void AdjustPixel(int delta) =>
        ConfigService.Current.AnnotationMosaicPixelSize = Math.Clamp(ConfigService.Current.AnnotationMosaicPixelSize + delta, 4, 32);

    private static void ToggleFontStyle(FontStyle flag)
    {
        FontStyle style = (FontStyle)ConfigService.Current.AnnotationFontStyle;
        style = style.HasFlag(flag) ? style & ~flag : style | flag;
        ConfigService.Current.AnnotationFontStyle = (int)style;
    }

    private void SaveToolbarState(bool preserveLayout = false)
    {
        if (!preserveLayout) ConfigService.Current.CaptureToolbarLayout = CaptureToolbarLayout.Custom;
        ConfigService.Current.CaptureToolbarOrder = [.. _order];
        ConfigService.Current.CaptureToolbarItems = _order.Where(_enabled.Contains).ToList();
        if (ConfigService.Current.CaptureToolbarItems.Count == 0)
        {
            ConfigService.Current.CaptureToolbarItems.Add(CaptureToolbarItem.Confirm);
            _enabled.Add(CaptureToolbarItem.Confirm);
        }
        SaveAndRefresh();
    }

    private void ApplyLayout(CaptureToolbarLayout layout)
    {
        ConfigService.Current.CaptureToolbarLayout = layout;
        if (layout == CaptureToolbarLayout.Custom)
        {
            SaveAndRefresh();
            return;
        }

        List<CaptureToolbarItem> items = CaptureToolbarDefaults.CreateLayout(layout);
        _order.Clear();
        _order.AddRange(items);
        foreach (CaptureToolbarItem item in DefaultOrder)
        {
            if (!_order.Contains(item)) _order.Add(item);
        }

        _enabled.Clear();
        foreach (CaptureToolbarItem item in items) _enabled.Add(item);
        RebuildHitTargets();
        SaveToolbarState(preserveLayout: true);
    }

    private void NormalizeConfiguredLayout()
    {
        CaptureToolbarLayout layout = ConfigService.Current.CaptureToolbarLayout;
        if (!Enum.IsDefined(layout) || layout == CaptureToolbarLayout.Custom) return;

        List<CaptureToolbarItem> expected = CaptureToolbarDefaults.CreateLayout(layout);
        List<CaptureToolbarItem> actual = _order.Where(_enabled.Contains).ToList();
        if (!actual.SequenceEqual(expected)) ConfigService.Current.CaptureToolbarLayout = CaptureToolbarLayout.Custom;
    }

    private void SaveAndRefresh()
    {
        ConfigService.Save();
        Invalidate();
    }

    private List<CaptureToolbarItem> BuildOrder()
    {
        var configured = ConfigService.Current.CaptureToolbarOrder ?? [];
        var result = configured.Where(Enum.IsDefined).Distinct().ToList();
        foreach (CaptureToolbarItem item in ConfigService.Current.CaptureToolbarItems ?? [])
        {
            if (Enum.IsDefined(item) && !result.Contains(item)) result.Add(item);
        }
        foreach (CaptureToolbarItem item in DefaultOrder)
        {
            if (!result.Contains(item)) result.Add(item);
        }
        return result;
    }

    private void ConfigureCombo(ComboBox combo, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 24;
        combo.Font = new Font("Microsoft YaHei UI", 8f);
        combo.Size = new Size(width, 28);
        combo.DrawItem += DrawComboItem;
        combo.DropDown += (_, _) => combo.Invalidate();
    }

    private void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0) return;
        ThemePalette palette = ThemeManager.Palette;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? palette.AccentColor : palette.InputBg);
        e.Graphics.FillRectangle(background, e.Bounds);
        string text = combo.Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            combo.Font,
            Rectangle.Inflate(e.Bounds, -6, 0),
            selected ? palette.AccentForeground : palette.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private void PopulateFontFamilies()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            foreach (string name in installed.Families.Select(family => family.Name).Distinct().OrderBy(name => name))
            {
                _fontFamily.Items.Add(name);
            }
        }
        catch
        {
            _fontFamily.Items.AddRange(["Microsoft YaHei UI", "Segoe UI", "SimSun"]);
        }
    }

    private void ApplyTheme()
    {
        if (IsDisposed) return;
        ThemePalette palette = ThemeManager.Palette;
        _fontFamily.BackColor = _fontSize.BackColor = Color.FromArgb(255, palette.InputBg);
        _fontFamily.ForeColor = _fontSize.ForeColor = palette.TextPrimary;
        _fontFamily.Invalidate();
        _fontSize.Invalidate();
        Invalidate();
    }

    private static int HitTest(IReadOnlyList<Rectangle> bounds, Point point)
    {
        for (int i = 0; i < bounds.Count; i++)
        {
            if (bounds[i].Contains(point)) return i;
        }
        return -1;
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try { return ColorTranslator.FromHtml(value); }
        catch { return fallback; }
    }

    private static Color Blend(Color source, Color target, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            255,
            (int)Math.Round(source.R + (target.R - source.R) * amount),
            (int)Math.Round(source.G + (target.G - source.G) * amount),
            (int)Math.Round(source.B + (target.B - source.B) * amount));
    }

    private static string ShortName(CaptureToolbarItem item) => item switch
    {
        CaptureToolbarItem.Pen => "画笔",
        CaptureToolbarItem.Arrow => "箭头",
        CaptureToolbarItem.Text => "文字",
        CaptureToolbarItem.Mosaic => "马赛克",
        CaptureToolbarItem.Style => "样式",
        CaptureToolbarItem.Undo => "撤销",
        CaptureToolbarItem.Redo => "重做",
        CaptureToolbarItem.Cursor => "指针",
        CaptureToolbarItem.ScrollCapture => "长截图",
        CaptureToolbarItem.Ocr => "OCR",
        CaptureToolbarItem.Copy => "复制",
        CaptureToolbarItem.Save => "保存",
        CaptureToolbarItem.Pin => "贴图",
        CaptureToolbarItem.Reset => "重选",
        CaptureToolbarItem.Cancel => "取消",
        CaptureToolbarItem.Confirm => "完成",
        _ => item.DisplayName()
    };

    private static LucideIcon IconFor(CaptureToolbarItem item) => item switch
    {
        CaptureToolbarItem.Pen => LucideIcon.PenLine,
        CaptureToolbarItem.Arrow => LucideIcon.ArrowUpRight,
        CaptureToolbarItem.Text => LucideIcon.Type,
        CaptureToolbarItem.Mosaic => LucideIcon.Grid3X3,
        CaptureToolbarItem.Style => LucideIcon.Palette,
        CaptureToolbarItem.Undo => LucideIcon.Undo2,
        CaptureToolbarItem.Redo => LucideIcon.Redo2,
        CaptureToolbarItem.Cursor => LucideIcon.MousePointer2,
        CaptureToolbarItem.ScrollCapture => LucideIcon.GalleryVerticalEnd,
        CaptureToolbarItem.Ocr => LucideIcon.FileText,
        CaptureToolbarItem.Copy => LucideIcon.Copy,
        CaptureToolbarItem.Save => LucideIcon.Folder,
        CaptureToolbarItem.Pin => LucideIcon.Pin,
        CaptureToolbarItem.Reset => LucideIcon.RotateCcw,
        CaptureToolbarItem.Cancel => LucideIcon.X,
        CaptureToolbarItem.Confirm => LucideIcon.Check,
        _ => LucideIcon.Sparkles
    };
}
