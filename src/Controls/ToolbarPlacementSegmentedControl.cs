using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class ToolbarPlacementSegmentedControl : Control
{
    private static readonly string[] Labels = ["左", "中", "右", "Auto"];
    private readonly SmoothValueAnimator _sliderAnimator;
    private readonly SmoothValueAnimator _hoverAnimator;
    private int _hoveredIndex = -1;
    private int _paintHoveredIndex = -1;

    public ToolbarPlacementSegmentedControl()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(204, 28);
        Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;

        float initial = Math.Clamp((int)ConfigService.Current.ToolbarPlacement, 0, Labels.Length - 1);
        _sliderAnimator = new SmoothValueAnimator(this, initial, Invalidate, defaultDurationMs: 170);
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 110);
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hovered = SegmentedPillRenderer.HitTest(Width, Labels.Length, e.X);
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
        _hoveredIndex = -1;
        _hoverAnimator.SetTarget(0f);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        int selected = SegmentedPillRenderer.HitTest(Width, Labels.Length, e.X);
        if (selected >= 0) SelectPlacement(selected);
    }

    private void SelectPlacement(int index)
    {
        int clamped = Math.Clamp(index, 0, Labels.Length - 1);
        var placement = (ToolbarPlacementMode)clamped;
        if (ConfigService.Current.ToolbarPlacement == placement) return;

        ConfigService.Current.ToolbarPlacement = placement;
        ConfigService.Save();
        _sliderAnimator.SetTarget(clamped);
        AccessibilityNotifyClients(AccessibleEvents.Selection, clamped + 1);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int current = Math.Clamp((int)ConfigService.Current.ToolbarPlacement, 0, Labels.Length - 1);
        int next = e.KeyCode switch
        {
            Keys.Left => current - 1,
            Keys.Right => current + 1,
            Keys.Home => 0,
            Keys.End => Labels.Length - 1,
            _ => current
        };
        if (next == current) return;
        SelectPlacement(next);
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedControlAccessibility(
        this, Labels, () => Math.Clamp((int)ConfigService.Current.ToolbarPlacement, 0, Labels.Length - 1), SelectPlacement);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        SegmentedPillRenderer.Draw(
            this,
            e.Graphics,
            Labels,
            _sliderAnimator.Value,
            _paintHoveredIndex,
            _hoverAnimator.Value,
            borderedCardStyle: false,
            Focused && ShowFocusCues);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _sliderAnimator.Dispose();
            _hoverAnimator.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnThemeChanged() => Invalidate();
}
