using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

/// <summary>
/// 版本发布通道分段选择器 (Release / Beta)
/// </summary>
public class ChannelSegmentedControl : Control
{
    private static readonly string[] Labels = ["正式版", "测试版"];
    private readonly SmoothValueAnimator _sliderAnimator;
    private readonly SmoothValueAnimator _hoverAnimator;
    private int _hoveredIndex = -1;
    private int _paintHoveredIndex = -1;

    public ChannelSegmentedControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(136, 28);
        Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;

        float initial = ChannelToIndex(ConfigService.Current.UpdateChannel);
        _sliderAnimator = new SmoothValueAnimator(this, initial, Invalidate, defaultDurationMs: 170);
        _hoverAnimator = new SmoothValueAnimator(this, 0f, Invalidate, defaultDurationMs: 110);

        ThemeManager.ThemeChanged += Invalidate;
        ConfigService.ConfigChanged += OnConfigChanged;
    }

    public static int ChannelToIndex(string? channel) => channel?.ToLowerInvariant() switch
    {
        "release" or "stable" => 0,
        _ => 1
    };

    public static string IndexToChannel(int index) => index == 0 ? "Release" : "Beta";

    private void OnConfigChanged()
    {
        _sliderAnimator.SetTarget(ChannelToIndex(ConfigService.Current.UpdateChannel));
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
        if (selected >= 0) SelectChannel(selected);
    }

    private void SelectChannel(int index)
    {
        int clamped = Math.Clamp(index, 0, Labels.Length - 1);
        string channel = IndexToChannel(clamped);
        if (!string.Equals(ConfigService.Current.UpdateChannel, channel, StringComparison.OrdinalIgnoreCase))
        {
            ConfigService.Current.UpdateChannel = channel;
            ConfigService.Save();
            _sliderAnimator.SetTarget(clamped);
            AccessibilityNotifyClients(AccessibleEvents.Selection, clamped + 1);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int current = ChannelToIndex(ConfigService.Current.UpdateChannel);
        int next = e.KeyCode switch
        {
            Keys.Left => current - 1,
            Keys.Right => current + 1,
            Keys.Home => 0,
            Keys.End => Labels.Length - 1,
            _ => current
        };
        if (next == current) return;
        SelectChannel(next);
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedControlAccessibility(
        this, Labels, () => ChannelToIndex(ConfigService.Current.UpdateChannel), SelectChannel);

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
            ThemeManager.ThemeChanged -= Invalidate;
            ConfigService.ConfigChanged -= OnConfigChanged;
            _sliderAnimator.Dispose();
            _hoverAnimator.Dispose();
        }
        base.Dispose(disposing);
    }
}
