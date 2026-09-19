using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class ModernButton : Control
{
    private bool _isHovered;
    private bool _isPressed;
    private string _text = "按钮";
    private bool _isPrimary = true;
    private LucideIcon? _icon;

    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            _isPrimary = value;
            Invalidate();
        }
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            Invalidate();
        }
    }

    public int CornerRadius { get; set; } = 6;

    public LucideIcon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Invalidate();
        }
    }

    public int IconSize { get; set; } = 16;

    public int IconGap { get; set; } = 7;

    public IconPosition IconPosition { get; set; } = IconPosition.Left;

    /// <summary>
    /// 仅绘制图标，不绘制文字、按钮背景或边框。
    /// </summary>
    public bool IsIconOnly { get; set; }

    public ModernButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(130, 38);
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
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
        _isPressed = false;
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
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var palette = ThemeManager.Palette;
        int pressedOffset = _isPressed ? 1 : 0;
        var rect = new Rectangle(0, pressedOffset, Width - 1, Height - 1 - pressedOffset);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (IsIconOnly)
        {
            if (_icon is LucideIcon iconOnly)
            {
                int iconSize = Math.Clamp(IconSize, 10, Math.Min(rect.Width, rect.Height));
                Color iconColor = _isPressed
                    ? palette.AccentColor
                    : _isHovered
                        ? palette.TextPrimary
                        : palette.TextSecondary;
                float iconX = rect.Left + (rect.Width - iconSize) / 2f;
                float iconY = rect.Top + (rect.Height - iconSize) / 2f;
                LucideRenderer.Draw(g, iconOnly, iconX, iconY, iconSize, iconColor, 1.8f);
            }

            return;
        }

        using var path = GraphicsHelper.GetRoundedRectangle(rect, CornerRadius);

        Color fillBg;
        Color textColor;
        Color borderColor = Color.Transparent;

        if (_isPrimary)
        {
            fillBg = _isPressed
                ? Blend(palette.AccentColor, Color.Black, 0.14f)
                : _isHovered
                    ? Blend(palette.AccentColor, Color.White, 0.10f)
                    : palette.AccentColor;

            textColor = palette.AccentForeground;
        }
        else
        {
            fillBg = _isPressed
                ? palette.NavItemHover
                : _isHovered
                    ? Color.FromArgb(palette.Mode == Models.ThemeMode.Dark ? 38 : 22, palette.TextPrimary)
                    : palette.CardBg;
            textColor = palette.TextPrimary;
            borderColor = palette.CardBorder;
        }

        if (!Enabled)
        {
            fillBg = palette.InputBg;
            textColor = palette.TextMuted;
            borderColor = palette.CardBorder;
        }

        using (var brush = new SolidBrush(fillBg))
        {
            g.FillPath(brush, path);
        }

        if (borderColor != Color.Transparent)
        {
            using var pen = new Pen(borderColor, 1f);
            g.DrawPath(pen, path);
        }

        if (Focused)
        {
            var focusRect = Rectangle.Inflate(rect, -2, -2);
            using var focusPath = GraphicsHelper.GetRoundedRectangle(focusRect, Math.Max(2, CornerRadius - 2));
            using var focusPen = new Pen(Color.FromArgb(150, palette.AccentColor), 1f) { DashStyle = DashStyle.Dot };
            g.DrawPath(focusPen, focusPath);
        }

        if (_icon is LucideIcon icon)
        {
            int iconSize = Math.Clamp(IconSize, 10, Math.Max(10, rect.Height - 8));
            Size textSize = string.IsNullOrEmpty(_text)
                ? Size.Empty
                : TextRenderer.MeasureText(g, _text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int gap = textSize.Width > 0 ? IconGap : 0;
            int groupWidth = iconSize + gap + textSize.Width;

            if (IconPosition == IconPosition.Right && textSize.Width > 0)
            {
                float textX = rect.Left + (rect.Width - groupWidth) / 2f;
                var textRect = new Rectangle(
                    (int)Math.Round(textX),
                    rect.Top,
                    textSize.Width + 2,
                    rect.Height);
                TextRenderer.DrawText(
                    g,
                    _text,
                    Font,
                    textRect,
                    textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                float iconX = textX + textSize.Width + gap;
                float iconY = rect.Top + (rect.Height - iconSize) / 2f;
                LucideRenderer.Draw(g, icon, iconX, iconY, iconSize, textColor, 1.8f);
            }
            else
            {
                float iconX = rect.Left + (rect.Width - groupWidth) / 2f;
                float iconY = rect.Top + (rect.Height - iconSize) / 2f;
                LucideRenderer.Draw(g, icon, iconX, iconY, iconSize, textColor, 1.8f);

                if (textSize.Width > 0)
                {
                    var textRect = new Rectangle(
                        (int)Math.Round(iconX + iconSize + gap),
                        rect.Top,
                        textSize.Width + 2,
                        rect.Height);
                    TextRenderer.DrawText(
                        g,
                        _text,
                        Font,
                        textRect,
                        textColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }
        else
        {
            using var textBrush = new SolidBrush(textColor);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(_text, Font, textBrush, rect, sf);
        }
    }

    private static Color Blend(Color source, Color target, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            source.A,
            (int)Math.Round(source.R + (target.R - source.R) * amount),
            (int)Math.Round(source.G + (target.G - source.G) * amount),
            (int)Math.Round(source.B + (target.B - source.B) * amount));
    }
}

public enum IconPosition
{
    Left,
    Right
}
