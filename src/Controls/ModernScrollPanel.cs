using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class ModernScrollPanel : Panel
{
    private int ScrollBarReserve => Helpers.GraphicsHelper.Dip(this, 16);
    private const double SpringStrength = 290d;
    private const double SpringDamping = 34d;

    private readonly BufferedPanel _content;
    private readonly ScrollSnapshotLayer _snapshotLayer;
    private readonly ModernScrollBar _scrollBar;
    private readonly UiAnimationTimer _scrollAnimation;
    private readonly System.Windows.Forms.Timer _snapshotSettleTimer = new() { Interval = 40 };
    private readonly HashSet<Control> _wheelHookedControls = [];
    private Bitmap? _scrollSnapshot;
    private int _contentHeight;
    private int _lastAppliedOffset = -1;
    private double _currentOffset;
    private double _targetOffset;
    private double _velocity;
    private bool _syncingScrollBar;
    private bool _snapshotDirty = true;
    private bool _suppressSnapshotInvalidation;

    public Panel Content => _content;

    public int ContentHeight
    {
        get => _contentHeight;
        set
        {
            _contentHeight = Math.Max(0, value);
            LayoutViewport();
        }
    }

    public void FitContentHeight(int bottomPadding = 0)
    {
        int contentBottom = 0;
        foreach (Control control in _content.Controls)
        {
            if (!control.Visible) continue;
            contentBottom = Math.Max(contentBottom, control.Bottom);
        }

        ContentHeight = contentBottom + Math.Max(0, bottomPadding);
    }

    public ModernScrollPanel()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);
        TabStop = true;

        _content = new BufferedPanel { Location = Point.Empty };
        _content.ControlAdded += HandleContentControlAdded;
        _content.ControlRemoved += HandleContentControlRemoved;

        _snapshotLayer = new ScrollSnapshotLayer
        {
            Location = Point.Empty,
            Visible = false
        };

        _scrollBar = new ModernScrollBar
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,
            SmallChange = 58
        };
        _scrollBar.ValueChanged += HandleScrollBarValueChanged;

        _scrollAnimation = new UiAnimationTimer(this, HandleScrollAnimationFrame);
        _snapshotSettleTimer.Tick += (_, _) =>
        {
            _snapshotSettleTimer.Stop();
            _suppressSnapshotInvalidation = false;
            _snapshotDirty = false;
        };

        Controls.Add(_content);
        Controls.Add(_snapshotLayer);
        Controls.Add(_scrollBar);
        HookMouseWheel(_content);
        HookMouseWheel(_snapshotLayer);
        ThemeManager.ThemeChanged += ApplyTheme;
        ApplyTheme();
    }

    public void ScrollToTop() => ScrollTo(0, true);

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        LayoutViewport();
    }


    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        FitContentHeight(Helpers.GraphicsHelper.Dip(this, 18));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleMouseWheel(this, e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int pageStep = Math.Max(48, ClientSize.Height - 48);
        switch (e.KeyCode)
        {
            case Keys.Up:
                ScrollTo(_targetOffset - 40);
                break;
            case Keys.Down:
                ScrollTo(_targetOffset + 40);
                break;
            case Keys.PageUp:
                ScrollTo(_targetOffset - pageStep);
                break;
            case Keys.PageDown:
                ScrollTo(_targetOffset + pageStep);
                break;
            case Keys.Home:
                ScrollTo(0);
                break;
            case Keys.End:
                ScrollTo(_scrollBar.Maximum);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void LayoutViewport()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        FinishSnapshotScroll();
        int maxScroll = Math.Max(0, _contentHeight - ClientSize.Height);
        _scrollBar.Bounds = new Rectangle(ClientSize.Width - ScrollBarReserve, 0, ScrollBarReserve, ClientSize.Height);
        _scrollBar.LargeChange = ClientSize.Height;
        _scrollBar.Maximum = maxScroll;
        _scrollBar.Visible = maxScroll > 0;

        int contentWidth = ClientSize.Width - (maxScroll > 0 ? ScrollBarReserve : 0);
        _content.Size = new Size(Math.Max(1, contentWidth), Math.Max(ClientSize.Height, _contentHeight));
        _snapshotLayer.Bounds = new Rectangle(0, 0, Math.Max(1, contentWidth), ClientSize.Height);
        InvalidateScrollSnapshot();

        _targetOffset = Math.Clamp(_targetOffset, 0, maxScroll);
        _currentOffset = Math.Clamp(_currentOffset, 0, maxScroll);
        _velocity = 0;
        _lastAppliedOffset = -1;
        ApplyScrollPosition();
        _scrollBar.BringToFront();
    }

    private void HandleScrollBarValueChanged(object? sender, EventArgs e)
    {
        if (_syncingScrollBar) return;
        ScrollTo(_scrollBar.Value, _scrollBar.IsDragging);
    }

    private void ScrollTo(double offset, bool immediate = false)
    {
        _targetOffset = Math.Clamp(offset, 0, _scrollBar.Maximum);
        _scrollBar.NotifyActivity();

        if (immediate)
        {
            _scrollAnimation.Stop();
            _currentOffset = _targetOffset;
            _velocity = 0;
            FinishSnapshotScroll();
            ApplyScrollPosition();
            return;
        }

        if (Math.Abs(_targetOffset - _currentOffset) < 0.1 && Math.Abs(_velocity) < 1)
        {
            _currentOffset = _targetOffset;
            _velocity = 0;
            FinishSnapshotScroll();
            ApplyScrollPosition();
            return;
        }

        if (!_scrollAnimation.Enabled)
        {
            BeginSnapshotScroll();
            _scrollAnimation.Start();
        }
    }

    private void HandleScrollAnimationFrame(double elapsedSeconds)
    {
        double displacement = _targetOffset - _currentOffset;
        double acceleration = displacement * SpringStrength - _velocity * SpringDamping;
        _velocity += acceleration * elapsedSeconds;
        _currentOffset += _velocity * elapsedSeconds;

        if ((_currentOffset < 0 && _velocity < 0) ||
            (_currentOffset > _scrollBar.Maximum && _velocity > 0))
        {
            _currentOffset = Math.Clamp(_currentOffset, 0, _scrollBar.Maximum);
            _velocity = 0;
        }

        if (Math.Abs(_targetOffset - _currentOffset) < 0.08 && Math.Abs(_velocity) < 1.5)
        {
            _currentOffset = _targetOffset;
            _velocity = 0;
            _scrollAnimation.Stop();
            FinishSnapshotScroll();
        }

        ApplyScrollPosition();
    }

    private void ApplyScrollPosition()
    {
        int offset = Math.Clamp((int)Math.Round(_currentOffset), 0, _scrollBar.Maximum);
        if (_lastAppliedOffset == offset)
        {
            return;
        }

        _lastAppliedOffset = offset;
        if (_snapshotLayer.Visible && _scrollSnapshot != null)
        {
            _snapshotLayer.Offset = offset;
        }
        else
        {
            _content.Location = new Point(0, -offset);
        }

        _syncingScrollBar = true;
        _scrollBar.SetValueFromOwner(offset);
        _syncingScrollBar = false;
    }

    private void BeginSnapshotScroll()
    {
        if (_snapshotLayer.Visible ||
            _content.Width <= 0 ||
            _content.Height <= 0)
        {
            return;
        }

        try
        {
            if (_snapshotDirty ||
                _scrollSnapshot is null ||
                _scrollSnapshot.Size != _content.Size)
            {
                _scrollSnapshot?.Dispose();
                _scrollSnapshot = new Bitmap(_content.Width, _content.Height, PixelFormat.Format32bppPArgb);
                using (Graphics graphics = Graphics.FromImage(_scrollSnapshot))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.Clear(Color.Transparent);
                }

                _content.DrawToBitmap(_scrollSnapshot, new Rectangle(Point.Empty, _content.Size));
                _snapshotDirty = false;
            }

            _snapshotLayer.Snapshot = _scrollSnapshot;
            _snapshotLayer.Offset = Math.Clamp((int)Math.Round(_currentOffset), 0, _scrollBar.Maximum);
            _snapshotLayer.Visible = true;
            _snapshotLayer.BringToFront();
            _scrollBar.BringToFront();
        }
        catch
        {
            _snapshotLayer.Visible = false;
            _snapshotLayer.Snapshot = null;
            _scrollSnapshot?.Dispose();
            _scrollSnapshot = null;
        }
    }

    private void FinishSnapshotScroll()
    {
        if (!_snapshotLayer.Visible && _scrollSnapshot == null)
        {
            return;
        }

        int offset = Math.Clamp((int)Math.Round(_currentOffset), 0, _scrollBar.Maximum);
        _suppressSnapshotInvalidation = true;
        try
        {
            _content.Location = new Point(0, -offset);
            _snapshotLayer.Visible = false;
            _snapshotLayer.Snapshot = null;
        }
        finally
        {
            _snapshotSettleTimer.Stop();
            _snapshotSettleTimer.Start();
        }
        _lastAppliedOffset = offset;
    }

    private void HandleContentControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control != null)
        {
            HookMouseWheel(e.Control);
            InvalidateScrollSnapshot();
        }
    }

    private void HandleContentControlRemoved(object? sender, ControlEventArgs e) =>
        InvalidateScrollSnapshot();

    private void HookMouseWheel(Control control)
    {
        if (!_wheelHookedControls.Add(control)) return;

        control.MouseWheel += HandleMouseWheel;
        control.ControlAdded += HandleNestedControlAdded;
        control.ControlRemoved += HandleNestedControlRemoved;
        if (!ReferenceEquals(control, _content))
        {
            control.Invalidated += HandleContentInvalidated;
        }
        foreach (Control child in control.Controls)
        {
            HookMouseWheel(child);
        }
    }

    private void HandleNestedControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control != null)
        {
            HookMouseWheel(e.Control);
            InvalidateScrollSnapshot();
        }
    }

    private void HandleNestedControlRemoved(object? sender, ControlEventArgs e) =>
        InvalidateScrollSnapshot();

    private void HandleContentInvalidated(object? sender, InvalidateEventArgs e)
    {
        if (!_suppressSnapshotInvalidation)
        {
            InvalidateScrollSnapshot();
        }
    }

    private void InvalidateScrollSnapshot()
    {
        _snapshotSettleTimer.Stop();
        _suppressSnapshotInvalidation = false;
        _snapshotDirty = true;
    }

    private void HandleMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_scrollBar.Maximum <= 0) return;

        int notches = e.Delta / SystemInformation.MouseWheelScrollDelta;
        if (notches == 0) notches = Math.Sign(e.Delta);
        ScrollTo(_targetOffset - notches * _scrollBar.SmallChange);
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    private void ApplyTheme()
    {
        FinishSnapshotScroll();
        BackColor = Color.Transparent;
        _content.BackColor = Color.Transparent;
        _snapshotLayer.BackColor = Color.Transparent;
        InvalidateScrollSnapshot();
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scrollAnimation.Dispose();
            _snapshotSettleTimer.Dispose();
            _scrollSnapshot?.Dispose();
            _scrollBar.ValueChanged -= HandleScrollBarValueChanged;
            _content.ControlAdded -= HandleContentControlAdded;
            _content.ControlRemoved -= HandleContentControlRemoved;
            ThemeManager.ThemeChanged -= ApplyTheme;

            foreach (Control control in _wheelHookedControls)
            {
                control.MouseWheel -= HandleMouseWheel;
                control.ControlAdded -= HandleNestedControlAdded;
                control.ControlRemoved -= HandleNestedControlRemoved;
                control.Invalidated -= HandleContentInvalidated;
            }

            _wheelHookedControls.Clear();
        }

        base.Dispose(disposing);
    }

    private sealed class ScrollSnapshotLayer : Control
    {
        private Bitmap? _snapshot;
        private int _offset;

        public Bitmap? Snapshot
        {
            get => _snapshot;
            set
            {
                _snapshot = value;
                Invalidate();
            }
        }

        public int Offset
        {
            get => _offset;
            set
            {
                if (_offset == value) return;
                _offset = value;
                Invalidate();
            }
        }

        public ScrollSnapshotLayer()
        {
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_snapshot == null || _offset >= _snapshot.Height)
            {
                return;
            }

            int width = Math.Min(ClientSize.Width, _snapshot.Width);
            int height = Math.Min(ClientSize.Height, _snapshot.Height - _offset);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // The snapshot may contain transparent pixels. SourceOver keeps the
            // parent-rendered background visible instead of copying zero-alpha
            // pixels into the WinForms back buffer as black.
            e.Graphics.CompositingMode = CompositingMode.SourceOver;
            e.Graphics.DrawImage(
                _snapshot,
                new Rectangle(0, 0, width, height),
                new Rectangle(0, _offset, width, height),
                GraphicsUnit.Pixel);
        }
    }
}
