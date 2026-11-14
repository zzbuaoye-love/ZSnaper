using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class HotkeyRecorder : Control
{
    private HotkeyGesture _gesture;
    private bool _hasGesture;
    private bool _isHovered;
    private bool _isRecording;

    public Func<HotkeyGesture, HotkeyChangeResult>? TryCommit { get; set; }
    public Func<HotkeyChangeResult>? BeginRecordingRequest { get; set; }
    public Func<HotkeyChangeResult>? EndRecordingRequest { get; set; }

    public event Action<HotkeyChangeResult>? Feedback;
    public event Action? RecordingStateChanged;

    public HotkeyGesture Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value;
            _hasGesture = true;
            Invalidate();
        }
    }

    public bool HasGesture
    {
        get => _hasGesture;
        set
        {
            if (_hasGesture == value) return;
            _hasGesture = value;
            Invalidate();
        }
    }

    public bool IsRecording => _isRecording;

    public HotkeyRecorder()
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
        Size = new Size(142, 34);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "修改快捷键";
    }

    protected override bool IsInputKey(Keys keyData) =>
        _isRecording || base.IsInputKey(keyData);

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_isRecording)
        {
            CancelExternalRecording();
            return;
        }

        StartRecording();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_isRecording)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                StartRecording();
                e.SuppressKeyPress = true;
            }
            return;
        }

        e.SuppressKeyPress = true;
        e.Handled = true;

        if (e.KeyCode == Keys.Escape)
        {
            HotkeyChangeResult endResult = CancelRecording();
            Feedback?.Invoke(endResult.Success
                ? new HotkeyChangeResult(false, "已取消修改")
                : endResult);
            return;
        }

        if (HotkeyGesture.IsModifierKey(e.KeyCode))
        {
            Invalidate();
            return;
        }

        HotkeyGesture proposed = HotkeyGesture.FromKeyEvent(e);
        if (!proposed.IsRecordable)
        {
            Feedback?.Invoke(new HotkeyChangeResult(
                false,
                "无法识别这个按键，请按一个非修饰键；Esc 取消",
                HotkeyChangeFailure.Invalid));
            return;
        }

        CommitGesture(proposed);
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
        HotkeyChangeResult endResult = CancelRecording();
        if (!endResult.Success)
        {
            Feedback?.Invoke(endResult);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        ThemePalette palette = ThemeManager.Palette;

        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(rect, GraphicsHelper.Dip(this, 12));
        Color fillColor = _isRecording
            ? palette.PrimaryContainer
            : palette.InputBg;
        using (var fillBrush = new SolidBrush(fillColor))
        {
            graphics.FillPath(fillBrush, path);
        }
        if (_isHovered && !_isRecording)
        {
            using var state = new SolidBrush(Color.FromArgb(10, palette.TextPrimary));
            graphics.FillPath(state, path);
        }

        Color borderColor = _isRecording || Focused ? palette.AccentColor : palette.CardBorder;
        using (var borderPen = new Pen(borderColor, _isRecording || Focused ? 2f : 1f))
        {
            graphics.DrawPath(borderPen, path);
        }

        Color contentColor = _isRecording ? palette.OnPrimaryContainer : palette.TextPrimary;
        LucideRenderer.Draw(graphics, LucideIcon.Keyboard, GraphicsHelper.Dip(this, 11), (Height - GraphicsHelper.Dip(this, 16)) / 2f, GraphicsHelper.Dip(this, 16), contentColor, 1.8f);
        string displayText = _isRecording
            ? "请按新的快捷键…"
            : _hasGesture ? _gesture.DisplayText : "未设置";
        TextRenderer.DrawText(
            graphics,
            displayText,
            Font,
            new Rectangle(GraphicsHelper.Dip(this, 36), 0, Math.Max(1, Width - GraphicsHelper.Dip(this, 44)), Height),
            contentColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    public void StartRecording()
    {
        if (_isRecording)
        {
            return;
        }

        HotkeyChangeResult beginResult = BeginRecordingRequest?.Invoke()
            ?? new HotkeyChangeResult(true, string.Empty);
        if (!beginResult.Success)
        {
            Feedback?.Invoke(beginResult);
            return;
        }

        Focus();
        _isRecording = true;
        RecordingStateChanged?.Invoke();
        Feedback?.Invoke(new HotkeyChangeResult(
            true,
            "现在按下想用的快捷键，Esc 取消"));
        Invalidate();
    }

    public void CommitRecordedGesture(HotkeyGesture gesture)
    {
        if (_isRecording)
        {
            CommitGesture(gesture);
        }
    }

    public void CancelExternalRecording()
    {
        if (!_isRecording)
        {
            return;
        }

        HotkeyChangeResult endResult = FinishRecording();
        Feedback?.Invoke(endResult.Success
            ? new HotkeyChangeResult(false, "已取消修改")
            : endResult);
    }

    private HotkeyChangeResult CancelRecording()
    {
        if (!_isRecording)
        {
            return new HotkeyChangeResult(true, string.Empty);
        }

        HotkeyChangeResult result = FinishRecording();
        Invalidate();
        return result;
    }

    private void CommitGesture(HotkeyGesture proposed)
    {
        HotkeyChangeResult result = TryCommit?.Invoke(proposed)
            ?? new HotkeyChangeResult(false, "快捷键服务尚未就绪");
        if (result.Success)
        {
            _gesture = proposed;
            _hasGesture = true;
        }

        HotkeyChangeResult endResult = FinishRecording();
        if (result.Success && !endResult.Success)
        {
            result = endResult;
        }

        Feedback?.Invoke(result);
        Invalidate();
    }

    private HotkeyChangeResult FinishRecording()
    {
        _isRecording = false;
        RecordingStateChanged?.Invoke();
        HotkeyChangeResult result = EndRecordingRequest?.Invoke()
            ?? new HotkeyChangeResult(true, string.Empty);
        Invalidate();
        return result;
    }
}
