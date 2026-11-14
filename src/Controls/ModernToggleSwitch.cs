using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class ModernToggleSwitch : Control
{
    private bool _checked = true;
    private readonly SmoothValueAnimator _progressAnimator;
    private readonly SmoothValueAnimator _hoverAnimator;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked != value)
            {
                _checked = value;
                _progressAnimator.SetTarget(value ? 1f : 0f);
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? CheckedChanged;

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessibility(this);
    private sealed class SwitchAccessibility(ModernToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
        public override string DefaultAction => "切换";
        public override void DoDefaultAction() { if (owner.Enabled) owner.Checked = !owner.Checked; }
    }

    public ModernToggleSwitch()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(52, 32);
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;

        _progressAnimator = new SmoothValueAnimator(this, 1f, Invalidate, defaultDurationMs: 170);
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 110);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hoverAnimator.SetTarget(1f);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverAnimator.SetTarget(0f);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            Checked = !Checked;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var palette = ThemeManager.Palette;
        float progress = Math.Clamp(_progressAnimator.Value, 0f, 1f);
        float hover = Math.Clamp(_hoverAnimator.Value, 0f, 1f);

        var rect = new RectangleF(1f, 1f, Width - 2.5f, Height - 2.5f);
        using var path = GraphicsHelper.GetRoundedRectangle(rect, rect.Height / 2f);

        Color offColor = GraphicsHelper.BlendColor(palette.SurfaceContainerHigh, palette.TextPrimary, 0.05f * hover);
        Color onColor = GraphicsHelper.BlendColor(palette.AccentColor, palette.AccentForeground, 0.08f * hover);
        Color trackColor = GraphicsHelper.BlendColor(offColor, onColor, progress);

        using (var brush = new SolidBrush(trackColor))
        {
            g.FillPath(brush, path);
        }

        Color borderOff = GraphicsHelper.BlendColor(palette.InputBorder, palette.TextPrimary, 0.25f * hover);
        using (var outline = new Pen(GraphicsHelper.BlendColor(borderOff, onColor, progress), 1.8f))
        {
            g.DrawPath(outline, path);
        }

        float scale = DeviceDpi / 96f;
        float thumbBase = 16f + 8f * progress;
        float thumbSize = (thumbBase + 1.4f * hover) * scale;
        float startX = 8f * scale - 0.7f * hover * scale;
        float endX = Width - thumbSize - 3.5f * scale;
        float currentX = startX + (endX - startX) * progress;

        var thumbRect = new RectangleF(currentX, (Height - thumbSize) / 2f, thumbSize, thumbSize);
        Color thumbColor = GraphicsHelper.BlendColor(palette.InputBorder, palette.AccentForeground, progress);
        using (var thumbBrush = new SolidBrush(thumbColor))
        {
            g.FillEllipse(thumbBrush, thumbRect);
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(palette.AccentColor, 1.5f);
            g.DrawPath(focus, path);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _progressAnimator.Dispose();
            _hoverAnimator.Dispose();
        }
        base.Dispose(disposing);
    }
}
