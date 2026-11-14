using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

/// <summary>
/// Shared subpixel renderer for animated segmented pill controls.
/// </summary>
internal static class SegmentedPillRenderer
{
    public static int HitTest(int width, int count, int x)
    {
        if (count <= 0 || width <= 0) return -1;
        float segmentWidth = width / (float)count;
        return Math.Clamp((int)(x / segmentWidth), 0, count - 1);
    }

    public static void Draw(
        Control control,
        Graphics graphics,
        IReadOnlyList<string> labels,
        float sliderIndex,
        int hoveredIndex,
        float hoverOpacity,
        bool borderedCardStyle,
        bool focusedAndShowCues)
    {
        if (control.Width <= 2 || control.Height <= 2 || labels.Count == 0) return;

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        ThemePalette palette = ThemeManager.Palette;
        int count = labels.Count;

        var outerRect = new RectangleF(0.5f, 0.5f, control.Width - 1.5f, control.Height - 1.5f);
        using (GraphicsPath bgPath = GraphicsHelper.GetRoundedRectangle(outerRect, outerRect.Height / 2f))
        {
            Color bgFill = borderedCardStyle
                ? palette.CardBg
                : palette.Mode == ThemeMode.Dark
                    ? Color.FromArgb(40, 255, 255, 255)
                    : Color.FromArgb(20, 0, 0, 0);
            using var bgBrush = new SolidBrush(bgFill);
            graphics.FillPath(bgBrush, bgPath);

            if (borderedCardStyle || focusedAndShowCues)
            {
                Color outlineColor = focusedAndShowCues ? palette.AccentColor : palette.InputBorder;
                using var outlinePen = new Pen(outlineColor, focusedAndShowCues ? 1.8f : 1f);
                graphics.DrawPath(outlinePen, bgPath);
            }
        }

        float inset = borderedCardStyle ? 2.5f : 2f;
        float innerWidth = Math.Max(4f, control.Width - inset * 2f - 0.5f);
        float segmentWidth = innerWidth / count;
        float pillHeight = Math.Max(4f, control.Height - inset * 2f - 0.5f);

        if (hoveredIndex >= 0 && hoveredIndex < count && hoverOpacity > 0.01f)
        {
            var hoverRect = new RectangleF(
                inset + hoveredIndex * segmentWidth + 1f,
                inset + 0.5f,
                Math.Max(2f, segmentWidth - 2f),
                Math.Max(2f, pillHeight - 1f));
            int alpha = (int)Math.Round((palette.Mode == ThemeMode.Dark ? 24 : 14) * Math.Clamp(hoverOpacity, 0f, 1f));
            using GraphicsPath hoverPath = GraphicsHelper.GetRoundedRectangle(hoverRect, hoverRect.Height / 2f);
            using var hoverBrush = new SolidBrush(Color.FromArgb(alpha, palette.TextPrimary));
            graphics.FillPath(hoverBrush, hoverPath);
        }

        float clampedSlider = Math.Clamp(sliderIndex, 0f, count - 1);
        var activeRect = new RectangleF(inset + clampedSlider * segmentWidth, inset, segmentWidth, pillHeight);
        Color activeFill = borderedCardStyle ? palette.PrimaryContainer : palette.AccentColor;
        using (GraphicsPath activePath = GraphicsHelper.GetRoundedRectangle(activeRect, pillHeight / 2f))
        using (var activeBrush = new SolidBrush(activeFill))
        {
            graphics.FillPath(activeBrush, activePath);
        }

        Color activeText = borderedCardStyle ? palette.OnPrimaryContainer : palette.AccentForeground;
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };

        for (int i = 0; i < count; i++)
        {
            float activeWeight = Math.Clamp(1f - Math.Abs(clampedSlider - i), 0f, 1f);
            Color idleColor = hoveredIndex == i
                ? GraphicsHelper.BlendColor(palette.TextMuted, palette.TextPrimary, 0.5f * Math.Clamp(hoverOpacity, 0f, 1f))
                : palette.TextMuted;
            Color textColor = GraphicsHelper.BlendColor(idleColor, activeText, activeWeight);
            var textRect = new RectangleF(inset + i * segmentWidth, 0f, segmentWidth, control.Height);
            using var textBrush = new SolidBrush(textColor);
            graphics.DrawString(labels[i], control.Font, textBrush, textRect, sf);
        }
    }
}
