using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Interop;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Forms;

internal sealed class ScrollCaptureForm : Form
{
    private const int ToolbarHeight = 44;
    private const int ToolbarButtonSize = 32;
    private const int ConfirmButtonWidth = 38;
    private const int ToolbarGap = 4;
    private const int ToolbarWidth = 204;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly Color TransparentColor = Color.FromArgb(1, 2, 3);
    private static readonly Font BadgeFont = new("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
    private static readonly Font HintFont = new("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
    private static readonly Font TooltipFont = new("Microsoft YaHei UI", 8.5f, FontStyle.Regular);

    private readonly Rectangle _screenBounds;
    private readonly Rectangle _selection;
    private readonly Bitmap _background;
    private readonly ScrollCaptureAssembler _assembler;
    private readonly nint _ownerOverlayHandle;
    private readonly System.Windows.Forms.Timer _captureTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _autoScrollCancellation;
    private DateTime _hintEndsAt = DateTime.UtcNow.AddMilliseconds(2100);

    private bool _showHint = true;
    private bool _autoCapturing;
    private bool _autoCaptureRunning;
    private bool _deferredDisposeAssembler;
    private bool _captureTickBusy;
    private bool _closing;
    private bool _resourcesDisposed;
    private bool _hasRegionState;
    private (Rectangle Selection, Rectangle Toolbar, Rectangle Badge, Rectangle Hint, Rectangle Tooltip) _lastRegionState;
    private nint _cachedTargetRoot;
    private nint _cachedTargetChild;
    private nint _hoveredTarget;
    private int _manualWheelTicksWithoutAdvance;
    private ScrollModeAction _hoveredAction;
    private ScrollModeAction _pressedAction;
    private string _statusText = "在选区内滚动鼠标，或点击工具栏自动滚动";

    public ScrollCaptureForm(
        Rectangle screenBounds,
        Bitmap background,
        Bitmap initialFrame,
        nint ownerOverlayHandle = 0)
    {
        _screenBounds = screenBounds;
        _ownerOverlayHandle = ownerOverlayHandle;
        _background = (Bitmap)background.Clone();
        _assembler = new ScrollCaptureAssembler(initialFrame);

        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        _selection = new Rectangle(
            screenBounds.X - virtualScreen.X,
            screenBounds.Y - virtualScreen.Y,
            screenBounds.Width,
            screenBounds.Height);

        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = TransparentColor;
        TransparencyKey = TransparentColor;
        Cursor = Cursors.Default;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _captureTimer = new System.Windows.Forms.Timer { Interval = 75 };
        _captureTimer.Tick += CaptureTimerTick;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public event Action<Bitmap, CaptureCompletionAction>? Completed;
    public event Action? Cancelled;

    internal Rectangle SelectionBoundsInClient => _selection;
    internal Rectangle ToolbarBoundsInClient => GetToolbarBounds();
    internal Point SelectionCenter => new(
        _screenBounds.Left + _screenBounds.Width / 2,
        _screenBounds.Top + _screenBounds.Height / 2);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.TryExcludeWindowFromCapture(Handle);
        UpdateWindowHoleRegion();
    }

    public void BeginMode()
    {
        (nint root, nint child) = ResolveTargetWindow();
        _cachedTargetRoot = root;
        _cachedTargetChild = child;

        UpdateWindowHoleRegion();
        Show();
        Invalidate();
        Update();
        ActivateTargetWindow();
        _captureTimer.Start();
    }

    public void CancelFromOwner()
    {
        if (_closing) return;
        _closing = true;
        _autoScrollCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _captureTimer.Stop();
        Close();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = (nint)NativeMethods.MA_NOACTIVATE;
            return;
        }

        if (m.Msg == NativeMethods.WM_NCHITTEST && IsPointInTransparentHole(m.LParam))
        {
            m.Result = (nint)NativeMethods.HTTRANSPARENT;
            return;
        }

        if (m.Msg == NativeMethods.WM_MOUSEWHEEL && !_autoCapturing && !_closing)
        {
            if (ForwardWheelToTarget(m.WParam))
            {
                m.Result = nint.Zero;
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // Paint dimmed freeze and border exclusively outside the hole so AA pixels
        // never pollute TransparentColor along _selection's inner edge.
        GraphicsState state = graphics.Save();
        graphics.SetClip(_selection, CombineMode.Exclude);
        graphics.DrawImage(_background, 0, 0, ClientSize.Width, ClientSize.Height);
        using (var dim = new SolidBrush(Color.FromArgb(124, 8, 10, 14)))
        {
            graphics.FillRectangle(dim, ClientRectangle);
        }

        DrawSelectionBorder(graphics);
        graphics.Restore(state);

        // This exact color becomes a live transparent hole into the target app.
        GraphicsState holeState = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var transparent = new SolidBrush(TransparentColor))
        {
            graphics.FillRectangle(transparent, _selection);
        }
        graphics.Restore(holeState);

        DrawDimensionBadge(graphics);
        DrawToolbar(graphics);
        DrawToolbarTooltip(graphics);
        if (_showHint) DrawHint(graphics);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ScrollModeAction hovered = HitTestToolbar(e.Location);
        if (hovered != _hoveredAction)
        {
            Rectangle previousTooltip = GetTooltipBoundsFor(_hoveredAction);
            Rectangle previousHint = _showHint ? GetHintBounds() : Rectangle.Empty;
            _hoveredAction = hovered;
            Cursor = hovered == ScrollModeAction.None ? Cursors.Default : Cursors.Hand;
            UpdateWindowHoleRegion();
            Invalidate(GetToolbarBounds());
            if (!previousTooltip.IsEmpty) Invalidate(Rectangle.Inflate(previousTooltip, 2, 2));
            if (!previousHint.IsEmpty) Invalidate(Rectangle.Inflate(previousHint, 2, 2));
            Rectangle nextTooltip = GetTooltipBoundsFor(hovered);
            if (!nextTooltip.IsEmpty) Invalidate(Rectangle.Inflate(nextTooltip, 2, 2));
            Rectangle nextHint = _showHint ? GetHintBounds() : Rectangle.Empty;
            if (!nextHint.IsEmpty) Invalidate(Rectangle.Inflate(nextHint, 2, 2));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressedAction = HitTestToolbar(e.Location);
        if (_pressedAction != ScrollModeAction.None) Invalidate(GetToolbarBounds());
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        ScrollModeAction pressed = _pressedAction;
        _pressedAction = ScrollModeAction.None;
        if (pressed != ScrollModeAction.None && pressed == HitTestToolbar(e.Location))
        {
            ExecuteAction(pressed);
        }
        else
        {
            Invalidate(GetToolbarBounds());
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            if (_autoCapturing)
            {
                _escapeCooldownUntil = DateTime.UtcNow.AddMilliseconds(350);
                _autoScrollCancellation?.Cancel();
            }
            else
            {
                CancelCapture();
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_autoCapturing) return;

        if (e.KeyCode == Keys.Enter)
        {
            CompleteCapture(CaptureCompletionAction.ScrollCapture);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Space)
        {
            StartAutoScroll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.S)
        {
            CompleteCapture(CaptureCompletionAction.Save);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
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

    private DateTime _escapeCooldownUntil = DateTime.MinValue;

    private void DisposeManagedResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        _captureTimer.Stop();
        _captureTimer.Dispose();
        _autoScrollCancellation?.Cancel();
        _autoScrollCancellation?.Dispose();
        _autoScrollCancellation = null;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        if (_autoCaptureRunning)
        {
            _deferredDisposeAssembler = true;
        }
        else
        {
            _assembler.Dispose();
        }
        Region? region = Region;
        Region = null;
        region?.Dispose();
        _background.Dispose();
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void CaptureTimerTick(object? sender, EventArgs e)
    {
        if (_closing || _autoCapturing || _captureTickBusy) return;
        if (DateTime.UtcNow >= _escapeCooldownUntil && EscapePressed())
        {
            CancelCapture();
            return;
        }

        if (_showHint && DateTime.UtcNow >= _hintEndsAt)
        {
            Rectangle oldHint = GetHintBounds();
            _showHint = false;
            UpdateWindowHoleRegion();
            if (!oldHint.IsEmpty) Invalidate(Rectangle.Inflate(oldHint, 2, 2));
        }

        _captureTickBusy = true;
        try
        {
            using Bitmap frame = CaptureService.CaptureScreen(_screenBounds);
            ScrollCaptureFrameStatus status = _assembler.AddFrame(frame);
            switch (status)
            {
                case ScrollCaptureFrameStatus.Added:
                    _manualWheelTicksWithoutAdvance = 0;
                    _statusText = $"已拼接 {_assembler.FrameCount} 屏 · {_assembler.CapturedHeight:N0} px";
                    UpdateWindowHoleRegion();
                    Invalidate(GetToolbarBounds());
                    Invalidate(Rectangle.Inflate(GetDimensionBadgeBounds(), 2, 2));
                    break;
                case ScrollCaptureFrameStatus.Pending:
                    break;
                case ScrollCaptureFrameStatus.ScrolledUp:
                    _statusText = "请向下滚动页面，暂不支持向上回滚";
                    ShowTransientHint();
                    break;
                case ScrollCaptureFrameStatus.NoOverlap:
                    _statusText = "滚动太快，未找到重叠区域，请慢一点";
                    ShowTransientHint();
                    break;
                case ScrollCaptureFrameStatus.SizeLimit:
                    _statusText = "已达到长截图尺寸上限，请点击完成";
                    _captureTimer.Stop();
                    ShowTransientHint();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException("ScrollCaptureForm.CaptureTimer", ex);
            _statusText = "捕获失败：" + ex.Message;
            _captureTimer.Stop();
            ShowTransientHint();
            Invalidate(GetToolbarBounds());
        }
        finally
        {
            _captureTickBusy = false;
        }
    }

    private void ExecuteAction(ScrollModeAction action)
    {
        switch (action)
        {
            case ScrollModeAction.AutoScroll:
                if (_autoCapturing)
                {
                    _escapeCooldownUntil = DateTime.UtcNow.AddMilliseconds(350);
                    _autoScrollCancellation?.Cancel();
                }
                else
                {
                    StartAutoScroll();
                }
                break;
            case ScrollModeAction.Save:
                CompleteCapture(CaptureCompletionAction.Save);
                break;
            case ScrollModeAction.Cancel:
                CancelCapture();
                break;
            case ScrollModeAction.Confirm:
                CompleteCapture(CaptureCompletionAction.ScrollCapture);
                break;
        }
    }

    private async void StartAutoScroll()
    {
        if (_autoCapturing || _closing) return;
        _showHint = false;
        _autoCapturing = true;
        _autoCaptureRunning = true;
        _captureTimer.Stop();
        ActivateTargetWindow();
        _statusText = "正在自动滚动 · 按 Esc 停止";
        UpdateWindowHoleRegion();
        Invalidate();
        Update();

        _autoScrollCancellation?.Dispose();
        _autoScrollCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);

        ScrollCaptureResult result;
        try
        {
            result = await ScrollCaptureService.ContinueCaptureAsync(
                _screenBounds,
                _assembler,
                _autoScrollCancellation.Token,
                (_, height) =>
                {
                    if (_closing || IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(() =>
                        {
                            if (_closing || IsDisposed) return;
                            _statusText = $"正在自动滚动 · 已捕获 {height:N0} px";
                            UpdateWindowHoleRegion();
                            Invalidate(GetToolbarBounds());
                            Invalidate(Rectangle.Inflate(GetDimensionBadgeBounds(), 2, 2));
                        });
                    }
                    catch { }
                },
                preparedScroller: null,
                BuildIgnoredWindowHandles());
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("ScrollCaptureForm.StartAutoScroll", exception);
            if (_closing || IsDisposed) return;
            _autoCapturing = false;
            _statusText = "自动滚动失败：" + exception.Message;
            ShowTransientHint();
            _captureTimer.Start();
            Invalidate();
            return;
        }
        finally
        {
            _autoCaptureRunning = false;
            if (_deferredDisposeAssembler)
            {
                _deferredDisposeAssembler = false;
                _assembler.Dispose();
            }
        }

        if (_closing || IsDisposed) return;
        if (result.Reason == ScrollCaptureStopReason.Cancelled && _lifetimeCancellation.IsCancellationRequested)
        {
            CancelCapture();
            return;
        }

        _autoCapturing = false;
        _escapeCooldownUntil = DateTime.UtcNow.AddMilliseconds(350);
        _statusText = result.Reason switch
        {
            ScrollCaptureStopReason.Completed => "已滚动到底，点击 ✓ 完成长截图",
            ScrollCaptureStopReason.SizeLimit => "已达到尺寸上限，点击 ✓ 完成长截图",
            ScrollCaptureStopReason.Cancelled => _assembler.FrameCount > 1
                ? "已停止自动滚动，点击 ✓ 完成长截图"
                : "已停止自动滚动，可在选区内手动滚动",
            _ => result.ErrorMessage ?? "没有检测到可滚动内容，可尝试手动滚动"
        };
        ShowTransientHint();
        _captureTimer.Start();
        Invalidate();
    }

    private void CompleteCapture(CaptureCompletionAction action)
    {
        if (_closing || _autoCapturing) return;
        if (_assembler.FrameCount <= 1)
        {
            _statusText = "请先在选区内缓慢滚动，或点击自动滚动";
            ShowTransientHint();
            return;
        }

        _closing = true;
        _captureTimer.Stop();
        Bitmap image = _assembler.BuildImage();
        Close();
        Action<Bitmap, CaptureCompletionAction>? handlers = Completed;
        if (handlers is null)
        {
            image.Dispose();
            return;
        }

        try
        {
            handlers(image, action);
        }
        catch (Exception exception)
        {
            image.Dispose();
            AppDiagnostics.LogException("ScrollCaptureForm.Completed", exception);
        }
    }

    private void CancelCapture()
    {
        if (_closing) return;
        _closing = true;
        _autoScrollCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _captureTimer.Stop();
        Close();
        try
        {
            Cancelled?.Invoke();
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("ScrollCaptureForm.Cancelled", exception);
        }
    }

    private void UpdateWindowHoleRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        Rectangle toolbar = GetToolbarBounds();
        Rectangle badge = GetDimensionBadgeBounds();
        Rectangle hint = _showHint ? GetHintBounds() : Rectangle.Empty;
        Rectangle tooltip = GetTooltipBoundsFor(_hoveredAction, toolbar);
        var state = (_selection, toolbar, badge, hint, tooltip);
        if (_hasRegionState && state == _lastRegionState) return;
        _hasRegionState = true;
        _lastRegionState = state;

        // Physically punch _selection out of the layered HWND via SetWindowRgn so Win32,
        // DWM, Chromium, Electron, and precision touchpads route wheel input straight to the target app.
        var nextRegion = new Region(new Rectangle(Point.Empty, ClientSize));
        nextRegion.Exclude(_selection);
        if (!toolbar.IsEmpty) nextRegion.Union(Rectangle.Inflate(toolbar, 2, 3));
        if (!badge.IsEmpty) nextRegion.Union(Rectangle.Inflate(badge, 2, 2));
        if (!hint.IsEmpty) nextRegion.Union(Rectangle.Inflate(hint, 2, 2));
        if (!tooltip.IsEmpty) nextRegion.Union(Rectangle.Inflate(tooltip, 2, 2));

        Region? previous = Region;
        Region = nextRegion;
        previous?.Dispose();
    }

    private void DrawSelectionBorder(Graphics graphics)
    {
        Color accentColor = ThemeManager.Palette.AccentColor;
        Rectangle border = new(_selection.X - 2, _selection.Y - 2, _selection.Width + 3, _selection.Height + 3);
        using var contrast = new Pen(Color.FromArgb(140, 0, 0, 0), 4f);
        using var accent = new Pen(accentColor, 2f);
        graphics.DrawRectangle(contrast, border);
        graphics.DrawRectangle(accent, border);
    }

    private string GetDimensionBadgeText() => _autoCapturing
        ? $"{_selection.Width} × {_assembler.CapturedHeight:N0} · 自动滚动 ({_assembler.FrameCount} 屏)"
        : _assembler.FrameCount <= 1
            ? $"{_selection.Width} × {_selection.Height}"
            : $"{_selection.Width} × {_assembler.CapturedHeight:N0} · {_assembler.FrameCount} 屏";

    private void DrawDimensionBadge(Graphics graphics)
    {
        Rectangle bounds = GetDimensionBadgeBounds();
        if (bounds.IsEmpty) return;

        ThemePalette palette = ThemeManager.Palette;
        var shadowBounds = new Rectangle(bounds.X, bounds.Y + 1, bounds.Width, bounds.Height);
        using (GraphicsPath shadowPath = GraphicsHelper.GetRoundedRectangle(shadowBounds, 6))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
        {
            graphics.FillPath(shadowBrush, shadowPath);
        }

        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 6);
        using var fill = new SolidBrush(Color.FromArgb(246, palette.CardBg));
        using var rim = new Pen(Color.FromArgb(200, palette.CardBorder), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(rim, path);

        TextRenderer.DrawText(
            graphics,
            GetDimensionBadgeText(),
            BadgeFont,
            bounds,
            palette.TextPrimary,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding);
    }

    private void DrawHint(Graphics graphics)
    {
        Rectangle bounds = GetHintBounds();
        if (bounds.IsEmpty) return;

        ThemePalette palette = ThemeManager.Palette;
        var shadowBounds = new Rectangle(bounds.X, bounds.Y + 2, bounds.Width, bounds.Height);
        using (GraphicsPath shadowPath = GraphicsHelper.GetRoundedRectangle(shadowBounds, 10))
        using (var shadow = new SolidBrush(Color.FromArgb(32, 0, 0, 0)))
        {
            graphics.FillPath(shadow, shadowPath);
        }

        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 10);
        using var fill = new SolidBrush(Color.FromArgb(238, palette.CardBg));
        using var rim = new Pen(Color.FromArgb(200, palette.CardBorder), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(rim, path);
        TextRenderer.DrawText(
            graphics,
            _statusText,
            HintFont,
            bounds,
            palette.TextPrimary,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);
    }

    private void DrawToolbar(Graphics graphics)
    {
        Rectangle bounds = GetToolbarBounds();
        if (bounds.IsEmpty) return;

        ThemePalette palette = ThemeManager.Palette;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var shadowBounds = new Rectangle(bounds.X, bounds.Y + 2, bounds.Width, bounds.Height);
        using (GraphicsPath shadowPath = GraphicsHelper.GetRoundedRectangle(shadowBounds, 16))
        using (var shadow = new SolidBrush(Color.FromArgb(24, 0, 0, 0)))
        {
            graphics.FillPath(shadow, shadowPath);
        }

        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 16);
        using var fill = new SolidBrush(palette.CardBg);
        using var outline = new Pen(Color.FromArgb(205, palette.CardBorder), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(outline, path);

        LogoRenderer.DrawLogo(graphics, bounds.Left + 11, bounds.Top + 12, 20, palette.TextPrimary);
        using (var separator = new Pen(Color.FromArgb(80, palette.TextSecondary), 1.9f)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawLine(separator, bounds.Left + 37, bounds.Top + 16, bounds.Left + 37, bounds.Bottom - 16);
        }

        foreach (ScrollModeButton button in GetToolbarButtons())
        {
            bool active = button.Action == ScrollModeAction.AutoScroll && _autoCapturing;
            bool hovered = button.Action == _hoveredAction;
            bool pressed = button.Action == _pressedAction && hovered;

            Color buttonFill = Color.Transparent;
            Color iconColor = palette.TextSecondary;
            if (button.IsPrimary)
            {
                buttonFill = hovered ? palette.PrimaryContainer : palette.AccentColor;
                iconColor = hovered ? palette.OnPrimaryContainer : palette.AccentForeground;
            }
            else if (active)
            {
                buttonFill = palette.PrimaryContainer;
                iconColor = palette.OnPrimaryContainer;
            }
            else if (hovered)
            {
                buttonFill = Color.FromArgb(pressed ? 34 : 22, palette.TextPrimary);
                iconColor = palette.TextPrimary;
            }

            if (buttonFill.A > 0)
            {
                using GraphicsPath buttonPath = GraphicsHelper.GetRoundedRectangle(button.Bounds, 16);
                using var buttonBrush = new SolidBrush(buttonFill);
                graphics.FillPath(buttonBrush, buttonPath);
            }

            if (active && hovered)
            {
                using GraphicsPath statePath = GraphicsHelper.GetRoundedRectangle(button.Bounds, 16);
                using var stateBrush = new SolidBrush(Color.FromArgb(pressed ? 24 : 14, palette.OnPrimaryContainer));
                graphics.FillPath(stateBrush, statePath);
            }

            if (active)
            {
                float stopSize = 10.5f;
                RectangleF stopRect = new(
                    button.Bounds.Left + (button.Bounds.Width - stopSize) / 2f,
                    button.Bounds.Top + (button.Bounds.Height - stopSize) / 2f,
                    stopSize,
                    stopSize);
                using GraphicsPath stopPath = GraphicsHelper.GetRoundedRectangle(stopRect, 2.2f);
                using var stopPen = new Pen(iconColor, 1.9f);
                graphics.DrawPath(stopPen, stopPath);
                continue;
            }

            float iconSize = button.IsPrimary ? 18f : 17f;
            float iconX = button.Bounds.Left + (button.Bounds.Width - iconSize) / 2f;
            float iconY = button.Bounds.Top + (button.Bounds.Height - iconSize) / 2f;
            LucideRenderer.Draw(graphics, button.Icon, iconX, iconY, iconSize, iconColor, 1.9f);
        }
    }

    private void DrawToolbarTooltip(Graphics graphics)
    {
        if (_hoveredAction == ScrollModeAction.None) return;
        ScrollModeButton hovered = GetToolbarButtons().FirstOrDefault(button => button.Action == _hoveredAction);
        if (hovered.Action == ScrollModeAction.None || string.IsNullOrEmpty(hovered.Tooltip)) return;

        Rectangle tooltipBounds = GetTooltipBoundsFor(_hoveredAction);
        if (tooltipBounds.IsEmpty) return;

        ThemePalette palette = ThemeManager.Palette;
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(tooltipBounds, 7);
        using var background = new SolidBrush(Color.FromArgb(252, palette.CardBg));
        using var outline = new Pen(Color.FromArgb(220, palette.CardBorder), 1f);
        graphics.FillPath(background, path);
        graphics.DrawPath(outline, path);
        TextRenderer.DrawText(
            graphics,
            hovered.Tooltip,
            TooltipFont,
            tooltipBounds,
            palette.TextPrimary,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding);
    }

    private Rectangle GetTooltipBoundsFor(ScrollModeAction action, Rectangle? toolbarOverride = null)
    {
        if (action == ScrollModeAction.None) return Rectangle.Empty;
        Rectangle toolbarBounds = toolbarOverride ?? GetToolbarBounds();
        if (toolbarBounds.IsEmpty) return Rectangle.Empty;

        ScrollModeButton hovered = GetToolbarButtons(toolbarBounds).FirstOrDefault(button => button.Action == action);
        if (hovered.Action == ScrollModeAction.None || string.IsNullOrEmpty(hovered.Tooltip)) return Rectangle.Empty;

        Size textSize = TextRenderer.MeasureText(hovered.Tooltip, TooltipFont, Size.Empty, TextFormatFlags.NoPadding);
        int width = textSize.Width + 18;
        int height = textSize.Height + 10;
        int x = hovered.Bounds.Left + hovered.Bounds.Width / 2 - width / 2;
        x = Math.Clamp(x, 6, Math.Max(6, ClientSize.Width - width - 6));

        bool toolbarIsBelowSelection = toolbarBounds.Top >= _selection.Bottom;
        int preferredY = toolbarIsBelowSelection
            ? toolbarBounds.Bottom + 7
            : toolbarBounds.Top - height - 7;
        int alternateY = toolbarIsBelowSelection
            ? _selection.Top - height - 7
            : _selection.Bottom + 7;

        // Prefer placing tooltips outside _selection so they never obstruct the live hole or pollute CopyFromScreen.
        int y;
        Rectangle preferred = new(x, preferredY, width, height);
        if (preferred.Top >= 6 && preferred.Bottom <= ClientSize.Height - 6 && !preferred.IntersectsWith(_selection))
        {
            y = preferredY;
        }
        else
        {
            Rectangle alternate = new(x, alternateY, width, height);
            if (alternate.Top >= 6 && alternate.Bottom <= ClientSize.Height - 6 && !alternate.IntersectsWith(_selection))
            {
                y = alternateY;
            }
            else
            {
                int fallbackY = toolbarIsBelowSelection ? toolbarBounds.Top - height - 7 : toolbarBounds.Bottom + 7;
                y = Math.Clamp(fallbackY, 6, Math.Max(6, ClientSize.Height - height - 6));
            }
        }
        return new Rectangle(x, y, width, height);
    }

    private Rectangle GetToolbarBounds()
    {
        int width = Math.Min(ToolbarWidth, Math.Max(1, ClientSize.Width - 16));
        int x = _selection.Left + (_selection.Width - width) / 2;
        x = Math.Clamp(x, 8, Math.Max(8, ClientSize.Width - width - 8));
        int below = _selection.Bottom + 10;
        int y = below + ToolbarHeight <= ClientSize.Height - 8
            ? below
            : _selection.Top - ToolbarHeight - 10;
        y = Math.Clamp(y, 8, Math.Max(8, ClientSize.Height - ToolbarHeight - 8));
        return new Rectangle(x, y, width, ToolbarHeight);
    }

    private ScrollModeButton[] GetToolbarButtons() => GetToolbarButtons(GetToolbarBounds());

    private ScrollModeButton[] GetToolbarButtons(Rectangle toolbar)
    {
        if (toolbar.IsEmpty) return [];
        int y = toolbar.Top + (ToolbarHeight - ToolbarButtonSize) / 2;
        int x = toolbar.Left + 46;
        return
        [
            new ScrollModeButton(
                ScrollModeAction.AutoScroll,
                new Rectangle(x, y, ToolbarButtonSize, ToolbarButtonSize),
                LucideIcon.ChevronsDown,
                _autoCapturing ? "停止自动滚动  (Esc)" : "自动滚动  (Space)"),
            new ScrollModeButton(
                ScrollModeAction.Save,
                new Rectangle(x + ToolbarButtonSize + ToolbarGap, y, ToolbarButtonSize, ToolbarButtonSize),
                LucideIcon.Folder,
                "保存到图片目录  (Ctrl+S)"),
            new ScrollModeButton(
                ScrollModeAction.Cancel,
                new Rectangle(x + (ToolbarButtonSize + ToolbarGap) * 2, y, ToolbarButtonSize, ToolbarButtonSize),
                LucideIcon.X,
                "取消  (Esc)"),
            new ScrollModeButton(
                ScrollModeAction.Confirm,
                new Rectangle(x + (ToolbarButtonSize + ToolbarGap) * 3, y, ConfirmButtonWidth, ToolbarButtonSize),
                LucideIcon.Check,
                "完成长截图  (Enter)",
                IsPrimary: true)
        ];
    }

    private ScrollModeAction HitTestToolbar(Point point)
    {
        foreach (ScrollModeButton button in GetToolbarButtons())
        {
            if (button.Bounds.Contains(point)) return button.Action;
        }
        return ScrollModeAction.None;
    }

    private Rectangle GetDimensionBadgeBounds()
    {
        Size size = TextRenderer.MeasureText(GetDimensionBadgeText(), BadgeFont, Size.Empty, TextFormatFlags.NoPadding);
        int width = size.Width + 16;
        int height = size.Height + 6;
        int x = Math.Clamp(_selection.Left, 6, Math.Max(6, ClientSize.Width - width - 6));
        int y = _selection.Top - height - 8;
        if (y < 6)
        {
            y = _selection.Bottom + 8;
            if (y + height > ClientSize.Height - 6) return Rectangle.Empty;
        }
        return new Rectangle(x, y, width, height);
    }

    private Rectangle GetHintBounds()
    {
        if (_hoveredAction != ScrollModeAction.None) return Rectangle.Empty;

        Size measured = TextRenderer.MeasureText(_statusText, HintFont, Size.Empty, TextFormatFlags.NoPadding);
        int maxAllowedWidth = Math.Max(160, ClientSize.Width - 24);
        int width = Math.Clamp(measured.Width + 28, Math.Min(220, maxAllowedWidth), Math.Min(420, maxAllowedWidth));
        const int height = 34;
        int x = Math.Clamp(_selection.Left + (_selection.Width - width) / 2, 8, Math.Max(8, ClientSize.Width - width - 8));
        const int gap = 8;
        Rectangle toolbar = GetToolbarBounds();

        // Keep transient status outside the live capture hole. Besides being less
        // intrusive, this means the capture timer no longer has to hide/show it.
        Rectangle[] candidates = toolbar.Top >= _selection.Bottom
            ?
            [
                new Rectangle(x, toolbar.Bottom + gap, width, height),
                new Rectangle(x, _selection.Top - height - gap, width, height)
            ]
            :
            [
                new Rectangle(x, toolbar.Top - height - gap, width, height),
                new Rectangle(x, _selection.Bottom + gap, width, height)
            ];

        foreach (Rectangle candidate in candidates)
        {
            if (candidate.Left < 8 || candidate.Right > ClientSize.Width - 8 ||
                candidate.Top < 8 || candidate.Bottom > ClientSize.Height - 8 ||
                candidate.IntersectsWith(_selection) || candidate.IntersectsWith(toolbar))
            {
                continue;
            }

            return candidate;
        }

        return Rectangle.Empty;
    }

    private static bool EscapePressed() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_ESCAPE) & 0x8000) != 0;

    private void ShowTransientHint()
    {
        Rectangle previousHint = _showHint ? GetHintBounds() : Rectangle.Empty;
        _showHint = true;
        _hintEndsAt = DateTime.UtcNow.AddMilliseconds(1850);
        UpdateWindowHoleRegion();
        if (!previousHint.IsEmpty) Invalidate(Rectangle.Inflate(previousHint, 2, 2));
        Rectangle nextHint = GetHintBounds();
        if (!nextHint.IsEmpty) Invalidate(Rectangle.Inflate(nextHint, 2, 2));
        Invalidate(GetToolbarBounds());
    }

    internal HashSet<nint> BuildIgnoredWindowHandles()
    {
        var ignored = new HashSet<nint>();
        if (IsHandleCreated && Handle != nint.Zero)
        {
            ignored.Add(Handle);
        }

        if (_ownerOverlayHandle != nint.Zero)
        {
            ignored.Add(_ownerOverlayHandle);
        }

        foreach (Form openForm in Application.OpenForms)
        {
            if (openForm.IsHandleCreated && openForm.Handle != nint.Zero)
            {
                ignored.Add(openForm.Handle);
            }
        }

        return ignored;
    }

    internal (nint RootWindow, nint TargetChildWindow) ResolveTargetWindow() =>
        NativeMethods.FindTargetWindowAtPoint(SelectionCenter, BuildIgnoredWindowHandles());

    private bool IsPointInTransparentHole(nint lParam)
    {
        int x = unchecked((short)((long)lParam & 0xFFFF));
        int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        Point client = PointToClient(new Point(x, y));
        if (!_selection.Contains(client))
        {
            return false;
        }

        if (GetToolbarBounds().Contains(client) ||
            GetDimensionBadgeBounds().Contains(client) ||
            (_showHint && GetHintBounds().Contains(client)))
        {
            return false;
        }

        return true;
    }

    private (nint Root, nint Child) GetOrRefreshTargetWindow()
    {
        if (_cachedTargetRoot != nint.Zero &&
            NativeMethods.IsWindowVisible(_cachedTargetRoot) &&
            !NativeMethods.IsIconic(_cachedTargetRoot))
        {
            return (_cachedTargetRoot, _cachedTargetChild);
        }

        (nint root, nint child) = ResolveTargetWindow();
        _cachedTargetRoot = root;
        _cachedTargetChild = child;
        return (root, child);
    }

    private void ActivateTargetWindow()
    {
        (nint root, nint child) = GetOrRefreshTargetWindow();
        if (root != nint.Zero && root != Handle && NativeMethods.GetForegroundWindow() != root)
        {
            NativeMethods.SetForegroundWindow(root);
        }

        nint target = child != nint.Zero && child != Handle ? child : root;
        if (target != nint.Zero && _hoveredTarget != target)
        {
            NativeMethods.WarmMouseHover(target, SelectionCenter);
            _hoveredTarget = target;
        }
    }

    private bool ForwardWheelToTarget(nint wParam)
    {
        Point cursorScreen = Cursor.Position;
        Point scrollPoint = _screenBounds.Contains(cursorScreen) ? cursorScreen : SelectionCenter;
        (nint root, nint child) = scrollPoint == SelectionCenter
            ? GetOrRefreshTargetWindow()
            : NativeMethods.FindTargetWindowAtPoint(scrollPoint, BuildIgnoredWindowHandles());
        if (root == nint.Zero || root == Handle)
        {
            (root, child) = GetOrRefreshTargetWindow();
            if (root == nint.Zero || root == Handle) return false;
            scrollPoint = SelectionCenter;
        }

        if (NativeMethods.GetForegroundWindow() != root)
        {
            NativeMethods.SetForegroundWindow(root);
        }

        nint target = child != nint.Zero && child != Handle ? child : root;
        bool warm = _hoveredTarget != target;
        _hoveredTarget = target;
        bool posted = NativeMethods.PostMouseWheelAtScreenPoint(target, scrollPoint, wParam, warmHover: warm);

        // Some non-Chromium frameworks ignore child WM_MOUSEWHEEL and expect it on their top-level host.
        if (_manualWheelTicksWithoutAdvance >= 1 && target != root)
        {
            var cls = new System.Text.StringBuilder(64);
            NativeMethods.GetClassName(target, cls, cls.Capacity);
            if (!cls.ToString().Equals("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal))
            {
                NativeMethods.PostMouseWheelAtScreenPoint(root, scrollPoint, wParam, warmHover: false);
            }
        }

        _manualWheelTicksWithoutAdvance++;
        if (_manualWheelTicksWithoutAdvance >= 4 && _assembler.FrameCount <= 1)
        {
            _statusText = "请将鼠标移至选区框内滚动，或点击自动滚动";
            ShowTransientHint();
        }

        return posted;
    }

    private void OnThemeChanged()
    {
        if (!IsDisposed) Invalidate();
    }

    private readonly record struct ScrollModeButton(
        ScrollModeAction Action,
        Rectangle Bounds,
        LucideIcon Icon,
        string Tooltip,
        bool IsPrimary = false);

    private enum ScrollModeAction
    {
        None,
        AutoScroll,
        Save,
        Cancel,
        Confirm
    }
}
