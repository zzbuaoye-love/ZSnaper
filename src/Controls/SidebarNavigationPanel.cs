using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

/// <summary>
/// Sidebar host panel that renders a continuous sliding active pill and accent bar behind <see cref="NavMenuButton"/> items.
/// </summary>
internal sealed class SidebarNavigationPanel : Panel
{
    private readonly SmoothValueAnimator _pillTopAnimator;
    private readonly SmoothValueAnimator _pillHeightAnimator;
    private readonly List<NavMenuButton> _buttons = [];
    private int _activeIndex;

    public SidebarNavigationPanel()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _pillTopAnimator = new SmoothValueAnimator(this, 0f, HandlePillTick, defaultDurationMs: 185, settleTolerance: 0.2f);
        _pillHeightAnimator = new SmoothValueAnimator(this, 30f, HandlePillTick, defaultDurationMs: 185, settleTolerance: 0.2f);

        BackColor = ThemeManager.Palette.SidebarBg;
        ThemeManager.ThemeChanged += ApplyTheme;
    }

    public void RegisterButton(NavMenuButton button)
    {
        button.UseParentActivePill = true;
        _buttons.Add(button);
        Controls.Add(button);
    }

    public void SetActiveIndex(int index, bool animate)
    {
        if (index < 0 || index >= _buttons.Count) return;
        _activeIndex = index;

        for (int i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].IsActive = i == index;
            if (!animate)
            {
                _buttons[i].SnapActiveState();
            }
        }

        RectangleF targetPill = GetButtonPillInPanel(_buttons[index]);
        if (animate && IsHandleCreated)
        {
            _pillTopAnimator.SetTarget(targetPill.Top);
            _pillHeightAnimator.SetTarget(targetPill.Height);
        }
        else
        {
            _pillTopAnimator.SnapTo(targetPill.Top);
            _pillHeightAnimator.SnapTo(targetPill.Height);
        }

        Invalidate(true);
    }

    public void SnapPillToActive()
    {
        if (_activeIndex < 0 || _activeIndex >= _buttons.Count) return;
        RectangleF pill = GetButtonPillInPanel(_buttons[_activeIndex]);
        _pillTopAnimator.SnapTo(pill.Top);
        _pillHeightAnimator.SnapTo(pill.Height);
        _buttons[_activeIndex].SnapActiveState();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (!_pillTopAnimator.IsAnimating && _activeIndex >= 0 && _activeIndex < _buttons.Count)
        {
            RectangleF pill = GetButtonPillInPanel(_buttons[_activeIndex]);
            _pillTopAnimator.SnapTo(pill.Top);
            _pillHeightAnimator.SnapTo(pill.Height);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_activeIndex < 0 || _activeIndex >= _buttons.Count || Width <= 0 || Height <= 0)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        ThemePalette palette = ThemeManager.Palette;
        NavMenuButton activeButton = _buttons[_activeIndex];
        RectangleF localPill = activeButton.GetPillBounds();
        var pillRect = new RectangleF(
            activeButton.Left + localPill.Left,
            _pillTopAnimator.Value,
            localPill.Width,
            Math.Max(8f, _pillHeightAnimator.Value));

        float scale = DeviceDpi / 96f;

        // Group divider separating workspace destinations from bottom utility controls (Settings / About).
        if (_buttons.Count >= 3)
        {
            NavMenuButton lastPrimary = _buttons[_buttons.Count - 3];
            NavMenuButton firstUtility = _buttons[_buttons.Count - 2];
            float gapMidY = Math.Min(firstUtility.Top - 6f * scale, (lastPrimary.Bottom + firstUtility.Top) / 2f);
            if (gapMidY > lastPrimary.Top + 4f * scale)
            {
                float insetX = Math.Max(10f * scale, Width * 0.22f);
                using var dividerPen = new Pen(palette.SeparatorColor, Math.Max(1f, scale));
                g.DrawLine(dividerPen, insetX, gapMidY, Width - insetX, gapMidY);
            }
        }

        float corner = 12f * scale;
        bool isLight = palette.Mode == Models.ThemeMode.Light;

        var shadowRect = new RectangleF(pillRect.Left, pillRect.Top + 1.5f * scale, pillRect.Width, pillRect.Height);
        using (GraphicsPath shadowPath = GraphicsHelper.GetRoundedRectangle(shadowRect, corner))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(isLight ? 16 : 48, 0, 0, 0)))
        {
            g.FillPath(shadowBrush, shadowPath);
        }

        Color activeSurface = isLight
            ? GraphicsHelper.BlendColor(Color.White, palette.AccentColor, 0.045f)
            : GraphicsHelper.BlendColor(palette.SurfaceContainerHigh, palette.AccentColor, 0.14f);
        Color activeRim = isLight
            ? GraphicsHelper.BlendColor(palette.SidebarBorder, palette.AccentColor, 0.22f)
            : Color.FromArgb(44, 255, 255, 255);

        using (GraphicsPath pillPath = GraphicsHelper.GetRoundedRectangle(pillRect, corner))
        {
            using (var pillBrush = new SolidBrush(activeSurface))
            {
                g.FillPath(pillBrush, pillPath);
            }

            using (var rimPen = new Pen(activeRim, Math.Max(1f, scale)))
            {
                g.DrawPath(rimPen, pillPath);
            }
        }

        // Slender vertical accent indicator aligned inside the elevated floating pill
        float barWidth = Math.Max(2.8f, 3f * scale);
        float barHeight = Math.Clamp(pillRect.Height * 0.48f, 11f * scale, 18f * scale);
        var accentBar = new RectangleF(
            pillRect.Left + 3.5f * scale,
            pillRect.Top + (pillRect.Height - barHeight) / 2f,
            barWidth,
            barHeight);
        using (GraphicsPath barPath = GraphicsHelper.GetRoundedRectangle(accentBar, barWidth / 2f))
        using (var barBrush = new SolidBrush(palette.AccentColor))
        {
            g.FillPath(barBrush, barPath);
        }
    }

    private static RectangleF GetButtonPillInPanel(NavMenuButton button)
    {
        RectangleF local = button.GetPillBounds();
        return new RectangleF(button.Left + local.Left, button.Top + local.Top, local.Width, local.Height);
    }

    private void HandlePillTick()
    {
        if (!IsDisposed)
        {
            Invalidate(true);
        }
    }

    private void ApplyTheme()
    {
        BackColor = ThemeManager.Palette.SidebarBg;
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= ApplyTheme;
            _pillTopAnimator.Dispose();
            _pillHeightAnimator.Dispose();
        }
        base.Dispose(disposing);
    }
}
