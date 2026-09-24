using System.Drawing.Drawing2D;
using System.Globalization;
using SharpHook;
using SharpHook.Data;
using SkiaSharp;
using ZSnaper.Helpers;
using ZSnaper.Interop;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Forms;

public class OverlayForm : Form
{
    private const int MinimumSelectionSize = 2;
    private const int HandleSize = 10;
    private const int EdgeHitSize = 9;
    private const int ToolbarHeight = 44;
    private const int ToolbarButtonSize = 32;
    private const int ToolbarGap = 3;
    private const float StrokePointSpacing = 2.25f;
    private const float MaxCustomPenWidth = 512f;
    private const float MaxCustomMosaicBrush = 512f;
    private const int MaxCustomMosaicPixel = 256;
    private const int SmartSelectionDragThreshold = 4;

    private Bitmap? _screen;
    private readonly Dictionary<int, Bitmap> _pixelatedScreens = [];
    private CapturedCursor? _capturedCursor;
    private Point _start;
    private Point _dragOrigin;
    private Rectangle _selection;
    private Rectangle _selectionAtDragStart;
    private DragMode _dragMode;
    private bool _hasSelection;
    private SmartSelectionTarget? _smartTarget;
    private SmartSelectionTarget? _smartFastTarget;
    private Rectangle _pendingSmartBounds;
    private bool _pendingSmartClick;
    private Point _pendingSmartPoint;
    private int _smartSelectionVersion;
    private bool _smartSelectionResolving;
    private bool _includeCursor;
    private ToolbarAction _hoveredAction;
    private ToolbarAction _pressedAction;
    private ToolbarAction _activeTool;
    private readonly List<AnnotationItem> _annotations = [];
    private AnnotationItem? _workingAnnotation;
    private RichTextBox? _inlineTextEditor;
    private PointF _inlineTextLocation;
    private RectangleF _inlineTextBounds;
    private ComboBox? _inlineFontFamily;
    private ComboBox? _inlineFontSize;
    private bool _showStyleBar;
    private bool _updatingStyleControls;
    private bool _inlineStyleControlsVisible;
    private ScrollCaptureForm? _scrollCaptureForm;
    private int _captureSessionVersion;
    private readonly System.Windows.Forms.Timer _inlineCaretTimer;
    private bool _inlineCaretVisible;
    private StyleSliderKind _activeStyleSlider;
    private TextBox? _styleValueEditor;
    private StyleSliderKind _editingStyleValue;
    private bool _committingStyleValue;
    private readonly SkiaRasterLayer _styleBarLayer = new();
    private int _styleBarRenderKey = int.MinValue;
    private bool _resourcesDisposed;
    private volatile bool _preserveForeground;
    private volatile bool _captureOverlayVisible;
    private HotkeyService? _hotkeyService;
    private readonly HashSet<KeyCode> _suppressedCaptureKeys = [];
    private static readonly Font SizeLabelFont = new("Segoe UI", 8.5f, FontStyle.Bold);

    public event Action<Bitmap, Point, CaptureCompletionAction>? Captured;
    public event Action<string>? CaptureFailed;
    public bool PreservesForeground => _preserveForeground;

    public void AttachHotkeyService(HotkeyService hotkeyService) => _hotkeyService = hotkeyService;

    protected override bool ShowWithoutActivation => _preserveForeground;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _inlineCaretTimer = new System.Windows.Forms.Timer { Interval = 530 };
        _inlineCaretTimer.Tick += (_, _) =>
        {
            if (_inlineTextEditor is null) return;
            _inlineCaretVisible = !_inlineCaretVisible;
            InvalidateInlineTextBounds();
        };

        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public void BeginCapture() => BeginCapture(preserveForeground: false);

    public void BeginCapture(bool preserveForeground)
    {
        StopCaptureKeyboardRouting();
        _preserveForeground = preserveForeground;
        _captureSessionVersion++;
        _scrollCaptureForm?.CancelFromOwner();
        _scrollCaptureForm = null;

        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Bounds = virtualScreen;

        CapturedCursor? nextCursor = null;
        Bitmap? nextScreen = null;
        try
        {
            nextCursor = CaptureService.CaptureCursor();
            nextScreen = CaptureService.CaptureScreen(virtualScreen);
        }
        catch
        {
            nextCursor?.Dispose();
            nextScreen?.Dispose();
            throw;
        }

        _screen?.Dispose();
        DisposePixelatedScreens();
        _capturedCursor?.Dispose();
        _capturedCursor = nextCursor;
        _screen = nextScreen;

        _selection = Rectangle.Empty;
        _hasSelection = false;
        _smartTarget = null;
        _smartFastTarget = null;
        _pendingSmartClick = false;
        _smartSelectionVersion++;
        _includeCursor = false;
        _dragMode = DragMode.None;
        _hoveredAction = ToolbarAction.None;
        _pressedAction = ToolbarAction.None;
        _activeTool = ToolbarAction.None;
        _activeStyleSlider = StyleSliderKind.None;
        CancelStyleValueEdit();
        _workingAnnotation = null;
        _annotations.Clear();
        CancelInlineText();
        _showStyleBar = false;
        Cursor = Cursors.Cross;

        ShowCaptureOverlay();
        UpdateSmartSelection(PointToClient(System.Windows.Forms.Cursor.Position));
        Invalidate();
    }

    protected override void WndProc(ref Message message)
    {
        if (_preserveForeground && message.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            message.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _captureOverlayVisible = Visible;
        if (!Visible) StopCaptureKeyboardRouting();
    }

    private void ShowCaptureOverlay()
    {
        if (_preserveForeground) StartCaptureKeyboardRouting();

        Show();
        if (!_preserveForeground) Activate();
    }

    private void StartCaptureKeyboardRouting()
    {
        StopCaptureKeyboardRouting();
        _hotkeyService?.BeginCaptureKeyboardRouting(OnCaptureKeyPressed, OnCaptureKeyReleased);
    }

    private void StopCaptureKeyboardRouting()
    {
        _hotkeyService?.EndCaptureKeyboardRouting();
        lock (_suppressedCaptureKeys) _suppressedCaptureKeys.Clear();
    }

    private void EnableOverlayTextInput()
    {
        if (!_preserveForeground) return;
        StopCaptureKeyboardRouting();
        _preserveForeground = false;
        Activate();
    }

    private void OnCaptureKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (!_preserveForeground || !_captureOverlayVisible || e.IsEventSimulated ||
            !TryGetCaptureShortcut(e.Data.KeyCode, out Keys shortcut))
        {
            return;
        }

        bool firstPress;
        lock (_suppressedCaptureKeys)
        {
            firstPress = _suppressedCaptureKeys.Add(e.Data.KeyCode);
        }
        e.SuppressEvent = true;
        if (!firstPress) return;

        try
        {
            BeginInvoke(() =>
            {
                if (!Visible || IsDisposed) return;
                if (shortcut == (Keys.Alt | Keys.F4)) CancelCapture();
                else OnKeyDown(new KeyEventArgs(shortcut));
            });
        }
        catch (InvalidOperationException) { }
    }

    private void OnCaptureKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        lock (_suppressedCaptureKeys)
        {
            if (_suppressedCaptureKeys.Remove(e.Data.KeyCode)) e.SuppressEvent = true;
        }
    }

    private static bool TryGetCaptureShortcut(KeyCode key, out Keys shortcut)
    {
        bool control = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool shift = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
        Keys modifiers = (control ? Keys.Control : Keys.None) |
                         (alt ? Keys.Alt : Keys.None) |
                         (shift ? Keys.Shift : Keys.None);

        shortcut = key switch
        {
            KeyCode.VcEscape => modifiers | Keys.Escape,
            KeyCode.VcEnter or KeyCode.VcNumPadEnter => modifiers | Keys.Enter,
            KeyCode.VcBackQuote => modifiers | Keys.Oemtilde,
            KeyCode.VcF4 when alt => modifiers | Keys.F4,
            KeyCode.VcC when control => modifiers | Keys.C,
            KeyCode.VcS when control => modifiers | Keys.S,
            KeyCode.VcP when control => modifiers | Keys.P,
            KeyCode.VcZ when control => modifiers | Keys.Z,
            KeyCode.VcR when modifiers == Keys.None => Keys.R,
            KeyCode.VcP when modifiers == Keys.None => Keys.P,
            KeyCode.VcA when modifiers == Keys.None => Keys.A,
            KeyCode.VcT when modifiers == Keys.None => Keys.T,
            KeyCode.VcM when modifiers == Keys.None => Keys.M,
            _ => Keys.None
        };
        return shortcut != Keys.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right && _preserveForeground)
        {
            CancelCapture();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_hasSelection)
        {
            Rectangle styleBarBounds = GetStyleBarBounds();
            if (_styleValueEditor is not null && _styleValueEditor.Visible)
            {
                if (_styleValueEditor.Bounds.Contains(e.Location))
                {
                    _styleValueEditor.Focus();
                    return;
                }
                CommitStyleValueEdit();
            }
            if (_inlineTextEditor is not null && _inlineTextBounds.Contains(e.Location))
            {
                _inlineTextEditor.Focus();
                return;
            }
            if (_inlineTextEditor is not null && !styleBarBounds.Contains(e.Location))
            {
                CommitInlineText();
            }

            if (TryBeginStyleValueEdit(e.Location) || TryBeginStyleSlider(e.Location))
            {
                return;
            }
            StyleButton? styleButton = HitTestStyleBar(e.Location);
            if (styleButton is StyleButton button)
            {
                ExecuteStyleAction(button);
                return;
            }
            if (styleBarBounds.Contains(e.Location)) return;

            ToolbarAction action = HitTestToolbar(e.Location);
            if (action != ToolbarAction.None)
            {
                _pressedAction = action;
                _hoveredAction = action;
                Capture = true;
                Cursor = Cursors.Hand;
                Invalidate();
                return;
            }

            if (GetToolbarBounds().Contains(e.Location))
            {
                return;
            }

            if (IsAnnotationTool(_activeTool) && _selection.Contains(e.Location))
            {
                if (_activeTool == ToolbarAction.Text)
                {
                    BeginInlineText(e.Location);
                }
                else
                {
                    BeginAnnotation(e.Location);
                }
                return;
            }

            DragMode hit = HitTestSelection(e.Location);
            if (hit != DragMode.None)
            {
                _dragMode = hit;
                _dragOrigin = e.Location;
                _selectionAtDragStart = _selection;
                if (!_selection.Contains(e.Location) && hit != DragMode.Move)
                {
                    _selection = ResizeSelection(ClampToClient(e.Location));
                }
                Capture = true;
                Invalidate();
                return;
            }

            Point expansionPoint = ClampToClient(e.Location);
            _selectionAtDragStart = _selection;
            _dragMode = GetExpansionDragMode(expansionPoint);
            _dragOrigin = expansionPoint;
            _selection = ResizeSelection(expansionPoint);
            Capture = true;
            Cursor = CursorForDragMode(_dragMode);
            Invalidate();
            return;
        }

        _start = ClampToClient(e.Location);
        if (_smartTarget is SmartSelectionTarget smartTarget)
        {
            Rectangle smartBounds = ScreenToClientBounds(smartTarget.ScreenBounds);
            if (smartBounds.Contains(e.Location))
            {
                _pendingSmartBounds = smartBounds;
                _pendingSmartClick = true;
                Capture = true;
                Cursor = Cursors.Hand;
                return;
            }
        }

        _dragMode = DragMode.NewSelection;
        _hasSelection = false;
        _smartTarget = null;
        _smartFastTarget = null;
        _selection = Rectangle.Empty;
        _hoveredAction = ToolbarAction.None;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_pendingSmartClick)
        {
            if (e.Button == MouseButtons.Left &&
                (Math.Abs(e.X - _start.X) >= SmartSelectionDragThreshold ||
                 Math.Abs(e.Y - _start.Y) >= SmartSelectionDragThreshold))
            {
                _pendingSmartClick = false;
                _smartTarget = null;
                _smartFastTarget = null;
                _selection = Rectangle.Empty;
                _hasSelection = false;
                _dragMode = DragMode.NewSelection;
                // The smart candidate restored its entire target to the undimmed screenshot.
                // Repaint the full overlay once so none of that old target remains around the manual selection.
                Invalidate();
            }
            else
            {
                Cursor = Cursors.Hand;
                return;
            }
        }

        if (_activeStyleSlider != StyleSliderKind.None)
        {
            UpdateStyleSlider(_activeStyleSlider, e.Location);
            Cursor = Cursors.SizeWE;
            return;
        }

        if (_workingAnnotation is not null)
        {
            UpdateWorkingAnnotation(ClampToSelection(e.Location));
            Cursor = Cursors.Cross;
            return;
        }

        if (_pressedAction != ToolbarAction.None)
        {
            _hoveredAction = HitTestToolbar(e.Location);
            Cursor = Cursors.Hand;
            Invalidate();
            return;
        }

        if (_dragMode != DragMode.None)
        {
            Point point = ClampToClient(e.Location);
            Rectangle nextSelection;
            if (_dragMode == DragMode.NewSelection)
            {
                nextSelection = FromPoints(_start, point);
            }
            else if (_dragMode == DragMode.Move)
            {
                nextSelection = MoveSelection(point);
            }
            else
            {
                nextSelection = ResizeSelection(point);
            }

            if (nextSelection == _selection)
            {
                return;
            }

            Rectangle oldSelection = _selection;
            _selection = nextSelection;
            _hasSelection = _selection.Width >= MinimumSelectionSize &&
                            _selection.Height >= MinimumSelectionSize;

            Cursor = CursorForDragMode(_dragMode);

            Rectangle dirty = ComputeSelectionDirtyRect(oldSelection, _selection);
            Invalidate(dirty);
            Update();
            return;
        }

        bool overStyleBar = _hasSelection && GetStyleBarBounds().Contains(e.Location);
        ToolbarAction hoveredAction = _hasSelection && !overStyleBar
            ? HitTestToolbar(e.Location)
            : ToolbarAction.None;
        DragMode hoveredEdge = _hasSelection && hoveredAction == ToolbarAction.None
            ? HitTestSelection(e.Location)
            : DragMode.None;

        if (_hoveredAction != hoveredAction)
        {
            _hoveredAction = hoveredAction;
            Invalidate();
        }

        bool overStyleSlider = HitTestStyleSlider(e.Location) != StyleSliderKind.None;
        Cursor = overStyleSlider
            ? Cursors.SizeWE
            : hoveredAction != ToolbarAction.None || overStyleBar
                ? Cursors.Hand
            : IsAnnotationTool(_activeTool) && _selection.Contains(e.Location)
                ? Cursors.Cross
                : CursorForDragMode(hoveredEdge);

        if (!_hasSelection)
        {
            UpdateSmartSelection(e.Location);
            Cursor = _smartTarget is SmartSelectionTarget target &&
                     ScreenToClientBounds(target.ScreenBounds).Contains(e.Location)
                ? Cursors.Hand
                : Cursors.Cross;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        if (_activeStyleSlider != StyleSliderKind.None)
        {
            UpdateStyleSlider(_activeStyleSlider, e.Location);
            _activeStyleSlider = StyleSliderKind.None;
            Capture = false;
            ConfigService.Save();
            Invalidate(GetStyleBarBounds());
            return;
        }

        if (_workingAnnotation is not null)
        {
            UpdateWorkingAnnotation(ClampToSelection(e.Location));
            FinishWorkingAnnotation();
            Capture = false;
            Cursor = Cursors.Cross;
            return;
        }

        if (_pressedAction != ToolbarAction.None)
        {
            ToolbarAction pressed = _pressedAction;
            ToolbarAction releasedOver = HitTestToolbar(e.Location);
            _pressedAction = ToolbarAction.None;
            Capture = false;
            if (pressed == releasedOver)
            {
                ExecuteToolbarAction(pressed);
            }
            else
            {
                Invalidate();
            }
            return;
        }

        if (_pendingSmartClick)
        {
            _pendingSmartClick = false;
            Capture = false;
            _selection = Rectangle.Intersect(_pendingSmartBounds, ClientRectangle);
            _hasSelection = _selection.Width >= MinimumSelectionSize &&
                            _selection.Height >= MinimumSelectionSize;
            _smartTarget = null;
            _smartFastTarget = null;
            Cursor = _hasSelection ? CursorForDragMode(HitTestSelection(e.Location)) : Cursors.Cross;
            UpdateInlineStyleControls();
            Invalidate();
            return;
        }

        if (_dragMode == DragMode.None) return;
        DragMode completedDragMode = _dragMode;
        _dragMode = DragMode.None;
        Capture = false;
        _hasSelection = _selection.Width >= MinimumSelectionSize &&
                        _selection.Height >= MinimumSelectionSize;
        UpdateAutoToolbarHabit(ClampToClient(e.Location), completedDragMode);
        Cursor = _hasSelection ? CursorForDragMode(HitTestSelection(e.Location)) : Cursors.Cross;
        UpdateInlineStyleControls();
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left &&
            _hasSelection &&
            _activeTool == ToolbarAction.None &&
            _selection.Contains(e.Location))
        {
            CompleteCapture(CaptureCompletionAction.Default);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        if (_screen is null) return;

        graphics.DrawImageUnscaled(_screen, 0, 0);
        using var dim = new SolidBrush(Color.FromArgb(112, 0, 0, 0));

        if (!_hasSelection || _selection.Width <= 0 || _selection.Height <= 0)
        {
            graphics.FillRectangle(dim, ClientRectangle);
            DrawSmartSelectionCandidate(graphics);
            return;
        }

        DrawDimmedOutside(graphics, dim);
        if (_includeCursor && _capturedCursor is not null)
        {
            GraphicsState state = graphics.Save();
            graphics.SetClip(_selection);
            CaptureService.DrawCursor(graphics, _capturedCursor, Location);
            graphics.Restore(state);
        }

        GraphicsState annotationState = graphics.Save();
        graphics.SetClip(_selection);
        DrawAnnotations(graphics);
        DrawInlineTextPreview(graphics);
        graphics.Restore(annotationState);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        ThemePalette palette = ThemeManager.Palette;
        bool isDark = palette.Mode == ThemeMode.Dark;

        // Keep the selection edge crisp without darkening the pixels around it.
        using (var borderPen = new Pen(palette.AccentColor, 2f) { LineJoin = LineJoin.Round })
        {
            graphics.DrawRectangle(
                borderPen,
                _selection.Left,
                _selection.Top,
                _selection.Width,
                _selection.Height);
        }

        // Inner subtle light reflection for depth (dark mode only, inside selection by 1px)
        if (isDark && _selection.Width > 8 && _selection.Height > 8)
        {
            using var innerPen = new Pen(Color.FromArgb(24, 255, 255, 255), 1f) { LineJoin = LineJoin.Round };
            graphics.DrawRectangle(
                innerPen,
                _selection.Left + 1,
                _selection.Top + 1,
                _selection.Width - 2,
                _selection.Height - 2);
        }

        DrawResizeHandles(graphics);
        DrawSizeLabel(graphics);

        if (_dragMode == DragMode.None)
        {
            DrawToolbar(graphics);
            DrawStyleBar(graphics);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_styleValueEditor is { Visible: true }) return;

        if (_inlineTextEditor is not null)
        {
            if (e.KeyCode == Keys.Escape)
            {
                CancelInlineText();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.Enter)
            {
                CommitInlineText();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            return;
        }

        if (_hasSelection && e.Control && e.KeyCode == Keys.C)
        {
            CompleteCapture(CaptureCompletionAction.Copy);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_hasSelection && e.Control && e.KeyCode == Keys.S)
        {
            CompleteCapture(CaptureCompletionAction.Save);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_hasSelection && e.Control && e.KeyCode == Keys.P)
        {
            CompleteCapture(CaptureCompletionAction.Pin);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_hasSelection && e.Control && e.KeyCode == Keys.Z)
        {
            UndoAnnotation();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Escape:
                CancelCapture();
                e.Handled = true;
                break;
            case Keys.Enter when _hasSelection:
                CompleteCapture(CaptureCompletionAction.Default);
                e.Handled = true;
                break;
            case Keys.Oemtilde:
                ToggleCursorCapture();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.R when _hasSelection && e.Modifiers == Keys.None:
                ResetSelection();
                e.Handled = true;
                break;
            case Keys.P when _hasSelection && e.Modifiers == Keys.None:
                SelectAnnotationTool(ToolbarAction.Pen);
                e.Handled = true;
                break;
            case Keys.A when _hasSelection && e.Modifiers == Keys.None:
                SelectAnnotationTool(ToolbarAction.Arrow);
                e.Handled = true;
                break;
            case Keys.T when _hasSelection && e.Modifiers == Keys.None:
                SelectAnnotationTool(ToolbarAction.Text);
                e.Handled = true;
                break;
            case Keys.M when _hasSelection && e.Modifiers == Keys.None:
                SelectAnnotationTool(ToolbarAction.Mosaic);
                e.Handled = true;
                break;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys keyCode = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;

        if (keyCode == Keys.F4 && modifiers.HasFlag(Keys.Alt))
        {
            CancelCapture();
            return true;
        }

        if (_styleValueEditor is { Visible: true })
        {
            if (keyCode == Keys.Enter)
            {
                CommitStyleValueEdit();
                return true;
            }
            if (keyCode == Keys.Escape)
            {
                CancelStyleValueEdit();
                return true;
            }
        }

        if (_inlineTextEditor is not null)
        {
            if (keyCode == Keys.Escape)
            {
                CancelInlineText();
                return true;
            }
            if (keyCode == Keys.Enter && modifiers.HasFlag(Keys.Control))
            {
                CommitInlineText();
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeManagedResources();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeManagedResources();
        base.Dispose(disposing);
    }

    private void DisposeManagedResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;

        StopCaptureKeyboardRouting();
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _inlineCaretTimer.Stop();
        _inlineCaretTimer.Dispose();
        _screen?.Dispose();
        _screen = null;
        DisposePixelatedScreens();
        _capturedCursor?.Dispose();
        _capturedCursor = null;
        _captureSessionVersion++;
        _scrollCaptureForm?.CancelFromOwner();
        _scrollCaptureForm = null;
        _styleBarLayer.Dispose();
    }

    private void CompleteCapture(CaptureCompletionAction action)
    {
        if (!_hasSelection || _screen is null) return;

        CommitInlineText();

        var crop = new Bitmap(_selection.Width, _selection.Height);
        using (Graphics graphics = Graphics.FromImage(crop))
        {
            graphics.DrawImage(
                _screen,
                new Rectangle(0, 0, _selection.Width, _selection.Height),
                _selection,
                GraphicsUnit.Pixel);

            if (_includeCursor && _capturedCursor is not null)
            {
                var selectionScreenOrigin = new Point(
                    Location.X + _selection.X,
                    Location.Y + _selection.Y);
                CaptureService.DrawCursor(graphics, _capturedCursor, selectionScreenOrigin);
            }


            GraphicsState annotationState = graphics.Save();
            graphics.TranslateTransform(-_selection.X, -_selection.Y);
            DrawAnnotations(graphics);
            graphics.Restore(annotationState);
        }

        var screenPoint = new Point(Location.X + _selection.X, Location.Y + _selection.Y);
        Hide();
        PublishCaptured(crop, screenPoint, action);
    }

    private async void StartScrollCapture()
    {
        if (!_hasSelection || _screen is null || _scrollCaptureForm is not null) return;

        CommitInlineText();
        Rectangle selection = _selection;
        var screenBounds = new Rectangle(
            Location.X + selection.X,
            Location.Y + selection.Y,
            selection.Width,
            selection.Height);
        if (screenBounds.Width < 40 || screenBounds.Height < 40)
        {
            CaptureFailed?.Invoke("长截图选区太小，请重新选择页面或列表区域");
            return;
        }
        int sessionVersion = ++_captureSessionVersion;
        using Bitmap background = (Bitmap)_screen.Clone();
        Hide();

        try
        {
            await Task.Delay(100);
            if (sessionVersion != _captureSessionVersion || IsDisposed) return;

            using Bitmap initialFrame = CaptureService.CaptureScreen(screenBounds);
            var mode = new ScrollCaptureForm(screenBounds, background, initialFrame)
            {
                Icon = Icon
            };
            _scrollCaptureForm = mode;
            mode.Completed += (image, action) =>
            {
                if (!ReferenceEquals(_scrollCaptureForm, mode))
                {
                    image.Dispose();
                    return;
                }

                _scrollCaptureForm = null;
                ApplyScrollCaptureOverlays(image, selection, screenBounds);
                PublishCaptured(image, screenBounds.Location, action);
            };
            mode.Cancelled += () =>
            {
                if (ReferenceEquals(_scrollCaptureForm, mode))
                {
                    _scrollCaptureForm = null;
                }
            };
            mode.BeginMode();
        }
        catch (Exception ex)
        {
            if (sessionVersion != _captureSessionVersion || IsDisposed) return;
            _scrollCaptureForm = null;
            ShowCaptureOverlay();
            Invalidate();
            CaptureFailed?.Invoke("长截图失败：" + ex.Message);
        }
    }

    private void ApplyScrollCaptureOverlays(
        Bitmap image,
        Rectangle selection,
        Rectangle screenBounds)
    {
        using Graphics graphics = Graphics.FromImage(image);
        graphics.SetClip(new Rectangle(0, 0, selection.Width, Math.Min(selection.Height, image.Height)));
        if (_includeCursor && _capturedCursor is not null)
        {
            CaptureService.DrawCursor(graphics, _capturedCursor, screenBounds.Location);
        }

        GraphicsState annotationState = graphics.Save();
        graphics.TranslateTransform(-selection.X, -selection.Y);
        DrawAnnotations(graphics);
        graphics.Restore(annotationState);
    }

    private void PublishCaptured(Bitmap image, Point screenPoint, CaptureCompletionAction action)
    {
        Action<Bitmap, Point, CaptureCompletionAction>? handlers = Captured;
        if (handlers is null)
        {
            image.Dispose();
            return;
        }

        try
        {
            handlers(image, screenPoint, action);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private void CancelCapture()
    {
        CancelStyleValueEdit();
        CancelInlineText();
        _dragMode = DragMode.None;
        _pressedAction = ToolbarAction.None;
        _pendingSmartClick = false;
        _smartTarget = null;
        _smartFastTarget = null;
        _smartSelectionVersion++;
        _workingAnnotation = null;
        Capture = false;
        Hide();
    }

    private void ResetSelection()
    {
        CancelStyleValueEdit();
        CancelInlineText();
        _selection = Rectangle.Empty;
        _hasSelection = false;
        _smartTarget = null;
        _smartFastTarget = null;
        _pendingSmartClick = false;
        _smartSelectionVersion++;
        _dragMode = DragMode.None;
        _hoveredAction = ToolbarAction.None;
        _pressedAction = ToolbarAction.None;
        _activeTool = ToolbarAction.None;
        _workingAnnotation = null;
        _annotations.Clear();
        Cursor = Cursors.Cross;
        UpdateSmartSelection(PointToClient(System.Windows.Forms.Cursor.Position));
        Invalidate();
    }

    private void ToggleCursorCapture()
    {
        _includeCursor = !_includeCursor;
        Invalidate();
    }

    private void SelectAnnotationTool(ToolbarAction action)
    {
        CommitStyleValueEdit();
        CommitInlineText();
        _activeTool = _activeTool == action ? ToolbarAction.None : action;
        _workingAnnotation = null;
        _showStyleBar = _activeTool != ToolbarAction.None;
        Cursor = Cursors.Cross;
        UpdateInlineStyleControls();
        Invalidate();
    }

    private void BeginAnnotation(Point point)
    {
        Color color = GetAnnotationColor();
        AnnotationItem annotation = _activeTool switch
        {
            ToolbarAction.Pen => new StrokeAnnotation(
                [new PointF(point.X, point.Y)],
                color,
                Math.Clamp(ConfigService.Current.AnnotationPenWidth, 0.5f, MaxCustomPenWidth),
                false,
                0),
            ToolbarAction.Mosaic => new StrokeAnnotation(
                [new PointF(point.X, point.Y)],
                Color.Transparent,
                Math.Clamp(ConfigService.Current.AnnotationMosaicSize, 1f, MaxCustomMosaicBrush),
                true,
                Math.Clamp(ConfigService.Current.AnnotationMosaicPixelSize, 1, MaxCustomMosaicPixel)),
            ToolbarAction.Arrow => new ArrowAnnotation(
                new PointF(point.X, point.Y),
                new PointF(point.X, point.Y),
                color,
                Math.Clamp(ConfigService.Current.AnnotationPenWidth, 0.5f, MaxCustomPenWidth),
                ConfigService.Current.AnnotationArrowStyle),
            _ => throw new InvalidOperationException("The selected toolbar action is not drawable.")
        };

        if (annotation is StrokeAnnotation { IsMosaic: true } mosaic) EnsurePixelatedScreen(mosaic.PixelSize);
        _workingAnnotation = annotation;
        _annotations.Add(annotation);
        Capture = true;
        InvalidateAnnotation(annotation);
    }

    private void UpdateWorkingAnnotation(Point point)
    {
        if (_workingAnnotation is null) return;
        RectangleF previousBounds = GetAnnotationBounds(_workingAnnotation);

        switch (_workingAnnotation)
        {
            case StrokeAnnotation stroke:
                AppendInterpolatedPoints(stroke.Points, new PointF(point.X, point.Y));
                break;
            case ArrowAnnotation arrow:
                arrow.End = new PointF(point.X, point.Y);
                break;
        }

        RectangleF currentBounds = GetAnnotationBounds(_workingAnnotation);
        InvalidateAnnotationBounds(RectangleF.Union(previousBounds, currentBounds));
    }

    private void FinishWorkingAnnotation()
    {
        if (_workingAnnotation is ArrowAnnotation arrow &&
            Distance(arrow.Start, arrow.End) < 3f)
        {
            _annotations.Remove(_workingAnnotation);
        }

        _workingAnnotation = null;
        if (ConfigService.Current.AnnotationToolBehavior == AnnotationToolBehavior.SingleUse)
        {
            _activeTool = ToolbarAction.None;
        }
        Invalidate(GetToolbarBounds());
    }

    private void BeginInlineText(Point point)
    {
        CommitInlineText();
        EnableOverlayTextInput();
        using Font font = CreateAnnotationFont();
        Color color = GetAnnotationColor();

        _inlineTextLocation = new PointF(point.X, point.Y);
        _inlineTextBounds = new RectangleF(point.X, point.Y, 24f, font.GetHeight() + 4f);
        _inlineTextEditor = new RichTextBox
        {
            BorderStyle = BorderStyle.None,
            Multiline = true,
            AcceptsTab = false,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.None,
            Location = new Point(-2048, -2048),
            Size = new Size(2, 2),
            BackColor = Color.Black,
            ForeColor = color,
            Font = (Font)font.Clone(),
            WordWrap = true,
            ShortcutsEnabled = true
        };
        _inlineTextEditor.TextChanged += (_, _) =>
        {
            _inlineCaretVisible = true;
            _inlineCaretTimer.Stop();
            _inlineCaretTimer.Start();
            ResizeInlineTextEditor();
        };
        Controls.Add(_inlineTextEditor);
        ResizeInlineTextEditor();
        _inlineCaretVisible = true;
        _inlineCaretTimer.Start();
        _showStyleBar = true;
        UpdateInlineStyleControls();
        _inlineTextEditor.Focus();
        Invalidate();
    }

    private void ResizeInlineTextEditor()
    {
        if (_inlineTextEditor is null) return;
        RectangleF previousBounds = _inlineTextBounds;
        int maxWidth = Math.Max(24, _selection.Right - (int)_inlineTextLocation.X);
        int maxHeight = Math.Max(24, _selection.Bottom - (int)_inlineTextLocation.Y);
        string displayText = string.IsNullOrEmpty(_inlineTextEditor.Text)
            ? "输入文字"
            : _inlineTextEditor.Text;
        string[] lines = displayText.Replace("\r", string.Empty).Split('\n');
        int naturalWidth = lines.Max(line => TextRenderer.MeasureText(
            string.IsNullOrEmpty(line) ? " " : line,
            _inlineTextEditor.Font,
            Size.Empty,
            TextFormatFlags.NoPadding).Width);
        int minimumWidth = Math.Max(24, (int)Math.Ceiling(_inlineTextEditor.Font.GetHeight()));
        int preferredWidth = Math.Clamp(naturalWidth + 4, minimumWidth, maxWidth);
        Size wrapped = TextRenderer.MeasureText(
            displayText,
            _inlineTextEditor.Font,
            new Size(preferredWidth, maxHeight),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        int preferredHeight = Math.Clamp(
            wrapped.Height + 4,
            Math.Max(24, (int)Math.Ceiling(_inlineTextEditor.Font.GetHeight()) + 3),
            maxHeight);
        _inlineTextBounds = new RectangleF(
            _inlineTextLocation.X,
            _inlineTextLocation.Y,
            preferredWidth,
            preferredHeight);
        InvalidateInlineTextBounds(RectangleF.Union(previousBounds, _inlineTextBounds));
    }

    private void DrawInlineTextPreview(Graphics graphics)
    {
        if (_inlineTextEditor is null || _inlineTextBounds.IsEmpty) return;

        string text = _inlineTextEditor.Text;
        bool placeholder = string.IsNullOrEmpty(text);
        string displayText = placeholder ? "输入文字" : text;
        Color textColor = placeholder
            ? WithAlpha(ThemeManager.Palette.TextMuted, 180)
            : _inlineTextEditor.ForeColor;
        using var brush = new SolidBrush(textColor);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.MeasureTrailingSpaces
        };
        graphics.DrawString(displayText, _inlineTextEditor.Font, brush, _inlineTextBounds, format);

        if (!_inlineCaretVisible || placeholder) return;
        string[] lines = text.Replace("\r", string.Empty).Split('\n');
        string lastLine = lines[^1];
        float lineHeight = _inlineTextEditor.Font.GetHeight(graphics);
        float caretX = _inlineTextLocation.X + graphics.MeasureString(
            lastLine,
            _inlineTextEditor.Font,
            int.MaxValue,
            StringFormat.GenericTypographic).Width;
        caretX = Math.Min(caretX, _inlineTextBounds.Right - 1f);
        float caretY = Math.Min(
            _inlineTextLocation.Y + (lines.Length - 1) * lineHeight,
            _inlineTextBounds.Bottom - lineHeight);
        using var caretPen = new Pen(_inlineTextEditor.ForeColor, 1.2f);
        graphics.DrawLine(caretPen, caretX, caretY, caretX, Math.Min(caretY + lineHeight, _inlineTextBounds.Bottom));
    }

    private void InvalidateInlineTextBounds() => InvalidateInlineTextBounds(_inlineTextBounds);

    private void InvalidateInlineTextBounds(RectangleF bounds)
    {
        Rectangle dirty = Rectangle.Ceiling(bounds);
        dirty.Inflate(4, 4);
        dirty.Intersect(ClientRectangle);
        if (!dirty.IsEmpty) Invalidate(dirty);
    }

    private void CommitInlineText()
    {
        if (_inlineTextEditor is null) return;

        RichTextBox editor = _inlineTextEditor;
        _inlineTextEditor = null;
        RectangleF editorBounds = _inlineTextBounds;
        _inlineTextBounds = RectangleF.Empty;
        _inlineCaretTimer.Stop();
        _inlineCaretVisible = false;
        string text = editor.Text.TrimEnd();
        string family = editor.Font.Name;
        float size = editor.Font.SizeInPoints;
        FontStyle style = editor.Font.Style;
        Color color = editor.ForeColor;
        Font editorFont = editor.Font;
        Controls.Remove(editor);
        editor.Dispose();
        editorFont.Dispose();
        InvalidateInlineTextBounds(editorBounds);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var annotation = new TextAnnotation(
                _inlineTextLocation,
                text,
                family,
                size,
                style,
                color);
            _annotations.Add(annotation);
            InvalidateAnnotation(annotation);
        }

        if (ConfigService.Current.AnnotationToolBehavior == AnnotationToolBehavior.SingleUse)
        {
            _activeTool = ToolbarAction.None;
            _showStyleBar = false;
        }
        UpdateInlineStyleControls();
        Focus();
        Invalidate();
    }

    private void CancelInlineText()
    {
        if (_inlineTextEditor is null) return;
        RichTextBox editor = _inlineTextEditor;
        _inlineTextEditor = null;
        RectangleF editorBounds = _inlineTextBounds;
        _inlineTextBounds = RectangleF.Empty;
        _inlineCaretTimer.Stop();
        _inlineCaretVisible = false;
        Font editorFont = editor.Font;
        Controls.Remove(editor);
        editor.Dispose();
        editorFont.Dispose();
        InvalidateInlineTextBounds(editorBounds);
        UpdateInlineStyleControls();
        Invalidate();
    }

    private void UndoAnnotation()
    {
        if (_annotations.Count == 0) return;
        AnnotationItem annotation = _annotations[^1];
        RectangleF bounds = GetAnnotationBounds(annotation);
        _annotations.RemoveAt(_annotations.Count - 1);
        if (ReferenceEquals(annotation, _workingAnnotation)) _workingAnnotation = null;
        InvalidateAnnotationBounds(bounds);
        Invalidate(GetToolbarBounds());
    }

    private static void AppendInterpolatedPoints(List<PointF> points, PointF target)
    {
        PointF start = points[^1];
        float distance = Distance(start, target);
        if (distance < 0.35f) return;

        int steps = Math.Max(1, (int)Math.Ceiling(distance / StrokePointSpacing));
        for (int step = 1; step <= steps; step++)
        {
            float amount = step / (float)steps;
            points.Add(new PointF(
                start.X + (target.X - start.X) * amount,
                start.Y + (target.Y - start.Y) * amount));
        }
    }

    private Font CreateAnnotationFont()
    {
        string family = string.IsNullOrWhiteSpace(ConfigService.Current.AnnotationFontFamily)
            ? "Microsoft YaHei UI"
            : ConfigService.Current.AnnotationFontFamily;
        float size = Math.Clamp(ConfigService.Current.AnnotationFontSize, 8f, 72f);
        FontStyle style = IsValidFontStyle(ConfigService.Current.AnnotationFontStyle)
            ? (FontStyle)ConfigService.Current.AnnotationFontStyle
            : FontStyle.Regular;
        try { return new Font(family, size, style); }
        catch { return new Font("Microsoft YaHei UI", size, FontStyle.Regular); }
    }

    private static Color GetAnnotationColor()
    {
        try { return ColorTranslator.FromHtml(ConfigService.Current.AnnotationColorHex); }
        catch { return Color.FromArgb(255, 59, 48); }
    }

    private void ExecuteToolbarAction(ToolbarAction action)
    {
        switch (action)
        {
            case ToolbarAction.Pen:
            case ToolbarAction.Arrow:
            case ToolbarAction.Text:
            case ToolbarAction.Mosaic:
                SelectAnnotationTool(action);
                break;
            case ToolbarAction.Style:
                _showStyleBar = !_showStyleBar;
                UpdateInlineStyleControls();
                Invalidate();
                break;
            case ToolbarAction.Undo:
                UndoAnnotation();
                break;
            case ToolbarAction.Cursor:
                ToggleCursorCapture();
                break;
            case ToolbarAction.ScrollCapture:
                StartScrollCapture();
                break;
            case ToolbarAction.Ocr:
                CompleteCapture(CaptureCompletionAction.Ocr);
                break;
            case ToolbarAction.Copy:
                CompleteCapture(CaptureCompletionAction.Copy);
                break;
            case ToolbarAction.Save:
                CompleteCapture(CaptureCompletionAction.Save);
                break;
            case ToolbarAction.Pin:
                CompleteCapture(CaptureCompletionAction.Pin);
                break;
            case ToolbarAction.Reset:
                ResetSelection();
                break;
            case ToolbarAction.Confirm:
                CompleteCapture(CaptureCompletionAction.Default);
                break;
            case ToolbarAction.Cancel:
                CancelCapture();
                break;
        }
    }

    private Rectangle MoveSelection(Point point)
    {
        int x = _selectionAtDragStart.X + point.X - _dragOrigin.X;
        int y = _selectionAtDragStart.Y + point.Y - _dragOrigin.Y;
        x = Math.Clamp(x, 0, Math.Max(0, ClientSize.Width - _selectionAtDragStart.Width));
        y = Math.Clamp(y, 0, Math.Max(0, ClientSize.Height - _selectionAtDragStart.Height));
        return new Rectangle(x, y, _selectionAtDragStart.Width, _selectionAtDragStart.Height);
    }

    private Rectangle ResizeSelection(Point point)
    {
        int left = _selectionAtDragStart.Left;
        int top = _selectionAtDragStart.Top;
        int right = _selectionAtDragStart.Right;
        int bottom = _selectionAtDragStart.Bottom;

        if (ChangesLeft(_dragMode))
            left = Math.Clamp(point.X, 0, right - MinimumSelectionSize);
        if (ChangesRight(_dragMode))
            right = Math.Clamp(point.X, left + MinimumSelectionSize, ClientSize.Width);
        if (ChangesTop(_dragMode))
            top = Math.Clamp(point.Y, 0, bottom - MinimumSelectionSize);
        if (ChangesBottom(_dragMode))
            bottom = Math.Clamp(point.Y, top + MinimumSelectionSize, ClientSize.Height);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private DragMode HitTestSelection(Point point)
    {
        if (!_hasSelection) return DragMode.None;

        bool nearLeft = Math.Abs(point.X - _selection.Left) <= EdgeHitSize;
        bool nearRight = Math.Abs(point.X - _selection.Right) <= EdgeHitSize;
        bool nearTop = Math.Abs(point.Y - _selection.Top) <= EdgeHitSize;
        bool nearBottom = Math.Abs(point.Y - _selection.Bottom) <= EdgeHitSize;
        bool withinX = point.X >= _selection.Left - EdgeHitSize && point.X <= _selection.Right + EdgeHitSize;
        bool withinY = point.Y >= _selection.Top - EdgeHitSize && point.Y <= _selection.Bottom + EdgeHitSize;

        if (nearLeft && nearTop) return DragMode.TopLeft;
        if (nearRight && nearTop) return DragMode.TopRight;
        if (nearLeft && nearBottom) return DragMode.BottomLeft;
        if (nearRight && nearBottom) return DragMode.BottomRight;
        if (nearLeft && withinY) return DragMode.Left;
        if (nearRight && withinY) return DragMode.Right;
        if (nearTop && withinX) return DragMode.Top;
        if (nearBottom && withinX) return DragMode.Bottom;
        return _selection.Contains(point) ? DragMode.Move : DragMode.None;
    }

    private DragMode GetExpansionDragMode(Point point)
    {
        bool expandLeft = point.X < _selection.Left;
        bool expandRight = point.X > _selection.Right;
        bool expandTop = point.Y < _selection.Top;
        bool expandBottom = point.Y > _selection.Bottom;

        if (expandLeft && expandTop) return DragMode.TopLeft;
        if (expandRight && expandTop) return DragMode.TopRight;
        if (expandLeft && expandBottom) return DragMode.BottomLeft;
        if (expandRight && expandBottom) return DragMode.BottomRight;
        if (expandLeft) return DragMode.Left;
        if (expandRight) return DragMode.Right;
        if (expandTop) return DragMode.Top;
        if (expandBottom) return DragMode.Bottom;

        return DragMode.Move;
    }

    private Rectangle GetToolbarBounds()
    {
        if (!_hasSelection) return Rectangle.Empty;

        int toolbarWidth = Math.Min(GetToolbarWidth(), Math.Max(1, ClientSize.Width - 16));
        int toolbarHeight = ToolbarHeight * GetToolbarRowCount(toolbarWidth);
        ToolbarPlacementMode placement = ResolveToolbarPlacement();
        int preferredX = placement switch
        {
            ToolbarPlacementMode.Left => _selection.Left,
            ToolbarPlacementMode.Center => _selection.Left + (_selection.Width - toolbarWidth) / 2,
            _ => _selection.Right - toolbarWidth
        };
        int x = Math.Clamp(preferredX, 8, Math.Max(8, ClientSize.Width - toolbarWidth - 8));
        int below = _selection.Bottom + 10;
        int y = below + toolbarHeight <= ClientSize.Height - 8
            ? below
            : _selection.Top - toolbarHeight - 10;
        y = Math.Clamp(y, 8, Math.Max(8, ClientSize.Height - toolbarHeight - 8));
        return new Rectangle(x, y, toolbarWidth, toolbarHeight);
    }

    private static int GetToolbarWidth() =>
        54 + GetConfiguredToolbarItems().Sum(item =>
            (item == CaptureToolbarItem.Confirm ? 38 : ToolbarButtonSize) + ToolbarGap);

    private static int GetToolbarRowCount(int width)
    {
        int rows = 1;
        int x = 46;
        int rowStart = x;
        foreach (CaptureToolbarItem item in GetConfiguredToolbarItems())
        {
            int buttonWidth = item == CaptureToolbarItem.Confirm ? 38 : ToolbarButtonSize;
            if (x + buttonWidth > width - 8 && x > rowStart)
            {
                rows++;
                x = rowStart = 8;
            }
            x += buttonWidth + ToolbarGap;
        }
        return rows;
    }

    private static CaptureToolbarItem[] GetConfiguredToolbarItems()
    {
        var items = (ConfigService.Current.CaptureToolbarItems ?? [])
            .Where(item => Enum.IsDefined(item))
            .Distinct()
            .ToArray();
        return items.Length == 0 ? [CaptureToolbarItem.Confirm] : items;
    }

    private static ToolbarPlacementMode ResolveToolbarPlacement()
    {
        ToolbarPlacementMode configured = ConfigService.Current.ToolbarPlacement;
        if (configured != ToolbarPlacementMode.Auto) return configured;

        double bias = Math.Clamp(ConfigService.Current.ToolbarAutoHorizontalBias, 0d, 1d);
        if (bias < 0.38d) return ToolbarPlacementMode.Left;
        if (bias > 0.62d) return ToolbarPlacementMode.Right;
        return ToolbarPlacementMode.Center;
    }

    private void UpdateAutoToolbarHabit(Point releasePoint, DragMode completedDragMode)
    {
        if (!_hasSelection ||
            completedDragMode is DragMode.None or DragMode.Move ||
            ConfigService.Current.ToolbarPlacement != ToolbarPlacementMode.Auto)
        {
            return;
        }

        double sample = Math.Clamp(
            (releasePoint.X - _selection.Left) / (double)Math.Max(1, _selection.Width),
            0d,
            1d);
        int samples = Math.Max(0, ConfigService.Current.ToolbarAutoSampleCount);
        double learningRate = samples < 5 ? 0.32d : 0.18d;
        double currentBias = Math.Clamp(ConfigService.Current.ToolbarAutoHorizontalBias, 0d, 1d);
        ConfigService.Current.ToolbarAutoHorizontalBias =
            currentBias + (sample - currentBias) * learningRate;
        ConfigService.Current.ToolbarAutoSampleCount = Math.Min(samples + 1, 10_000);
        ConfigService.Save();
    }

    private ToolbarAction HitTestToolbar(Point point)
    {
        foreach (ToolbarButton button in GetToolbarButtons())
        {
            if (button.Bounds.Contains(point)) return button.Action;
        }
        return ToolbarAction.None;
    }

    private ToolbarButton[] GetToolbarButtons()
    {
        Rectangle toolbar = GetToolbarBounds();
        if (toolbar.IsEmpty) return [];

        int y = toolbar.Top + (ToolbarHeight - ToolbarButtonSize) / 2;
        int x = toolbar.Left + 46;
        int rowStart = x;
        var buttons = new List<ToolbarButton>();

        void Add(
            ToolbarAction action,
            LucideIcon icon,
            string tooltip,
            int width = ToolbarButtonSize,
            bool primary = false)
        {
            if (x + width > toolbar.Right - 8 && x > rowStart)
            {
                x = rowStart = toolbar.Left + 8;
                y += ToolbarHeight;
            }
            buttons.Add(new ToolbarButton(
                action,
                new Rectangle(x, y, width, ToolbarButtonSize),
                icon,
                tooltip,
                primary));
            x += width + ToolbarGap;
        }

        foreach (CaptureToolbarItem item in GetConfiguredToolbarItems())
        {
            switch (item)
            {
                case CaptureToolbarItem.Pen:
                    Add(ToolbarAction.Pen, LucideIcon.PenLine, "批注画笔  (P)");
                    break;
                case CaptureToolbarItem.Arrow:
                    Add(ToolbarAction.Arrow, LucideIcon.ArrowUpRight, "箭头  (A)");
                    break;
                case CaptureToolbarItem.Text:
                    Add(ToolbarAction.Text, LucideIcon.Type, "添加文字  (T)");
                    break;
                case CaptureToolbarItem.Mosaic:
                    Add(ToolbarAction.Mosaic, LucideIcon.Grid3X3, "马赛克  (M)");
                    break;
                case CaptureToolbarItem.Style:
                    Add(ToolbarAction.Style, LucideIcon.Palette, "颜色、字体与工具栏设置");
                    break;
                case CaptureToolbarItem.Undo:
                    Add(ToolbarAction.Undo, LucideIcon.Undo2, "撤销上一条批注  (Ctrl+Z)");
                    break;
                case CaptureToolbarItem.Cursor:
                    Add(
                        ToolbarAction.Cursor,
                        LucideIcon.MousePointer2,
                        _includeCursor ? "隐藏鼠标指针  (~)" : "显示鼠标指针  (~)");
                    break;
                case CaptureToolbarItem.ScrollCapture:
                    Add(
                        ToolbarAction.ScrollCapture,
                        LucideIcon.GalleryVerticalEnd,
                        "滚动并自动拼接长截图  (Esc 停止)");
                    break;
                case CaptureToolbarItem.Ocr:
                    Add(ToolbarAction.Ocr, LucideIcon.FileText, "识别选区文字");
                    break;
                case CaptureToolbarItem.Copy:
                    Add(ToolbarAction.Copy, LucideIcon.Copy, "复制到剪贴板  (Ctrl+C)");
                    break;
                case CaptureToolbarItem.Save:
                    Add(ToolbarAction.Save, LucideIcon.Folder, "保存到图片目录  (Ctrl+S)");
                    break;
                case CaptureToolbarItem.Pin:
                    Add(ToolbarAction.Pin, LucideIcon.Pin, "贴到桌面并保持置顶  (Ctrl+P)");
                    break;
                case CaptureToolbarItem.Reset:
                    Add(ToolbarAction.Reset, LucideIcon.RotateCcw, "重新选择  (R)");
                    break;
                case CaptureToolbarItem.Cancel:
                    Add(ToolbarAction.Cancel, LucideIcon.X, "取消  (Esc)");
                    break;
                case CaptureToolbarItem.Confirm:
                    Add(ToolbarAction.Confirm, LucideIcon.Check, GetConfirmTooltip(), width: 38, primary: true);
                    break;
            }
        }
        return buttons.ToArray();
    }

    private static string GetConfirmTooltip() => ConfigService.Current.ConfirmButtonBehavior switch
    {
        ConfirmButtonBehavior.Copy => "完成并复制  (Enter)",
        ConfirmButtonBehavior.Save => "完成并保存  (Enter)",
        ConfirmButtonBehavior.CopyAndSave => "完成、复制并保存  (Enter)",
        ConfirmButtonBehavior.FinishOnly => "仅完成截图  (Enter)",
        _ => "完成并跟随工作流设置  (Enter)"
    };

    private Bitmap? EnsurePixelatedScreen(int pixelBlock)
    {
        pixelBlock = Math.Clamp(pixelBlock, 4, 32);
        if (_pixelatedScreens.TryGetValue(pixelBlock, out Bitmap? cached)) return cached;
        if (_screen is null) return null;

        int smallWidth = Math.Max(1, (int)Math.Ceiling(_screen.Width / (double)pixelBlock));
        int smallHeight = Math.Max(1, (int)Math.Ceiling(_screen.Height / (double)pixelBlock));
        using var small = new Bitmap(smallWidth, smallHeight);
        using (Graphics downsample = Graphics.FromImage(small))
        {
            downsample.CompositingMode = CompositingMode.SourceCopy;
            downsample.InterpolationMode = InterpolationMode.HighQualityBilinear;
            downsample.DrawImage(_screen, new Rectangle(0, 0, smallWidth, smallHeight));
        }

        var pixelated = new Bitmap(_screen.Width, _screen.Height);
        using Graphics upsample = Graphics.FromImage(pixelated);
        upsample.CompositingMode = CompositingMode.SourceCopy;
        upsample.InterpolationMode = InterpolationMode.NearestNeighbor;
        upsample.PixelOffsetMode = PixelOffsetMode.Half;
        upsample.DrawImage(small, new Rectangle(0, 0, _screen.Width, _screen.Height));
        _pixelatedScreens[pixelBlock] = pixelated;
        return pixelated;
    }

    private void DisposePixelatedScreens()
    {
        foreach (Bitmap bitmap in _pixelatedScreens.Values) bitmap.Dispose();
        _pixelatedScreens.Clear();
    }

    private void DrawAnnotations(Graphics graphics)
    {
        if (_annotations.Count == 0) return;

        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (AnnotationItem annotation in _annotations)
        {
            switch (annotation)
            {
                case StrokeAnnotation { IsMosaic: true } mosaic:
                    DrawMosaicStroke(graphics, mosaic);
                    break;
                case StrokeAnnotation stroke:
                    DrawPenStroke(graphics, stroke);
                    break;
                case ArrowAnnotation arrow:
                    DrawArrow(graphics, arrow);
                    break;
                case TextAnnotation text:
                    DrawTextAnnotation(graphics, text);
                    break;
            }
        }
        graphics.SmoothingMode = previousSmoothing;
    }

    private static void DrawPenStroke(Graphics graphics, StrokeAnnotation stroke)
    {
        using var pen = new Pen(stroke.Color, stroke.Width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        if (stroke.Points.Count == 1)
        {
            PointF point = stroke.Points[0];
            using var brush = new SolidBrush(stroke.Color);
            graphics.FillEllipse(
                brush,
                point.X - stroke.Width / 2f,
                point.Y - stroke.Width / 2f,
                stroke.Width,
                stroke.Width);
            return;
        }
        graphics.DrawLines(pen, stroke.Points.ToArray());
    }

    private void DrawMosaicStroke(Graphics graphics, StrokeAnnotation stroke)
    {
        Bitmap? pixelatedScreen = EnsurePixelatedScreen(stroke.PixelSize);
        if (pixelatedScreen is null || stroke.Points.Count == 0) return;

        using var path = new GraphicsPath();
        if (stroke.Points.Count == 1)
        {
            PointF point = stroke.Points[0];
            path.AddEllipse(
                point.X - stroke.Width / 2f,
                point.Y - stroke.Width / 2f,
                stroke.Width,
                stroke.Width);
        }
        else
        {
            path.AddLines(stroke.Points.ToArray());
            using var wideningPen = new Pen(Color.Black, stroke.Width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            path.Widen(wideningPen);
        }

        GraphicsState state = graphics.Save();
        graphics.SetClip(path, CombineMode.Intersect);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImageUnscaled(pixelatedScreen, 0, 0);
        graphics.Restore(state);
    }

    private static void DrawArrow(Graphics graphics, ArrowAnnotation arrow)
    {
        float distance = Distance(arrow.Start, arrow.End);
        if (distance < 1f) return;

        using var pen = new Pen(arrow.Color, arrow.Width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLine(pen, arrow.Start, arrow.End);

        float angle = MathF.Atan2(arrow.End.Y - arrow.Start.Y, arrow.End.X - arrow.Start.X);
        float headLength = Math.Clamp(arrow.Width * 4.2f, 12f, 30f);
        if (arrow.Style == AnnotationArrowStyle.Filled)
        {
            FillArrowHead(graphics, arrow.End, angle, headLength, arrow.Color);
        }
        else
        {
            DrawOpenArrowHead(graphics, pen, arrow.End, angle, headLength);
            if (arrow.Style == AnnotationArrowStyle.Double)
            {
                DrawOpenArrowHead(graphics, pen, arrow.Start, angle + MathF.PI, headLength);
            }
        }
    }

    private static void DrawOpenArrowHead(
        Graphics graphics,
        Pen pen,
        PointF tip,
        float angle,
        float headLength)
    {
        const float spread = 0.58f;
        var left = new PointF(
            tip.X - headLength * MathF.Cos(angle - spread),
            tip.Y - headLength * MathF.Sin(angle - spread));
        var right = new PointF(
            tip.X - headLength * MathF.Cos(angle + spread),
            tip.Y - headLength * MathF.Sin(angle + spread));
        graphics.DrawLine(pen, tip, left);
        graphics.DrawLine(pen, tip, right);
    }

    private static void FillArrowHead(
        Graphics graphics,
        PointF tip,
        float angle,
        float headLength,
        Color color)
    {
        const float spread = 0.52f;
        var left = new PointF(
            tip.X - headLength * MathF.Cos(angle - spread),
            tip.Y - headLength * MathF.Sin(angle - spread));
        var right = new PointF(
            tip.X - headLength * MathF.Cos(angle + spread),
            tip.Y - headLength * MathF.Sin(angle + spread));
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, [tip, left, right]);
    }

    private static void DrawTextAnnotation(Graphics graphics, TextAnnotation text)
    {
        using var font = CreateFont(text);
        using var brush = new SolidBrush(text.Color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.MeasureTrailingSpaces
        };
        graphics.DrawString(text.Text, font, brush, text.Location, format);
    }

    private static Font CreateFont(TextAnnotation text)
    {
        try { return new Font(text.FontFamily, text.FontSize, text.FontStyle); }
        catch { return new Font("Microsoft YaHei UI", text.FontSize, FontStyle.Regular); }
    }

    private static RectangleF GetAnnotationBounds(AnnotationItem annotation)
    {
        switch (annotation)
        {
            case StrokeAnnotation stroke:
            {
                float left = stroke.Points.Min(point => point.X);
                float top = stroke.Points.Min(point => point.Y);
                float right = stroke.Points.Max(point => point.X);
                float bottom = stroke.Points.Max(point => point.Y);
                float padding = stroke.Width / 2f + 3f;
                return RectangleF.FromLTRB(left - padding, top - padding, right + padding, bottom + padding);
            }
            case ArrowAnnotation arrow:
            {
                float padding = Math.Max(34f, arrow.Width / 2f + 6f);
                return RectangleF.FromLTRB(
                    Math.Min(arrow.Start.X, arrow.End.X) - padding,
                    Math.Min(arrow.Start.Y, arrow.End.Y) - padding,
                    Math.Max(arrow.Start.X, arrow.End.X) + padding,
                    Math.Max(arrow.Start.Y, arrow.End.Y) + padding);
            }
            case TextAnnotation text:
            {
                int lines = Math.Max(1, text.Text.Count(character => character == '\n') + 1);
                int longestLine = text.Text.Split('\n').Max(line => line.Length);
                float width = Math.Max(text.FontSize, longestLine * text.FontSize * 0.9f);
                float height = lines * text.FontSize * 1.55f;
                return new RectangleF(text.Location.X - 2f, text.Location.Y - 2f, width + 6f, height + 6f);
            }
            default:
                return RectangleF.Empty;
        }
    }

    private void InvalidateAnnotation(AnnotationItem annotation) =>
        InvalidateAnnotationBounds(GetAnnotationBounds(annotation));

    private void InvalidateAnnotationBounds(RectangleF bounds)
    {
        Rectangle dirty = Rectangle.Ceiling(bounds);
        dirty.Inflate(3, 3);
        dirty.Intersect(ClientRectangle);
        if (!dirty.IsEmpty) Invalidate(dirty);
    }

    private static float Distance(PointF first, PointF second)
    {
        float x = second.X - first.X;
        float y = second.Y - first.Y;
        return MathF.Sqrt(x * x + y * y);
    }

    private void UpdateSmartSelection(Point clientPoint)
    {
        if (_hasSelection || _pendingSmartClick || _dragMode != DragMode.None || !Visible) return;

        _pendingSmartPoint = ClampToClient(clientPoint);
        Point screenPoint = PointToScreen(_pendingSmartPoint);
        _smartSelectionVersion++;
        SmartSelectionTarget? fastTarget = SmartSelectionService.ResolveFast(screenPoint, Handle);
        _smartFastTarget = fastTarget;
        OfferSmartTarget(fastTarget, screenPoint, allowImmediate: _smartTarget is null);

        Cursor = _smartTarget is not null || fastTarget is not null ? Cursors.Hand : Cursors.Cross;
        if (!_smartSelectionResolving && fastTarget is not null)
        {
            RefineSmartSelection();
        }
    }

    private void OfferSmartTarget(
        SmartSelectionTarget? proposedTarget,
        Point screenPoint,
        bool allowImmediate = false)
    {
        if (proposedTarget is not SmartSelectionTarget proposed) return;

        if (_smartTarget is not SmartSelectionTarget current || allowImmediate)
        {
            CommitSmartTarget(proposed);
            return;
        }

        if (IsSameSmartRegion(current, proposed))
        {
            if (!string.Equals(current.Label, proposed.Label, StringComparison.Ordinal) &&
                IsGenericSmartLabel(current.Label) &&
                !IsGenericSmartLabel(proposed.Label))
            {
                _smartTarget = proposed;
                Invalidate();
            }

            return;
        }

        bool nestedTarget = current.ScreenBounds.Contains(proposed.ScreenBounds);
        bool coarserTarget = proposed.ScreenBounds.Contains(current.ScreenBounds);

        // A native/window fallback must never pull a stable small control back to its parent.
        if (coarserTarget && current.ScreenBounds.Contains(screenPoint))
        {
            return;
        }

        // Overlapping peers cannot steal focus until the pointer has actually left the current one.
        if (!nestedTarget && current.ScreenBounds.Contains(screenPoint))
        {
            return;
        }

        CommitSmartTarget(proposed);
    }

    private void CommitSmartTarget(SmartSelectionTarget target)
    {
        bool changed = _smartTarget is not SmartSelectionTarget current ||
                       !IsSameSmartRegion(current, target) ||
                       !string.Equals(current.Label, target.Label, StringComparison.Ordinal);
        _smartTarget = target;
        if (changed) Invalidate();
    }

    private static bool IsSameSmartRegion(
        SmartSelectionTarget first,
        SmartSelectionTarget second) =>
        first.WindowHandle == second.WindowHandle && first.ScreenBounds == second.ScreenBounds;

    private static bool IsGenericSmartLabel(string label) =>
        label is "窗口" or "控件" or "区域";

    private async void RefineSmartSelection()
    {
        if (_smartSelectionResolving || _hasSelection || _pendingSmartClick || !Visible)
        {
            return;
        }

        int version = _smartSelectionVersion;
        Point screenPoint = PointToScreen(_pendingSmartPoint);
        if (_smartFastTarget is not SmartSelectionTarget fastTarget) return;

        _smartSelectionResolving = true;
        SmartSelectionTarget refined = fastTarget;
        try
        {
            refined = await Task.Run(() => SmartSelectionService.Refine(fastTarget, screenPoint));
        }
        catch
        {
            // Hit testing is best-effort; inaccessible or closing applications should not block capture.
        }
        finally
        {
            _smartSelectionResolving = false;
        }

        if (IsDisposed || !Visible || _hasSelection || _pendingSmartClick) return;
        Point currentScreenPoint = PointToScreen(_pendingSmartPoint);
        bool fastTargetIsCurrent = _smartFastTarget is SmartSelectionTarget latestFastTarget &&
                                   IsSameSmartRegion(latestFastTarget, fastTarget);
        if (fastTargetIsCurrent && refined.ScreenBounds.Contains(currentScreenPoint))
        {
            OfferSmartTarget(refined, currentScreenPoint);
        }

        if (version != _smartSelectionVersion && _smartFastTarget is not null)
        {
            RefineSmartSelection();
        }
    }

    private Rectangle ScreenToClientBounds(Rectangle screenBounds)
    {
        Rectangle bounds = RectangleToClient(screenBounds);
        return Rectangle.Intersect(bounds, ClientRectangle);
    }

    private void DrawSmartSelectionCandidate(Graphics graphics)
    {
        if (_screen is null || _smartTarget is not SmartSelectionTarget target) return;

        Rectangle bounds = ScreenToClientBounds(target.ScreenBounds);
        if (bounds.Width < MinimumSelectionSize || bounds.Height < MinimumSelectionSize) return;

        graphics.DrawImage(_screen, bounds, bounds, GraphicsUnit.Pixel);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        ThemePalette palette = ThemeManager.Palette;

        using (var borderPen = new Pen(palette.AccentColor, 2f) { LineJoin = LineJoin.Round })
        {
            graphics.DrawRectangle(
                borderPen,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height);
        }

        string label = $"{target.Label}  ·  {bounds.Width} × {bounds.Height}  ·  单击选择";
        using var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        Size size = TextRenderer.MeasureText(graphics, label, font, Size.Empty, TextFormatFlags.NoPadding);
        int width = Math.Min(size.Width + 16, Math.Max(80, ClientSize.Width - 8));
        int x = Math.Clamp(bounds.Left, 4, Math.Max(4, ClientSize.Width - width - 4));
        int y = bounds.Top - size.Height - 10 < 4
            ? Math.Min(ClientSize.Height - size.Height - 10, bounds.Top + 8)
            : bounds.Top - size.Height - 10;
        var labelBounds = new Rectangle(x, Math.Max(4, y), width, size.Height + 6);

        var shadowBounds = new Rectangle(labelBounds.X, labelBounds.Y + 1, labelBounds.Width, labelBounds.Height);
        using (var shadowPath = RoundedRectangle(shadowBounds, 6))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
        {
            graphics.FillPath(shadowBrush, shadowPath);
        }

        using var path = RoundedRectangle(labelBounds, 6);
        using var background = new SolidBrush(WithAlpha(palette.CardBg, 246));
        using var outline = new Pen(WithAlpha(palette.CardBorder, 200), 1f);
        graphics.FillPath(background, path);
        graphics.DrawPath(outline, path);
        TextRenderer.DrawText(
            graphics,
            label,
            font,
            labelBounds,
            palette.TextPrimary,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);
    }

    private void DrawDimmedOutside(Graphics graphics, Brush dim)
    {
        graphics.FillRectangle(dim, new Rectangle(0, 0, Width, _selection.Top));
        graphics.FillRectangle(dim, new Rectangle(0, _selection.Bottom, Width, Height - _selection.Bottom));
        graphics.FillRectangle(dim, new Rectangle(0, _selection.Top, _selection.Left, _selection.Height));
        graphics.FillRectangle(dim, new Rectangle(_selection.Right, _selection.Top, Width - _selection.Right, _selection.Height));
    }

    private void DrawResizeHandles(Graphics graphics)
    {
        if (_selection.Width < 20 || _selection.Height < 20) return;

        ThemePalette palette = ThemeManager.Palette;
        bool isDark = palette.Mode == ThemeMode.Dark;

        float diameter = 9f;
        float radius = diameter / 2f;

        // Accent ring color with high-luminance fallback
        Color ringColor = (palette.AccentColor.R > 220 && palette.AccentColor.G > 220 && palette.AccentColor.B > 220)
            ? (isDark ? Color.FromArgb(210, 40, 40, 40) : Color.FromArgb(180, 100, 100, 100))
            : palette.AccentColor;

        using var shadowBrush = new SolidBrush(Color.FromArgb(45, 0, 0, 0));
        using var fillBrush = new SolidBrush(Color.White);
        using var borderPen = new Pen(ringColor, 1.5f);

        foreach (Point center in GetHandleCenters())
        {
            float cx = center.X;
            float cy = center.Y;

            // 1. Soft drop shadow (1px offset down)
            graphics.FillEllipse(shadowBrush, cx - radius, cy - radius + 1f, diameter, diameter);

            // 2. Crisp handle body (smooth white center)
            graphics.FillEllipse(fillBrush, cx - radius, cy - radius, diameter, diameter);

            // 3. Accent ring border
            graphics.DrawEllipse(borderPen, cx - radius, cy - radius, diameter, diameter);
        }
    }

    private Point[] GetHandleCenters()
    {
        int centerX = _selection.Left + _selection.Width / 2;
        int centerY = _selection.Top + _selection.Height / 2;
        return
        [
            new(_selection.Left, _selection.Top),
            new(centerX, _selection.Top),
            new(_selection.Right, _selection.Top),
            new(_selection.Left, centerY),
            new(_selection.Right, centerY),
            new(_selection.Left, _selection.Bottom),
            new(centerX, _selection.Bottom),
            new(_selection.Right, _selection.Bottom)
        ];
    }

    private void DrawSizeLabel(Graphics graphics)
    {
        ThemePalette palette = ThemeManager.Palette;
        string label = $"{_selection.Width} × {_selection.Height}";
        Size size = TextRenderer.MeasureText(graphics, label, SizeLabelFont, Size.Empty, TextFormatFlags.NoPadding);
        int labelWidth = size.Width + 16;
        int labelHeight = size.Height + 6;

        int x = Math.Clamp(_selection.Left, 4, Math.Max(4, ClientSize.Width - labelWidth - 4));
        int y = _selection.Top - labelHeight - 8 < 4
            ? _selection.Top + 8
            : _selection.Top - labelHeight - 8;
        var bounds = new Rectangle(x, y, labelWidth, labelHeight);

        // Soft subtle drop shadow
        var shadowBounds = new Rectangle(bounds.X, bounds.Y + 1, bounds.Width, bounds.Height);
        using (var shadowPath = RoundedRectangle(shadowBounds, 6))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
        {
            graphics.FillPath(shadowBrush, shadowPath);
        }

        using var path = RoundedRectangle(bounds, 6);
        using var background = new SolidBrush(WithAlpha(palette.CardBg, 246));
        using var outline = new Pen(WithAlpha(palette.CardBorder, 200), 1f);
        graphics.FillPath(background, path);
        graphics.DrawPath(outline, path);

        TextRenderer.DrawText(
            graphics,
            label,
            SizeLabelFont,
            bounds,
            palette.TextPrimary,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding);
    }

    private Rectangle ComputeSelectionDirtyRect(Rectangle oldSel, Rectangle newSel)
    {
        Rectangle union;
        if (oldSel.IsEmpty || oldSel.Width <= 0 || oldSel.Height <= 0)
        {
            union = newSel;
        }
        else if (newSel.IsEmpty || newSel.Width <= 0 || newSel.Height <= 0)
        {
            union = oldSel;
        }
        else
        {
            union = Rectangle.Union(oldSel, newSel);
        }

        int margin = HandleSize + 18;
        union.Inflate(margin, margin);

        Rectangle oldLabel = GetSizeLabelDirtyBounds(oldSel);
        Rectangle newLabel = GetSizeLabelDirtyBounds(newSel);
        if (!oldLabel.IsEmpty) union = Rectangle.Union(union, oldLabel);
        if (!newLabel.IsEmpty) union = Rectangle.Union(union, newLabel);

        return Rectangle.Intersect(union, ClientRectangle);
    }

    private Rectangle GetSizeLabelDirtyBounds(Rectangle sel)
    {
        if (sel.IsEmpty || sel.Width <= 0 || sel.Height <= 0) return Rectangle.Empty;
        int width = 120;
        int height = 30;
        int y = sel.Top - height - 8 < 4
            ? sel.Top + 8
            : sel.Top - height - 8;
        int x = Math.Clamp(sel.Left, 4, Math.Max(4, ClientSize.Width - width - 4));
        var bounds = new Rectangle(x - 4, y - 4, width + 8, height + 8);
        return Rectangle.Intersect(bounds, ClientRectangle);
    }

    private void DrawToolbar(Graphics graphics)
    {
        Rectangle bounds = GetToolbarBounds();
        if (bounds.IsEmpty) return;

        ThemePalette palette = ThemeManager.Palette;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var shadowPath = RoundedRectangle(
            new Rectangle(bounds.X, bounds.Y + 2, bounds.Width, bounds.Height),
            9);
        using var shadow = new SolidBrush(Color.FromArgb(48, 0, 0, 0));
        graphics.FillPath(shadow, shadowPath);

        using var path = RoundedRectangle(bounds, 9);
        using var background = new SolidBrush(WithAlpha(palette.CardBg, 246));
        using var outline = new Pen(WithAlpha(palette.CardBorder, 205), 1f);
        graphics.FillPath(background, path);
        graphics.DrawPath(outline, path);

        LogoRenderer.DrawLogo(graphics, bounds.Left + 11, bounds.Top + 12, 20, palette.TextPrimary);
        Color subtleSeparator = WithAlpha(palette.TextSecondary, 80);
        DrawSeparator(graphics, bounds.Left + 37, bounds.Top + 16, bounds.Top + ToolbarHeight - 16, subtleSeparator);

        foreach (ToolbarButton button in GetToolbarButtons())
        {
            bool active = button.Action == _activeTool ||
                          button.Action == ToolbarAction.Cursor && _includeCursor;
            bool hovered = button.Action == _hoveredAction;
            bool pressed = button.Action == _pressedAction && hovered;

            Color buttonFill = Color.Transparent;
            Color iconColor = palette.TextSecondary;
            if (button.IsPrimary)
            {
                if (hovered)
                {
                    buttonFill = pressed
                        ? WithAlpha(palette.TextPrimary, 34)
                        : WithAlpha(palette.TextPrimary, 22);
                }
                iconColor = palette.AccentColor;
            }
            else if (active)
            {
                buttonFill = WithAlpha(palette.AccentColor, pressed ? 46 : hovered ? 34 : 22);
                iconColor = palette.AccentColor;
            }
            else if (hovered)
            {
                buttonFill = pressed
                    ? WithAlpha(palette.TextPrimary, 34)
                    : WithAlpha(palette.TextPrimary, 22);
                iconColor = palette.TextPrimary;
            }

            if (buttonFill.A > 0)
            {
                using var buttonPath = RoundedRectangle(button.Bounds, 6);
                using var fill = new SolidBrush(buttonFill);
                graphics.FillPath(fill, buttonPath);
            }

            if (active)
            {
                using var indicator = new Pen(palette.AccentColor, 2f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(
                    indicator,
                    button.Bounds.Left + 10,
                    button.Bounds.Bottom - 3,
                    button.Bounds.Right - 10,
                    button.Bounds.Bottom - 3);
            }

            float iconSize = button.IsPrimary ? 18f : 17f;
            float iconX = button.Bounds.Left + (button.Bounds.Width - iconSize) / 2f;
            float iconY = button.Bounds.Top + (button.Bounds.Height - iconSize) / 2f;
            LucideRenderer.Draw(graphics, button.Icon, iconX, iconY, iconSize, iconColor, 1.9f);
        }

        DrawToolbarTooltip(graphics, bounds, palette);
    }

    private Rectangle GetStyleBarBounds()
    {
        if (!_hasSelection || !_showStyleBar) return Rectangle.Empty;

        int preferredWidth = _activeTool switch
        {
            ToolbarAction.Text => 620,
            ToolbarAction.Arrow => 580,
            ToolbarAction.Pen => 580,
            ToolbarAction.Mosaic => 580,
            _ => 220
        };
        int width = Math.Min(preferredWidth, Math.Max(180, ClientSize.Width - 16));
        int height = _activeTool is ToolbarAction.Pen or ToolbarAction.Arrow or ToolbarAction.Mosaic ? 82 : 38;
        Rectangle toolbar = GetToolbarBounds();
        int x = Math.Clamp(toolbar.Left, 8, Math.Max(8, ClientSize.Width - width - 8));

        bool toolbarBelowSelection = toolbar.Top >= _selection.Bottom;
        int y = toolbarBelowSelection ? toolbar.Bottom + 5 : toolbar.Top - height - 5;
        if (y < 8 || y + height > ClientSize.Height - 8)
        {
            y = toolbarBelowSelection ? toolbar.Top - height - 5 : toolbar.Bottom + 5;
        }
        y = Math.Clamp(y, 8, Math.Max(8, ClientSize.Height - height - 8));
        return new Rectangle(x, y, width, height);
    }

    private StyleButton[] GetStyleButtons()
    {
        Rectangle bounds = GetStyleBarBounds();
        if (bounds.IsEmpty) return [];
        var buttons = new List<StyleButton>();

        void Add(StyleAction action, int x, int y = 6, int width = 36, int height = 30, float value = 0f) =>
            buttons.Add(new StyleButton(action, new Rectangle(bounds.Left + x, bounds.Top + y, width, height), value));

        void AddColors(int startX, int y, int count = 11, int step = 28)
        {
            for (int index = 0; index < count; index++)
            {
                Add((StyleAction)((int)StyleAction.ColorRed + index), startX + index * step, y, 24, 28);
            }
        }

        void AddPresets(StyleAction action, int y, params float[] values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                Add(action, 270 + index * 40, y, 36, 30, values[index]);
            }
        }

        switch (_activeTool)
        {
            case ToolbarAction.Pen:
                AddPresets(StyleAction.SetPrimaryPreset, 6, 1, 2, 4, 8, 16, 32, 48);
                AddColors(54, 47);
                break;
            case ToolbarAction.Arrow:
                AddPresets(StyleAction.SetPrimaryPreset, 6, 1, 2, 4, 8, 16, 32, 48);
                Add(StyleAction.ArrowOpen, 54, 47, 36, 28);
                Add(StyleAction.ArrowFilled, 94, 47, 36, 28);
                Add(StyleAction.ArrowDouble, 134, 47, 36, 28);
                AddColors(184, 47);
                break;
            case ToolbarAction.Mosaic:
                AddPresets(StyleAction.SetPrimaryPreset, 6, 8, 16, 24, 40, 60, 80);
                AddPresets(StyleAction.SetSecondaryPreset, 46, 4, 8, 12, 16, 24, 32);
                break;
            case ToolbarAction.Text:
                Add(StyleAction.Bold, 270, 5, 30, 28);
                Add(StyleAction.Italic, 304, 5, 30, 28);
                AddColors(342, 5, 11, 24);
                break;
            default:
                AddColors(12, 5);
                break;
        }
        return buttons.ToArray();
    }

    private void DrawStyleBar(Graphics graphics)
    {
        Rectangle bounds = GetStyleBarBounds();
        if (bounds.IsEmpty) return;
        ThemePalette palette = ThemeManager.Palette;
        int renderKey = GetStyleBarRenderKey(bounds, palette);
        if (_styleBarLayer.Size != bounds.Size || _styleBarRenderKey != renderKey)
        {
            _styleBarLayer.Render(bounds.Size, canvas =>
            {
                // Hit testing stays in WinForms coordinates. Translating here lets the
                // Skia renderer consume those exact bounds without duplicating layout.
                canvas.Translate(-bounds.Left, -bounds.Top);

            using (SKPaint shadow = SkiaDrawing.Fill(Color.FromArgb(42, 0, 0, 0)))
            using (SKPaint background = SkiaDrawing.Fill(WithAlpha(palette.CardBg, 250)))
            using (SKPaint outline = SkiaDrawing.Stroke(WithAlpha(palette.CardBorder, 220), 1f))
            {
                canvas.DrawRoundRect(
                    SkiaDrawing.ToSkRect(new Rectangle(bounds.X, bounds.Y + 2, bounds.Width, bounds.Height)),
                    8f,
                    8f,
                    shadow);
                canvas.DrawRoundRect(SkiaDrawing.ToSkRect(bounds), 8f, 8f, background);
                canvas.DrawRoundRect(SkiaDrawing.ToSkRect(bounds), 8f, 8f, outline);
            }

            if (_activeTool is ToolbarAction.Pen or ToolbarAction.Arrow)
            {
                DrawStyleSlider(canvas, StyleSliderKind.PenWidth, "粗细", palette);
                DrawStyleText(canvas, _activeTool == ToolbarAction.Arrow ? "箭头" : "颜色",
                    palette.TextSecondary, bounds.Left + 11, bounds.Top + 52, 42);
            }
            else if (_activeTool == ToolbarAction.Mosaic)
            {
                DrawStyleSlider(canvas, StyleSliderKind.MosaicBrush, "笔刷", palette);
                DrawStyleSlider(canvas, StyleSliderKind.MosaicPixel, "颗粒", palette);
            }

            FontStyle configuredFontStyle = IsValidFontStyle(ConfigService.Current.AnnotationFontStyle)
                ? (FontStyle)ConfigService.Current.AnnotationFontStyle
                : FontStyle.Regular;
            Color configuredColor = GetAnnotationColor();
                foreach (StyleButton button in GetStyleButtons())
                {
                    bool active = button.Action switch
                    {
                        StyleAction.Bold => configuredFontStyle.HasFlag(FontStyle.Bold),
                        StyleAction.Italic => configuredFontStyle.HasFlag(FontStyle.Italic),
                        StyleAction.ArrowOpen => ConfigService.Current.AnnotationArrowStyle == AnnotationArrowStyle.Open,
                        StyleAction.ArrowFilled => ConfigService.Current.AnnotationArrowStyle == AnnotationArrowStyle.Filled,
                        StyleAction.ArrowDouble => ConfigService.Current.AnnotationArrowStyle == AnnotationArrowStyle.Double,
                        StyleAction.SetPrimaryPreset => IsPrimaryPresetActive(button.Value),
                        StyleAction.SetSecondaryPreset => Math.Abs(ConfigService.Current.AnnotationMosaicPixelSize - button.Value) < 0.1f,
                        _ when IsColorAction(button.Action) => ColorsEqual(configuredColor, GetStyleColor(button.Action)),
                        _ => false
                    };

                    SKRect buttonRect = SkiaDrawing.ToSkRect(button.Bounds);
                    if (active)
                    {
                        using SKPaint activeFill = SkiaDrawing.Fill(WithAlpha(palette.AccentColor, 38));
                        canvas.DrawRoundRect(buttonRect, 5f, 5f, activeFill);
                    }
                    else if (button.Action is StyleAction.SetPrimaryPreset or StyleAction.SetSecondaryPreset)
                    {
                        using SKPaint presetFill = SkiaDrawing.Fill(WithAlpha(palette.TextPrimary, 10));
                        using SKPaint presetOutline = SkiaDrawing.Stroke(WithAlpha(palette.TextPrimary, 24), 1f);
                        canvas.DrawRoundRect(buttonRect, 5f, 5f, presetFill);
                        canvas.DrawRoundRect(buttonRect, 5f, 5f, presetOutline);
                    }

                    DrawStyleButtonContent(canvas, button, active ? palette.AccentColor : palette.TextPrimary, palette);
                }
            });
            _styleBarRenderKey = renderKey;
        }
        _styleBarLayer.Draw(graphics, bounds.Location);
    }

    private int GetStyleBarRenderKey(Rectangle bounds, ThemePalette palette)
    {
        var hash = new HashCode();
        hash.Add(bounds.Size);
        hash.Add(_activeTool);
        hash.Add(palette.CardBg.ToArgb());
        hash.Add(palette.CardBorder.ToArgb());
        hash.Add(palette.TextPrimary.ToArgb());
        hash.Add(palette.TextSecondary.ToArgb());
        hash.Add(palette.TextMuted.ToArgb());
        hash.Add(palette.AccentColor.ToArgb());
        hash.Add(ConfigService.Current.AnnotationPenWidth);
        hash.Add(ConfigService.Current.AnnotationMosaicSize);
        hash.Add(ConfigService.Current.AnnotationMosaicPixelSize);
        hash.Add(ConfigService.Current.AnnotationFontStyle);
        hash.Add(ConfigService.Current.AnnotationArrowStyle);
        hash.Add(ConfigService.Current.AnnotationColorHex, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    private void DrawStyleSlider(SKCanvas canvas, StyleSliderKind kind, string label, ThemePalette palette)
    {
        Rectangle bounds = GetStyleBarBounds();
        Rectangle track = GetStyleSliderTrack(kind);
        if (track.IsEmpty) return;

        (float minimum, float maximum, float value) = GetStyleSliderValues(kind);
        float amount = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        int rowTop = kind == StyleSliderKind.MosaicPixel ? bounds.Top + 46 : bounds.Top + 6;
        DrawStyleText(canvas, label, palette.TextSecondary, bounds.Left + 11, rowTop + 6, 40);

        using SKPaint trackPen = SkiaDrawing.Stroke(WithAlpha(palette.TextMuted, 92), 4f, SKStrokeCap.Round);
        using SKPaint progressPen = SkiaDrawing.Stroke(palette.AccentColor, 4f, SKStrokeCap.Round);
        int centerY = track.Top + track.Height / 2;
        canvas.DrawLine(track.Left, centerY, track.Right, centerY, trackPen);
        float thumbX = track.Left + track.Width * amount;
        canvas.DrawLine(track.Left, centerY, thumbX, centerY, progressPen);

        using SKPaint thumbFill = SkiaDrawing.Fill(palette.AccentColor);
        using SKPaint thumbOutline = SkiaDrawing.Stroke(WithAlpha(palette.CardBg, 250), 2f);
        canvas.DrawCircle(thumbX, centerY, 6f, thumbFill);
        canvas.DrawCircle(thumbX, centerY, 6f, thumbOutline);

        var valueBounds = new Rectangle(bounds.Left + 216, rowTop, 44, 30);
        using SKPaint valueFill = SkiaDrawing.Fill(WithAlpha(palette.TextPrimary, 18));
        canvas.DrawRoundRect(SkiaDrawing.ToSkRect(valueBounds), 6f, 6f, valueFill);
        SkiaDrawing.DrawText(
            canvas,
            value.ToString(kind == StyleSliderKind.PenWidth ? "0.#" : "0"),
            "Segoe UI",
            10.8f,
            palette.TextPrimary,
            valueBounds.Left + valueBounds.Width / 2f,
            valueBounds.Top + valueBounds.Height / 2f,
            SKFontStyleWeight.Bold,
            align: SKTextAlign.Center);
    }

    private static void DrawStyleText(
        SKCanvas canvas,
        string text,
        Color color,
        int x,
        int y,
        int width)
    {
        SkiaDrawing.DrawText(
            canvas,
            text,
            "Microsoft YaHei UI",
            11.1f,
            color,
            x,
            y + 9f);
    }

    private static void DrawStyleButtonContent(
        SKCanvas canvas,
        StyleButton button,
        Color color,
        ThemePalette palette)
    {
        if (IsColorAction(button.Action))
        {
            Color swatch = GetStyleColor(button.Action);
            var swatchBounds = new Rectangle(button.Bounds.Left + 3, button.Bounds.Top + 3, 16, 16);
            using SKPaint brush = SkiaDrawing.Fill(swatch);
            using SKPaint pen = SkiaDrawing.Stroke(WithAlpha(palette.TextPrimary, 100), 1f);
            canvas.DrawRoundRect(SkiaDrawing.ToSkRect(swatchBounds), 2f, 2f, brush);
            canvas.DrawRoundRect(SkiaDrawing.ToSkRect(swatchBounds), 2f, 2f, pen);
            return;
        }

        if (button.Action is StyleAction.SetPrimaryPreset or StyleAction.SetSecondaryPreset)
        {
            SkiaDrawing.DrawText(
                canvas,
                button.Value.ToString("0.#"),
                "Segoe UI",
                10.8f,
                color,
                button.Bounds.Left + button.Bounds.Width / 2f,
                button.Bounds.Top + button.Bounds.Height / 2f,
                align: SKTextAlign.Center);
            return;
        }

        string? glyph = button.Action switch
        {
            StyleAction.Bold => "B",
            StyleAction.Italic => "I",
            _ => null
        };
        if (glyph is not null)
        {
            SkiaDrawing.DrawText(
                canvas,
                glyph,
                "Segoe UI",
                13.3f,
                color,
                button.Bounds.Left + button.Bounds.Width / 2f,
                button.Bounds.Top + button.Bounds.Height / 2f,
                button.Action == StyleAction.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                button.Action == StyleAction.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright,
                SKTextAlign.Center);
            return;
        }

        float centerY = button.Bounds.Top + button.Bounds.Height / 2f;
        using SKPaint arrowPen = SkiaDrawing.Stroke(color, 1.8f, SKStrokeCap.Round);
        SKPoint start = new(button.Bounds.Left + 6, centerY);
        SKPoint end = new(button.Bounds.Right - 6, centerY);
        canvas.DrawLine(start, end, arrowPen);
        if (button.Action == StyleAction.ArrowFilled)
        {
            using var head = new SKPath();
            head.MoveTo(end);
            head.LineTo(end.X - 7f, end.Y - 4.2f);
            head.LineTo(end.X - 7f, end.Y + 4.2f);
            head.Close();
            using SKPaint fill = SkiaDrawing.Fill(color);
            canvas.DrawPath(head, fill);
        }
        else
        {
            canvas.DrawLine(end, new SKPoint(end.X - 7f, end.Y - 4.2f), arrowPen);
            canvas.DrawLine(end, new SKPoint(end.X - 7f, end.Y + 4.2f), arrowPen);
            if (button.Action == StyleAction.ArrowDouble)
            {
                canvas.DrawLine(start, new SKPoint(start.X + 7f, start.Y - 4.2f), arrowPen);
                canvas.DrawLine(start, new SKPoint(start.X + 7f, start.Y + 4.2f), arrowPen);
            }
        }
    }

    private StyleButton? HitTestStyleBar(Point point)
    {
        foreach (StyleButton button in GetStyleButtons())
        {
            if (button.Bounds.Contains(point)) return button;
        }
        return null;
    }

    private void ExecuteStyleAction(StyleButton button)
    {
        StyleAction action = button.Action;
        switch (action)
        {
            case StyleAction.SetPrimaryPreset when _activeTool == ToolbarAction.Mosaic:
                ConfigService.Current.AnnotationMosaicSize = Math.Clamp(button.Value, 8f, 80f);
                break;
            case StyleAction.SetPrimaryPreset:
                ConfigService.Current.AnnotationPenWidth = Math.Clamp(button.Value, 1f, 48f);
                break;
            case StyleAction.SetSecondaryPreset:
                ConfigService.Current.AnnotationMosaicPixelSize = (int)Math.Clamp(button.Value, 4f, 32f);
                break;
            case StyleAction.ArrowOpen:
                ConfigService.Current.AnnotationArrowStyle = AnnotationArrowStyle.Open;
                break;
            case StyleAction.ArrowFilled:
                ConfigService.Current.AnnotationArrowStyle = AnnotationArrowStyle.Filled;
                break;
            case StyleAction.ArrowDouble:
                ConfigService.Current.AnnotationArrowStyle = AnnotationArrowStyle.Double;
                break;
            case StyleAction.Bold:
                ToggleConfiguredFontStyle(FontStyle.Bold);
                break;
            case StyleAction.Italic:
                ToggleConfiguredFontStyle(FontStyle.Italic);
                break;
            default:
                if (IsColorAction(action))
                {
                    Color color = GetStyleColor(action);
                    ConfigService.Current.AnnotationColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                }
                break;
        }

        ConfigService.Save();
        ApplyInlineTextStyle();
        UpdateInlineStyleControls();
        Invalidate(GetStyleBarBounds());
    }

    private bool TryBeginStyleSlider(Point point)
    {
        StyleSliderKind kind = HitTestStyleSlider(point);
        if (kind == StyleSliderKind.None) return false;
        _activeStyleSlider = kind;
        Capture = true;
        Cursor = Cursors.SizeWE;
        UpdateStyleSlider(kind, point);
        return true;
    }

    private StyleSliderKind HitTestStyleSlider(Point point)
    {
        StyleSliderKind[] candidates = _activeTool switch
        {
            ToolbarAction.Pen or ToolbarAction.Arrow => [StyleSliderKind.PenWidth],
            ToolbarAction.Mosaic => [StyleSliderKind.MosaicBrush, StyleSliderKind.MosaicPixel],
            _ => []
        };

        foreach (StyleSliderKind kind in candidates)
        {
            Rectangle hitBounds = Rectangle.Inflate(GetStyleSliderTrack(kind), 7, 13);
            if (hitBounds.Contains(point)) return kind;
        }
        return StyleSliderKind.None;
    }

    private Rectangle GetStyleSliderTrack(StyleSliderKind kind)
    {
        Rectangle bounds = GetStyleBarBounds();
        if (bounds.IsEmpty || kind == StyleSliderKind.None) return Rectangle.Empty;
        int y = kind == StyleSliderKind.MosaicPixel ? bounds.Top + 58 : bounds.Top + 18;
        return new Rectangle(bounds.Left + 58, y, 148, 4);
    }

    private Rectangle GetStyleValueBounds(StyleSliderKind kind)
    {
        Rectangle bounds = GetStyleBarBounds();
        if (bounds.IsEmpty || kind == StyleSliderKind.None) return Rectangle.Empty;
        int rowTop = kind == StyleSliderKind.MosaicPixel ? bounds.Top + 46 : bounds.Top + 6;
        return new Rectangle(bounds.Left + 216, rowTop, 44, 30);
    }

    private static (float Minimum, float Maximum, float Value) GetStyleSliderValues(StyleSliderKind kind) => kind switch
    {
        StyleSliderKind.PenWidth => (1f, 48f, Math.Clamp(ConfigService.Current.AnnotationPenWidth, 0.5f, MaxCustomPenWidth)),
        StyleSliderKind.MosaicBrush => (8f, 80f, Math.Clamp(ConfigService.Current.AnnotationMosaicSize, 1f, MaxCustomMosaicBrush)),
        StyleSliderKind.MosaicPixel => (4f, 32f, Math.Clamp(ConfigService.Current.AnnotationMosaicPixelSize, 1, MaxCustomMosaicPixel)),
        _ => (0f, 1f, 0f)
    };

    private bool TryBeginStyleValueEdit(Point point)
    {
        StyleSliderKind[] candidates = _activeTool switch
        {
            ToolbarAction.Pen or ToolbarAction.Arrow => [StyleSliderKind.PenWidth],
            ToolbarAction.Mosaic => [StyleSliderKind.MosaicBrush, StyleSliderKind.MosaicPixel],
            _ => []
        };

        foreach (StyleSliderKind kind in candidates)
        {
            if (!GetStyleValueBounds(kind).Contains(point)) continue;
            BeginStyleValueEdit(kind);
            return true;
        }
        return false;
    }

    private void BeginStyleValueEdit(StyleSliderKind kind)
    {
        EnsureStyleValueEditor();
        if (_styleValueEditor is null) return;
        EnableOverlayTextInput();

        _editingStyleValue = kind;
        (_, _, float value) = GetStyleSliderValues(kind);
        Rectangle bounds = GetStyleValueBounds(kind);
        ThemePalette palette = ThemeManager.Palette;
        _styleValueEditor.Bounds = Rectangle.Inflate(bounds, -1, -2);
        _styleValueEditor.BackColor = Color.FromArgb(255, palette.InputBg);
        _styleValueEditor.ForeColor = palette.TextPrimary;
        _styleValueEditor.Text = value.ToString(kind == StyleSliderKind.PenWidth ? "0.#" : "0", CultureInfo.InvariantCulture);
        _styleValueEditor.Visible = true;
        _styleValueEditor.BringToFront();
        _styleValueEditor.Focus();
        _styleValueEditor.SelectAll();
    }

    private void EnsureStyleValueEditor()
    {
        if (_styleValueEditor is not null) return;
        _styleValueEditor = new TextBox
        {
            Visible = false,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", 8.3f, FontStyle.Bold),
            MaxLength = 7
        };
        _styleValueEditor.KeyPress += (_, e) =>
        {
            if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar) || e.KeyChar is '.' or ',') return;
            e.Handled = true;
        };
        _styleValueEditor.LostFocus += (_, _) =>
        {
            if (!_committingStyleValue && _styleValueEditor.Visible) CommitStyleValueEdit();
        };
        Controls.Add(_styleValueEditor);
    }

    private void CommitStyleValueEdit()
    {
        if (_committingStyleValue || _styleValueEditor is not { Visible: true } editor) return;
        _committingStyleValue = true;
        try
        {
            string normalized = editor.Text.Trim().Replace(',', '.');
            if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                float.IsFinite(value))
            {
                switch (_editingStyleValue)
                {
                    case StyleSliderKind.PenWidth:
                        ConfigService.Current.AnnotationPenWidth = Math.Clamp(value, 0.5f, MaxCustomPenWidth);
                        break;
                    case StyleSliderKind.MosaicBrush:
                        ConfigService.Current.AnnotationMosaicSize = Math.Clamp(value, 1f, MaxCustomMosaicBrush);
                        break;
                    case StyleSliderKind.MosaicPixel:
                        ConfigService.Current.AnnotationMosaicPixelSize = Math.Clamp((int)MathF.Round(value), 1, MaxCustomMosaicPixel);
                        break;
                }
                ConfigService.Save();
            }

            editor.Visible = false;
            _editingStyleValue = StyleSliderKind.None;
            Focus();
            Invalidate(GetStyleBarBounds());
        }
        finally
        {
            _committingStyleValue = false;
        }
    }

    private void CancelStyleValueEdit()
    {
        if (_styleValueEditor is null) return;
        _committingStyleValue = true;
        try
        {
            _styleValueEditor.Visible = false;
            _editingStyleValue = StyleSliderKind.None;
        }
        finally
        {
            _committingStyleValue = false;
        }
    }

    private void UpdateStyleSlider(StyleSliderKind kind, Point point)
    {
        Rectangle track = GetStyleSliderTrack(kind);
        if (track.IsEmpty) return;
        float amount = Math.Clamp((point.X - track.Left) / (float)track.Width, 0f, 1f);
        (float minimum, float maximum, _) = GetStyleSliderValues(kind);
        float raw = minimum + (maximum - minimum) * amount;
        switch (kind)
        {
            case StyleSliderKind.PenWidth:
                ConfigService.Current.AnnotationPenWidth = Math.Clamp(MathF.Round(raw * 2f) / 2f, 1f, 48f);
                break;
            case StyleSliderKind.MosaicBrush:
                ConfigService.Current.AnnotationMosaicSize = Math.Clamp(MathF.Round(raw / 2f) * 2f, 8f, 80f);
                break;
            case StyleSliderKind.MosaicPixel:
                ConfigService.Current.AnnotationMosaicPixelSize = Math.Clamp((int)MathF.Round(raw / 2f) * 2, 4, 32);
                break;
        }
        Invalidate(GetStyleBarBounds());
    }

    private bool IsPrimaryPresetActive(float value) => _activeTool == ToolbarAction.Mosaic
        ? Math.Abs(ConfigService.Current.AnnotationMosaicSize - value) < 0.1f
        : Math.Abs(ConfigService.Current.AnnotationPenWidth - value) < 0.1f;

    private static void ToggleConfiguredFontStyle(FontStyle flag)
    {
        FontStyle current = IsValidFontStyle(ConfigService.Current.AnnotationFontStyle)
            ? (FontStyle)ConfigService.Current.AnnotationFontStyle
            : FontStyle.Regular;
        ConfigService.Current.AnnotationFontStyle = (int)(current.HasFlag(flag) ? current & ~flag : current | flag);
    }

    private void EnsureInlineFontControls()
    {
        if (_inlineFontFamily is not null && _inlineFontSize is not null) return;

        _inlineFontFamily = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 8.3f)
        };
        string[] families = ["Microsoft YaHei UI", "Segoe UI", "Arial", "Consolas", "SimSun"];
        foreach (string family in families.Distinct()) _inlineFontFamily.Items.Add(family);
        _inlineFontFamily.SelectionChangeCommitted += (_, _) =>
        {
            if (_updatingStyleControls || _inlineFontFamily.SelectedItem is not string family) return;
            ConfigService.Current.AnnotationFontFamily = family;
            ConfigService.Save();
            ApplyInlineTextStyle();
        };

        _inlineFontSize = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.3f)
        };
        _inlineFontSize.Items.AddRange(["12", "14", "16", "18", "20", "24", "28", "32", "40", "48", "64"]);
        _inlineFontSize.SelectionChangeCommitted += (_, _) => ApplyFontSizeFromControl();
        _inlineFontSize.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            ApplyFontSizeFromControl();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        _inlineFontSize.Validated += (_, _) => ApplyFontSizeFromControl();
        Controls.Add(_inlineFontFamily);
        Controls.Add(_inlineFontSize);
    }

    private void UpdateInlineStyleControls()
    {
        bool visible = _showStyleBar && _activeTool == ToolbarAction.Text && _hasSelection;
        if (!visible)
        {
            if (_inlineStyleControlsVisible)
            {
                if (_inlineFontFamily is not null) _inlineFontFamily.Visible = false;
                if (_inlineFontSize is not null) _inlineFontSize.Visible = false;
                _inlineStyleControlsVisible = false;
            }
            return;
        }

        EnsureInlineFontControls();
        Rectangle bounds = GetStyleBarBounds();
        if (_inlineFontFamily is null || _inlineFontSize is null || bounds.IsEmpty) return;

        _updatingStyleControls = true;
        try
        {
            bool firstShow = !_inlineStyleControlsVisible;
            string family = string.IsNullOrWhiteSpace(ConfigService.Current.AnnotationFontFamily)
                ? "Microsoft YaHei UI"
                : ConfigService.Current.AnnotationFontFamily;
            if (!_inlineFontFamily.Items.Contains(family)) _inlineFontFamily.Items.Add(family);
            if (!string.Equals(_inlineFontFamily.SelectedItem as string, family, StringComparison.Ordinal))
                _inlineFontFamily.SelectedItem = family;
            string sizeText = ConfigService.Current.AnnotationFontSize.ToString("0.#");
            if (_inlineFontSize.Text != sizeText) _inlineFontSize.Text = sizeText;

            var familyBounds = new Rectangle(bounds.Left + 10, bounds.Top + 7, 174, 25);
            var sizeBounds = new Rectangle(bounds.Left + 190, bounds.Top + 7, 72, 25);
            if (_inlineFontFamily.Bounds != familyBounds) _inlineFontFamily.Bounds = familyBounds;
            if (_inlineFontSize.Bounds != sizeBounds) _inlineFontSize.Bounds = sizeBounds;
            ThemePalette palette = ThemeManager.Palette;
            Color inputBackground = Color.FromArgb(255, palette.InputBg);
            if (_inlineFontFamily.BackColor != inputBackground) _inlineFontFamily.BackColor = inputBackground;
            if (_inlineFontFamily.ForeColor != palette.TextPrimary) _inlineFontFamily.ForeColor = palette.TextPrimary;
            if (_inlineFontSize.BackColor != inputBackground) _inlineFontSize.BackColor = inputBackground;
            if (_inlineFontSize.ForeColor != palette.TextPrimary) _inlineFontSize.ForeColor = palette.TextPrimary;
            if (!_inlineFontFamily.Visible) _inlineFontFamily.Visible = true;
            if (!_inlineFontSize.Visible) _inlineFontSize.Visible = true;
            _inlineStyleControlsVisible = true;
            if (firstShow)
            {
                _inlineFontFamily.BringToFront();
                _inlineFontSize.BringToFront();
            }
        }
        finally
        {
            _updatingStyleControls = false;
        }
    }

    private void ApplyFontSizeFromControl()
    {
        if (_updatingStyleControls || _inlineFontSize is null) return;
        if (!float.TryParse(_inlineFontSize.Text, out float size)) return;
        ConfigService.Current.AnnotationFontSize = Math.Clamp(size, 8f, 72f);
        ConfigService.Save();
        ApplyInlineTextStyle();
        UpdateInlineStyleControls();
        Invalidate(GetStyleBarBounds());
    }

    private void ApplyInlineTextStyle()
    {
        if (_inlineTextEditor is null) return;
        using Font newFont = CreateAnnotationFont();
        Font previous = _inlineTextEditor.Font;
        _inlineTextEditor.Font = (Font)newFont.Clone();
        _inlineTextEditor.ForeColor = GetAnnotationColor();
        if (!ReferenceEquals(previous, DefaultFont)) previous.Dispose();
        ResizeInlineTextEditor();
        _inlineTextEditor.Focus();
    }

    private static bool IsColorAction(StyleAction action) =>
        action >= StyleAction.ColorRed && action <= StyleAction.ColorBlack;

    private static Color GetStyleColor(StyleAction action) => action switch
    {
        StyleAction.ColorRed => Color.FromArgb(255, 59, 48),
        StyleAction.ColorOrange => Color.FromArgb(255, 149, 0),
        StyleAction.ColorYellow => Color.FromArgb(255, 204, 0),
        StyleAction.ColorGreen => Color.FromArgb(52, 199, 89),
        StyleAction.ColorCyan => Color.FromArgb(50, 215, 225),
        StyleAction.ColorBlue => Color.FromArgb(0, 122, 255),
        StyleAction.ColorPurple => Color.FromArgb(175, 82, 222),
        StyleAction.ColorPink => Color.FromArgb(255, 45, 85),
        StyleAction.ColorWhite => Color.White,
        StyleAction.ColorGray => Color.FromArgb(142, 142, 147),
        StyleAction.ColorBlack => Color.FromArgb(17, 19, 24),
        _ => Color.FromArgb(255, 59, 48)
    };

    private static bool ColorsEqual(Color first, Color second) =>
        first.R == second.R && first.G == second.G && first.B == second.B;

    private void DrawToolbarTooltip(Graphics graphics, Rectangle toolbarBounds, ThemePalette palette)
    {
        ToolbarButton hovered = GetToolbarButtons().FirstOrDefault(button => button.Action == _hoveredAction);
        if (hovered.Action == ToolbarAction.None || string.IsNullOrEmpty(hovered.Tooltip)) return;

        using var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
        Size textSize = TextRenderer.MeasureText(graphics, hovered.Tooltip, font, Size.Empty, TextFormatFlags.NoPadding);
        int width = textSize.Width + 18;
        int height = textSize.Height + 10;
        int x = hovered.Bounds.Left + hovered.Bounds.Width / 2 - width / 2;
        x = Math.Clamp(x, 6, Math.Max(6, ClientSize.Width - width - 6));

        bool toolbarIsBelowSelection = toolbarBounds.Top >= _selection.Bottom;
        int y = toolbarIsBelowSelection
            ? toolbarBounds.Top - height - 7
            : toolbarBounds.Bottom + 7;
        y = Math.Clamp(y, 6, Math.Max(6, ClientSize.Height - height - 6));
        var tooltipBounds = new Rectangle(x, y, width, height);

        using var path = RoundedRectangle(tooltipBounds, 7);
        using var background = new SolidBrush(WithAlpha(palette.CardBg, 252));
        using var outline = new Pen(WithAlpha(palette.CardBorder, 220), 1f);
        graphics.FillPath(background, path);
        graphics.DrawPath(outline, path);
        TextRenderer.DrawText(
            graphics,
            hovered.Tooltip,
            font,
            tooltipBounds,
            palette.TextPrimary,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding);
    }

    private static void DrawSeparator(Graphics graphics, int x, int top, int bottom, Color color)
    {
        using var separator = new Pen(color, 1.9f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(separator, x, top, x, bottom);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Cursor CursorForDragMode(DragMode mode) => mode switch
    {
        DragMode.Move => Cursors.SizeAll,
        DragMode.Left or DragMode.Right => Cursors.SizeWE,
        DragMode.Top or DragMode.Bottom => Cursors.SizeNS,
        DragMode.TopLeft or DragMode.BottomRight => Cursors.SizeNWSE,
        DragMode.TopRight or DragMode.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Cross
    };

    private Point ClampToClient(Point point) => new(
        Math.Clamp(point.X, 0, ClientSize.Width),
        Math.Clamp(point.Y, 0, ClientSize.Height));

    private Point ClampToSelection(Point point) => new(
        Math.Clamp(point.X, _selection.Left, Math.Max(_selection.Left, _selection.Right - 1)),
        Math.Clamp(point.Y, _selection.Top, Math.Max(_selection.Top, _selection.Bottom - 1)));

    private static Rectangle FromPoints(Point first, Point second) => Rectangle.FromLTRB(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Max(first.X, second.X),
        Math.Max(first.Y, second.Y));

    private static bool ChangesLeft(DragMode mode) =>
        mode is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft;

    private static bool ChangesRight(DragMode mode) =>
        mode is DragMode.Right or DragMode.TopRight or DragMode.BottomRight;

    private static bool ChangesTop(DragMode mode) =>
        mode is DragMode.Top or DragMode.TopLeft or DragMode.TopRight;

    private static bool ChangesBottom(DragMode mode) =>
        mode is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight;

    private static bool IsAnnotationTool(ToolbarAction action) =>
        action is ToolbarAction.Pen or ToolbarAction.Arrow or ToolbarAction.Text or ToolbarAction.Mosaic;

    private static bool IsValidFontStyle(int value)
    {
        const int validFlags = (int)(FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout);
        return value >= 0 && (value & ~validFlags) == 0;
    }

    private static Color WithAlpha(Color color, int alpha) =>
        Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

    private void OnThemeChanged()
    {
        if (!IsHandleCreated) return;
        if (_styleValueEditor is { Visible: true } editor)
        {
            editor.BackColor = Color.FromArgb(255, ThemeManager.Palette.InputBg);
            editor.ForeColor = ThemeManager.Palette.TextPrimary;
        }
        UpdateInlineStyleControls();
        Invalidate();
    }

    private readonly record struct ToolbarButton(
        ToolbarAction Action,
        Rectangle Bounds,
        LucideIcon Icon,
        string Tooltip,
        bool IsPrimary);

    private readonly record struct StyleButton(StyleAction Action, Rectangle Bounds, float Value = 0f);

    private abstract class AnnotationItem
    {
    }

    private sealed class StrokeAnnotation(
        List<PointF> points,
        Color color,
        float width,
        bool isMosaic,
        int pixelSize) : AnnotationItem
    {
        public List<PointF> Points { get; } = points;
        public Color Color { get; } = color;
        public float Width { get; } = width;
        public bool IsMosaic { get; } = isMosaic;
        public int PixelSize { get; } = pixelSize;
    }

    private sealed class ArrowAnnotation(
        PointF start,
        PointF end,
        Color color,
        float width,
        AnnotationArrowStyle style) : AnnotationItem
    {
        public PointF Start { get; } = start;
        public PointF End { get; set; } = end;
        public Color Color { get; } = color;
        public float Width { get; } = width;
        public AnnotationArrowStyle Style { get; } = style;
    }

    private sealed class TextAnnotation(
        PointF location,
        string text,
        string fontFamily,
        float fontSize,
        FontStyle fontStyle,
        Color color) : AnnotationItem
    {
        public PointF Location { get; } = location;
        public string Text { get; } = text;
        public string FontFamily { get; } = fontFamily;
        public float FontSize { get; } = fontSize;
        public FontStyle FontStyle { get; } = fontStyle;
        public Color Color { get; } = color;
    }

    private enum DragMode
    {
        None,
        NewSelection,
        Move,
        Left,
        Top,
        Right,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private enum ToolbarAction
    {
        None,
        Pen,
        Arrow,
        Text,
        Mosaic,
        Style,
        Undo,
        Cursor,
        ScrollCapture,
        Ocr,
        Copy,
        Save,
        Pin,
        Reset,
        Cancel,
        Confirm
    }

    private enum StyleAction
    {
        None,
        SetPrimaryPreset,
        SetSecondaryPreset,
        ArrowOpen,
        ArrowFilled,
        ArrowDouble,
        Bold,
        Italic,
        ColorRed,
        ColorOrange,
        ColorYellow,
        ColorGreen,
        ColorCyan,
        ColorBlue,
        ColorPurple,
        ColorPink,
        ColorWhite,
        ColorGray,
        ColorBlack
    }

    private enum StyleSliderKind
    {
        None,
        PenWidth,
        MosaicBrush,
        MosaicPixel
    }
}
