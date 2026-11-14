using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class ThemeSegmentedControl : Control
{
    private static readonly string[] Labels = ["浅色", "深色"];
    private readonly SmoothValueAnimator _sliderAnimator;
    private readonly SmoothValueAnimator _hoverAnimator;
    private int _hoveredIndex = -1;
    private int _paintHoveredIndex = -1;

    public ThemeSegmentedControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(160, 36);
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;

        float initial = ThemeManager.CurrentMode == ThemeMode.Light ? 0f : 1f;
        _sliderAnimator = new SmoothValueAnimator(this, initial, Invalidate, defaultDurationMs: 175);
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 110);

        ThemeManager.ThemeChanged += OnGlobalThemeChanged;
    }

    private void OnGlobalThemeChanged()
    {
        AccessibilityNotifyClients(AccessibleEvents.Selection, (int)ThemeManager.CurrentMode + 1);
        _sliderAnimator.SetTarget(ThemeManager.CurrentMode == ThemeMode.Light ? 0f : 1f);
        Invalidate();
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
        ThemeMode next = e.X < Width / 2 ? ThemeMode.Light : ThemeMode.Dark;
        if (ThemeManager.CurrentMode != next)
        {
            ThemeManager.CurrentMode = next;
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Home) ThemeManager.CurrentMode = ThemeMode.Light;
        else if (e.KeyCode is Keys.Right or Keys.End) ThemeManager.CurrentMode = ThemeMode.Dark;
        else if (e.KeyCode is Keys.Space or Keys.Enter) ThemeManager.ToggleTheme();
        else return;
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedControlAccessibility(
        this, Labels, () => (int)ThemeManager.CurrentMode, index => ThemeManager.CurrentMode = (ThemeMode)index);

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
            ThemeManager.ThemeChanged -= OnGlobalThemeChanged;
            _sliderAnimator.Dispose();
            _hoverAnimator.Dispose();
        }
        base.Dispose(disposing);
    }
}
