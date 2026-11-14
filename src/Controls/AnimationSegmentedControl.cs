using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class AnimationSegmentedControl : Control
{
    private static readonly string[] Labels = ["精简", "默认", "优雅"];
    private readonly SmoothValueAnimator _sliderAnimator;
    private readonly SmoothValueAnimator _hoverAnimator;
    private int _hoveredIndex = -1;
    private int _paintHoveredIndex = -1;

    public AnimationSegmentedControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(210, 36);
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;

        float initial = Math.Clamp((int)ConfigService.Current.AnimationMode, 0, Labels.Length - 1);
        _sliderAnimator = new SmoothValueAnimator(this, initial, Invalidate, defaultDurationMs: 175);
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 110);

        ThemeManager.ThemeChanged += Invalidate;
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
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            int selected = SegmentedPillRenderer.HitTest(Width, Labels.Length, e.X);
            if (selected >= 0) SelectMode(selected);
        }
    }

    private void SelectMode(int index)
    {
        int clamped = Math.Clamp(index, 0, Labels.Length - 1);
        ConfigService.Current.AnimationMode = (AnimationLevel)clamped;
        ConfigService.Save();
        _sliderAnimator.SetTarget(clamped);
        AccessibilityNotifyClients(AccessibleEvents.Selection, clamped + 1);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int current = (int)ConfigService.Current.AnimationMode;
        int next = e.KeyCode switch
        {
            Keys.Left => current - 1,
            Keys.Right => current + 1,
            Keys.Home => 0,
            Keys.End => Labels.Length - 1,
            _ => current
        };
        if (next == current) return;
        SelectMode(next);
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedControlAccessibility(
        this, Labels, () => (int)ConfigService.Current.AnimationMode, SelectMode);

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
            borderedCardStyle: true,
            Focused && ShowFocusCues);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= Invalidate;
            _sliderAnimator.Dispose();
            _hoverAnimator.Dispose();
        }
        base.Dispose(disposing);
    }
}
