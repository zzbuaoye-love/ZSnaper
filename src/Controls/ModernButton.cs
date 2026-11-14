using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class ModernButton : Control
{
    private readonly SmoothValueAnimator _hoverAnimator;
    private readonly SmoothValueAnimator _pressAnimator;
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

    public int CornerRadius { get; set; } = 20;

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
        AccessibleRole = AccessibleRole.PushButton;
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 120);
        _pressAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 85);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hoverAnimator.SetTarget(1f);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverAnimator.SetTarget(0f);
        _pressAnimator.SetTarget(0f);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _pressAnimator.SetTarget(1f);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressAnimator.SetTarget(0f);
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
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var palette = ThemeManager.Palette;
        float hover = Math.Clamp(_hoverAnimator.Value, 0f, 1f);
        float press = Math.Clamp(_pressAnimator.Value, 0f, 1f);
        float pressedOffset = press;
        var rectF = new RectangleF(0.5f, 0.5f + pressedOffset, Width - 1.5f, Height - 1.5f - pressedOffset);
        var rect = new Rectangle(0, (int)Math.Round(pressedOffset), Width - 1, Math.Max(1, Height - 1 - (int)Math.Round(pressedOffset)));
        if (rectF.Width <= 0 || rectF.Height <= 0) return;

        if (IsIconOnly)
        {
            if (_icon is LucideIcon iconOnly)
            {
                int iconSize = Math.Clamp(GraphicsHelper.Dip(this, IconSize), GraphicsHelper.Dip(this, 10), Math.Min(rect.Width, rect.Height));
                Color hoverColor = GraphicsHelper.BlendColor(palette.TextSecondary, palette.TextPrimary, hover);
                Color iconColor = GraphicsHelper.BlendColor(hoverColor, palette.AccentColor, press);
                float iconX = rectF.Left + (rectF.Width - iconSize) / 2f;
                float iconY = rectF.Top + (rectF.Height - iconSize) / 2f;
                LucideRenderer.Draw(g, iconOnly, iconX, iconY, iconSize, iconColor, 1.8f);
            }

            return;
        }

        using var path = GraphicsHelper.GetRoundedRectangle(rectF, GraphicsHelper.Dip(this, CornerRadius));

        Color fillBg;
        Color textColor;
        Color borderColor = Color.Transparent;

        if (_isPrimary)
        {
            Color hoverTarget = GraphicsHelper.BlendColor(palette.AccentColor, Color.White, 0.12f * hover);
            fillBg = GraphicsHelper.BlendColor(hoverTarget, Color.Black, 0.14f * press);
            textColor = palette.AccentForeground;
        }
        else
        {
            Color hoverTarget = GraphicsHelper.BlendColor(palette.CardBg, palette.TextPrimary, 0.05f * hover);
            fillBg = GraphicsHelper.BlendColor(hoverTarget, palette.TextPrimary, 0.05f * press);
            textColor = palette.TextPrimary;
            borderColor = GraphicsHelper.BlendColor(palette.CardBorder, palette.AccentColor, 0.25f * hover);
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

        if (Focused && ShowFocusCues)
        {
            var focusRect = Rectangle.Inflate(rect, -2, -2);
            using var focusPath = GraphicsHelper.GetRoundedRectangle(focusRect, Math.Max(2, CornerRadius - 2));
            using var focusPen = new Pen(Color.FromArgb(150, textColor), 1f);
            g.DrawPath(focusPen, focusPath);
        }

        if (_icon is LucideIcon icon)
        {
            int iconSize = Math.Clamp(GraphicsHelper.Dip(this, IconSize), GraphicsHelper.Dip(this, 10), Math.Max(10, rect.Height - GraphicsHelper.Dip(this, 8)));
            Size textSize = string.IsNullOrEmpty(_text)
                ? Size.Empty
                : TextRenderer.MeasureText(g, _text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int gap = textSize.Width > 0 ? GraphicsHelper.Dip(this, IconGap) : 0;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverAnimator.Dispose();
            _pressAnimator.Dispose();
        }
        base.Dispose(disposing);
    }
}

public enum IconPosition
{
    Left,
    Right
}
