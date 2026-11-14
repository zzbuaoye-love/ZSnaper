using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class WindowControls : Control
{
    private int ButtonWidth => GraphicsHelper.Dip(this, 36);
    private const int ButtonCount = 2;
    private int _hoveredButton = -1;
    private int _pressedButton = -1;

    public WindowControls()
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
        Size = new Size(ButtonWidth * ButtonCount, 30);
        TabStop = false;
        ThemeManager.ThemeChanged += HandleThemeChanged;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hovered = e.X >= 0 && e.X < Width && e.Y >= 0 && e.Y < Height
            ? Math.Clamp(e.X / ButtonWidth, 0, ButtonCount - 1)
            : -1;
        if (_hoveredButton != hovered)
        {
            _hoveredButton = hovered;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredButton = -1;
        _pressedButton = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressedButton = Math.Clamp(e.X / ButtonWidth, 0, ButtonCount - 1);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int releasedButton = e.X >= 0 && e.X < Width && e.Y >= 0 && e.Y < Height
            ? Math.Clamp(e.X / ButtonWidth, 0, ButtonCount - 1)
            : -1;
        int pressedButton = _pressedButton;
        _pressedButton = -1;
        Invalidate();

        if (e.Button != MouseButtons.Left || releasedButton != pressedButton)
        {
            return;
        }

        Form? window = FindForm();
        if (window == null) return;
        switch (releasedButton)
        {
            case 0:
                window.WindowState = FormWindowState.Minimized;
                break;
            case 1:
                window.Hide();
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        ThemePalette palette = ThemeManager.Palette;

        for (int index = 0; index < ButtonCount; index++)
        {
            var buttonBounds = new Rectangle(index * ButtonWidth + 2, 2, ButtonWidth - 4, Height - 4);
            if (_hoveredButton == index || _pressedButton == index)
            {
                DrawHoverBackground(
                    graphics,
                    buttonBounds,
                    isClose: index == ButtonCount - 1,
                    isPressed: _pressedButton == index,
                    palette);
            }

            bool closeActive = index == ButtonCount - 1 &&
                (_hoveredButton == index || _pressedButton == index);
            Color glyphColor = closeActive
                ? Color.White
                : palette.WindowControlText;
            DrawCenteredIcon(
                graphics,
                index,
                index == 0 ? LucideIcon.Minus : LucideIcon.X,
                glyphColor);
        }
    }

    private void DrawCenteredIcon(Graphics graphics, int buttonIndex, LucideIcon icon, Color color)
    {
        float iconSize = GraphicsHelper.Dip(this, 16);
        float x = buttonIndex * ButtonWidth + (ButtonWidth - iconSize) / 2f;
        float y = (Height - iconSize) / 2f;
        LucideRenderer.Draw(graphics, icon, x, y, iconSize, color);
    }

    private static void DrawHoverBackground(
        Graphics graphics,
        Rectangle bounds,
        bool isClose,
        bool isPressed,
        ThemePalette palette)
    {
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 7);
        Color fill = isClose
            ? isPressed ? Color.FromArgb(190, 36, 48) : Color.FromArgb(222, 49, 63)
            : palette.WindowControlHover;
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
    }

    private void HandleThemeChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= HandleThemeChanged;
        }

        base.Dispose(disposing);
    }
}
