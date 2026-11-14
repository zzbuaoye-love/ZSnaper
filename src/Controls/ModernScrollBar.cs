using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class ModernScrollBar : Control
{
    private int TrackInset => GraphicsHelper.Dip(this, 3);
    private int MinimumThumbLength => GraphicsHelper.Dip(this, 34);
    private const int AutoHideDelayMs = 650;

    private readonly UiAnimationTimer _fadeTimer;
    private int _maximum;
    private int _largeChange = 1;
    private int _value;
    private bool _isHovered;
    private bool _isDragging;
    private int _dragStartY;
    private int _dragStartValue;
    private float _opacity;
    private long _showUntil;

    public event EventHandler? ValueChanged;

    public int Maximum
    {
        get => _maximum;
        set
        {
            int normalized = Math.Max(0, value);
            if (_maximum == normalized) return;
            _maximum = normalized;
            SetValue(_value, false, false);
            Invalidate();
        }
    }

    public int LargeChange
    {
        get => _largeChange;
        set
        {
            _largeChange = Math.Max(1, value);
            Invalidate();
        }
    }

    public int SmallChange { get; set; } = 52;

    public int Value
    {
        get => _value;
        set => SetValue(value, true, true);
    }

    public bool IsDragging => _isDragging;

    public ModernScrollBar()
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
        Cursor = Cursors.Hand;
        Width = 12;
        TabStop = false;

        _fadeTimer = new UiAnimationTimer(this, HandleFadeFrame);
        ThemeManager.ThemeChanged += HandleThemeChanged;
    }

    public void ScrollBy(int delta) => Value = _value + delta;

    public void NotifyActivity()
    {
        _showUntil = Environment.TickCount64 + AutoHideDelayMs;
        if (!_fadeTimer.Enabled)
        {
            _fadeTimer.Start();
        }
    }

    internal void SetValueFromOwner(int value) => SetValue(value, false, true);

    private void SetValue(int value, bool raiseChanged, bool show)
    {
        int normalized = Math.Clamp(value, 0, _maximum);
        if (show)
        {
            NotifyActivity();
        }

        if (_value == normalized) return;

        _value = normalized;
        Invalidate();
        if (raiseChanged)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        NotifyActivity();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isDragging)
        {
            _isHovered = false;
            NotifyActivity();
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _maximum <= 0) return;

        NotifyActivity();
        Rectangle thumb = GetThumbRectangle();
        if (thumb.Contains(e.Location))
        {
            _isDragging = true;
            _dragStartY = e.Y;
            _dragStartValue = _value;
            Capture = true;
        }
        else
        {
            Value += e.Y < thumb.Top ? -_largeChange : _largeChange;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging || _maximum <= 0) return;

        NotifyActivity();
        Rectangle thumb = GetThumbRectangle();
        int travel = Math.Max(1, Height - TrackInset * 2 - thumb.Height);
        int deltaValue = (int)Math.Round((e.Y - _dragStartY) * (_maximum / (double)travel));
        Value = _dragStartValue + deltaValue;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        _isDragging = false;
        Capture = false;
        _isHovered = ClientRectangle.Contains(PointToClient(MousePosition));
        NotifyActivity();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-(e.Delta / SystemInformation.MouseWheelScrollDelta) * SmallChange);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_maximum <= 0 || Width <= 0 || Height <= 0 || _opacity <= 0.01f) return;

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var palette = ThemeManager.Palette;

        int trackWidth = GraphicsHelper.Dip(this, _isHovered || _isDragging ? 6 : 4);
        int x = (Width - trackWidth) / 2;
        if (_isHovered || _isDragging)
        {
            var track = new Rectangle(x, TrackInset, trackWidth, Height - TrackInset * 2);
            using GraphicsPath trackPath = GraphicsHelper.GetRoundedRectangle(track, trackWidth / 2);
            Color trackColor = palette.Mode == Models.ThemeMode.Dark
                ? WithOpacity(Color.FromArgb(255, 255, 255), 28)
                : WithOpacity(Color.FromArgb(15, 23, 42), 24);
            using var trackBrush = new SolidBrush(trackColor);
            graphics.FillPath(trackBrush, trackPath);
        }

        Rectangle thumb = GetThumbRectangle();
        thumb.X = x;
        thumb.Width = trackWidth;
        using GraphicsPath thumbPath = GraphicsHelper.GetRoundedRectangle(thumb, trackWidth / 2);
        Color thumbColor = _isDragging
            ? WithOpacity(palette.AccentColor, 255)
            : palette.Mode == Models.ThemeMode.Dark
                ? WithOpacity(Color.FromArgb(214, 222, 232), _isHovered ? 170 : 125)
                : WithOpacity(Color.FromArgb(71, 85, 105), _isHovered ? 150 : 110);
        using var thumbBrush = new SolidBrush(thumbColor);
        graphics.FillPath(thumbBrush, thumbPath);
    }

    private Rectangle GetThumbRectangle()
    {
        int available = Math.Max(1, Height - TrackInset * 2);
        int totalExtent = Math.Max(1, _maximum + _largeChange);
        int minimumThumb = Math.Min(MinimumThumbLength, available);
        int thumbLength = _maximum <= 0
            ? available
            : Math.Clamp((int)Math.Round(available * (_largeChange / (double)totalExtent)), minimumThumb, available);
        int travel = Math.Max(0, available - thumbLength);
        int thumbTop = TrackInset + (_maximum <= 0 ? 0 : (int)Math.Round(travel * (_value / (double)_maximum)));
        return new Rectangle(0, thumbTop, Width, thumbLength);
    }

    private void HandleFadeFrame(double elapsedSeconds)
    {
        bool shouldShow = _isHovered || _isDragging || Environment.TickCount64 < _showUntil;
        float target = shouldShow ? 1f : 0f;
        double response = shouldShow ? 24d : 14d;
        float blend = (float)(1d - Math.Exp(-response * elapsedSeconds));
        _opacity += (target - _opacity) * blend;

        if (Math.Abs(target - _opacity) < 0.015f)
        {
            _opacity = target;
        }

        Invalidate();
        if (_opacity <= 0f && !shouldShow)
        {
            _fadeTimer.Stop();
        }
    }

    private Color WithOpacity(Color color, int alpha)
    {
        return Color.FromArgb(
            Math.Clamp((int)Math.Round(alpha * _opacity), 0, 255),
            color.R,
            color.G,
            color.B);
    }

    private void HandleThemeChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Dispose();
            ThemeManager.ThemeChanged -= HandleThemeChanged;
        }

        base.Dispose(disposing);
    }
}
