using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Interop;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Forms;

internal sealed class ScrollCaptureForm : Form
{
    private const int ToolbarHeight = 44;
    private const int ButtonHeight = 32;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly Color TransparentColor = Color.FromArgb(1, 2, 3);

    private readonly Rectangle _screenBounds;
    private readonly Rectangle _selection;
    private readonly Bitmap _background;
    private readonly ScrollCaptureAssembler _assembler;
    private readonly ScrollCaptureService.Scroller? _scroller;
    private readonly System.Windows.Forms.Timer _captureTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private DateTime _hintEndsAt = DateTime.UtcNow.AddMilliseconds(1450);

    private bool _showHint = true;
    private bool _autoCapturing;
    private bool _captureTickBusy;
    private bool _closing;
    private bool _resourcesDisposed;
    private ScrollModeAction _hoveredAction;
    private ScrollModeAction _pressedAction;
    private string _statusText = "请缓慢滚动页面，或点击自动滚动";

    public ScrollCaptureForm(Rectangle screenBounds, Bitmap background, Bitmap initialFrame)
    {
        _screenBounds = screenBounds;
        _background = (Bitmap)background.Clone();
        _assembler = new ScrollCaptureAssembler(initialFrame);
        _scroller = ScrollCaptureService.CreateScroller(new Point(
            screenBounds.Left + screenBounds.Width / 2,
            screenBounds.Top + screenBounds.Height / 2));

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

        _captureTimer = new System.Windows.Forms.Timer { Interval = 260 };
        _captureTimer.Tick += CaptureTimerTick;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public event Action<Bitmap, CaptureCompletionAction>? Completed;
    public event Action? Cancelled;

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

    public void BeginMode()
    {
        Show();
        _captureTimer.Start();
        Invalidate();
    }

    public void CancelFromOwner()
    {
        if (_closing) return;
        _closing = true;
        _lifetimeCancellation.Cancel();
        _captureTimer.Stop();
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawImageUnscaled(_background, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(118, 0, 0, 0)))
        {
            graphics.FillRectangle(dim, ClientRectangle);
        }

        // This exact color becomes a live transparent hole into the target app.
        using (var transparent = new SolidBrush(TransparentColor))
        {
            graphics.FillRectangle(transparent, _selection);
        }

        DrawSelectionBorder(graphics);
        DrawDimensionBadge(graphics);
        DrawToolbar(graphics);
        if (_showHint) DrawHint(graphics);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ScrollModeAction hovered = HitTestToolbar(e.Location);
        if (hovered != _hoveredAction)
        {
            _hoveredAction = hovered;
            Cursor = hovered == ScrollModeAction.None ? Cursors.Default : Cursors.Hand;
            Invalidate(GetToolbarBounds());
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
        if (e.KeyCode != Keys.Escape) return;
        CancelCapture();
        e.Handled = true;
        e.SuppressKeyPress = true;
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
        _captureTimer.Stop();
        _captureTimer.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _assembler.Dispose();
        _scroller?.Dispose();
        _background.Dispose();
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void CaptureTimerTick(object? sender, EventArgs e)
    {
        if (_closing || _autoCapturing || _captureTickBusy) return;
        if (EscapePressed())
        {
            CancelCapture();
            return;
        }

        if (_showHint && DateTime.UtcNow >= _hintEndsAt)
        {
            _showHint = false;
            Invalidate(GetHintBounds());
        }

        _captureTickBusy = true;
        try
        {
            using Bitmap frame = CaptureService.CaptureScreen(_screenBounds);
            ScrollCaptureFrameStatus status = _assembler.AddFrame(frame);
            switch (status)
            {
                case ScrollCaptureFrameStatus.Added:
                    _statusText = $"已拼接 {_assembler.FrameCount} 屏 · {_assembler.CapturedHeight:N0} px";
                    Invalidate(GetToolbarBounds());
                    Invalidate(GetDimensionBadgeBounds());
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
            case ScrollModeAction.Mode:
                _statusText = "当前仅支持竖向长截图";
                ShowTransientHint();
                break;
            case ScrollModeAction.AutoScroll:
                StartAutoScroll();
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
        _captureTimer.Stop();
        _statusText = "正在自动滚动 · 按 Esc 停止";
        Invalidate();
        Update();

        ScrollCaptureResult result;
        try
        {
            result = await ScrollCaptureService.ContinueCaptureAsync(
                _screenBounds,
                _assembler,
                _lifetimeCancellation.Token,
                (_, height) =>
                {
                    if (_closing || IsDisposed) return;
                    _statusText = $"正在自动滚动 · 已捕获 {height:N0} px";
                    Invalidate(GetToolbarBounds());
                    Invalidate(GetDimensionBadgeBounds());
                    Update();
                },
                _scroller);
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

        if (_closing || IsDisposed) return;
        if (result.Reason == ScrollCaptureStopReason.Cancelled)
        {
            CancelCapture();
            return;
        }

        _autoCapturing = false;
        _statusText = result.Reason switch
        {
            ScrollCaptureStopReason.Completed => "已滚动到底，点击 ✓ 完成长截图",
            ScrollCaptureStopReason.SizeLimit => "已达到尺寸上限，点击 ✓ 完成长截图",
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

    private void DrawSelectionBorder(Graphics graphics)
    {
        Rectangle border = Rectangle.Inflate(_selection, 2, 2);
        using var contrast = new Pen(Color.FromArgb(150, 0, 0, 0), 4f);
        using var accent = new Pen(Color.FromArgb(0, 174, 255), 2f);
        graphics.DrawRectangle(contrast, border);
        graphics.DrawRectangle(accent, border);
    }

    private void DrawDimensionBadge(Graphics graphics)
    {
        Rectangle bounds = GetDimensionBadgeBounds();
        if (bounds.IsEmpty) return;
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 8);
        using var fill = new SolidBrush(Color.FromArgb(238, 37, 39, 44));
        graphics.FillPath(fill, path);
        string text = _assembler.FrameCount <= 1
            ? $"{_selection.Width} × {_selection.Height}"
            : $"{_selection.Width} × {_assembler.CapturedHeight:N0}";
        using var font = new Font("Microsoft YaHei UI", 8.3f, FontStyle.Regular);
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            bounds,
            Color.FromArgb(235, 240, 244),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawHint(Graphics graphics)
    {
        Rectangle bounds = GetHintBounds();
        if (bounds.IsEmpty) return;

        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 7);
        using var fill = new SolidBrush(Color.FromArgb(236, 8, 9, 12));
        graphics.FillPath(fill, path);
        using var font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
        TextRenderer.DrawText(
            graphics,
            _statusText,
            font,
            bounds,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawToolbar(Graphics graphics)
    {
        Rectangle bounds = GetToolbarBounds();
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, 9);
        using var shadow = new SolidBrush(Color.FromArgb(72, 0, 0, 0));
        using var fill = new SolidBrush(Color.FromArgb(248, 35, 37, 42));
        Rectangle shadowBounds = bounds;
        shadowBounds.Offset(0, 2);
        using GraphicsPath shadowPath = GraphicsHelper.GetRoundedRectangle(shadowBounds, 9);
        graphics.FillPath(shadow, shadowPath);
        graphics.FillPath(fill, path);

        foreach (ScrollModeButton button in GetToolbarButtons())
        {
            bool hovered = button.Action == _hoveredAction;
            bool pressed = button.Action == _pressedAction;
            if (hovered || pressed || button.Action == ScrollModeAction.Mode)
            {
                Color color = pressed
                    ? Color.FromArgb(78, 255, 255, 255)
                    : Color.FromArgb(button.Action == ScrollModeAction.Mode ? 42 : 28, 255, 255, 255);
                using GraphicsPath buttonPath = GraphicsHelper.GetRoundedRectangle(button.Bounds, 6);
                using var buttonFill = new SolidBrush(color);
                graphics.FillPath(buttonFill, buttonPath);
            }

            Color iconColor = button.Action switch
            {
                ScrollModeAction.Cancel => Color.FromArgb(255, 92, 84),
                ScrollModeAction.Confirm => Color.FromArgb(45, 211, 122),
                _ => Color.FromArgb(218, 224, 232)
            };

            if (button.Icon is LucideIcon icon && string.IsNullOrEmpty(button.Text))
            {
                int iconX = button.Bounds.X + (button.Bounds.Width - 17) / 2;
                LucideRenderer.Draw(graphics, icon, iconX, button.Bounds.Y + 7, 17, iconColor, 1.9f);
            }
            else if (!string.IsNullOrEmpty(button.Text))
            {
                using var font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
                int textLeft = button.Bounds.Left;
                if (button.Icon is LucideIcon textIcon)
                {
                    LucideRenderer.Draw(graphics, textIcon, button.Bounds.Left + 9, button.Bounds.Y + 8, 16, iconColor, 1.8f);
                    textLeft += 20;
                }
                TextRenderer.DrawText(
                    graphics,
                    button.Action == ScrollModeAction.AutoScroll && _autoCapturing ? "滚动中" : button.Text,
                    font,
                    Rectangle.FromLTRB(textLeft, button.Bounds.Top, button.Bounds.Right, button.Bounds.Bottom),
                    Color.FromArgb(232, 236, 242),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        ScrollModeButton mode = GetToolbarButtons().First(button => button.Action == ScrollModeAction.Mode);
        using var arrowPen = new Pen(Color.FromArgb(175, 183, 194), 1.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        int arrowX = mode.Bounds.Right - 13;
        int arrowY = mode.Bounds.Top + mode.Bounds.Height / 2 - 1;
        graphics.DrawLines(arrowPen, new Point[] { new(arrowX - 3, arrowY - 1), new(arrowX, arrowY + 2), new(arrowX + 3, arrowY - 1) });

        int separatorX = GetToolbarButtons().First(button => button.Action == ScrollModeAction.Save).Bounds.Left - 7;
        using var separator = new Pen(Color.FromArgb(55, 255, 255, 255));
        graphics.DrawLine(separator, separatorX, bounds.Top + 12, separatorX, bounds.Bottom - 12);
    }

    private Rectangle GetToolbarBounds()
    {
        const int width = 374;
        int x = _selection.Left + (_selection.Width - width) / 2;
        x = Math.Clamp(x, 8, Math.Max(8, ClientSize.Width - width - 8));
        int below = _selection.Bottom + 9;
        int y = below + ToolbarHeight <= ClientSize.Height - 8
            ? below
            : _selection.Top - ToolbarHeight - 9;
        y = Math.Clamp(y, 8, Math.Max(8, ClientSize.Height - ToolbarHeight - 8));
        return new Rectangle(x, y, width, ToolbarHeight);
    }

    private ScrollModeButton[] GetToolbarButtons()
    {
        Rectangle toolbar = GetToolbarBounds();
        int y = toolbar.Top + (ToolbarHeight - ButtonHeight) / 2;
        return
        [
            new ScrollModeButton(ScrollModeAction.Mode, new Rectangle(toolbar.Left + 8, y, 92, ButtonHeight), null, "竖向截图"),
            new ScrollModeButton(ScrollModeAction.AutoScroll, new Rectangle(toolbar.Left + 106, y, 112, ButtonHeight), LucideIcon.ChevronsDown, "自动滚动"),
            new ScrollModeButton(ScrollModeAction.Save, new Rectangle(toolbar.Left + 232, y, 32, ButtonHeight), LucideIcon.Download, string.Empty),
            new ScrollModeButton(ScrollModeAction.Cancel, new Rectangle(toolbar.Left + 272, y, 32, ButtonHeight), LucideIcon.X, string.Empty),
            new ScrollModeButton(ScrollModeAction.Confirm, new Rectangle(toolbar.Left + 312, y, 36, ButtonHeight), LucideIcon.Check, string.Empty)
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
        int width = _assembler.FrameCount <= 1 ? 92 : 116;
        int x = Math.Clamp(_selection.Left, 6, Math.Max(6, ClientSize.Width - width - 6));
        int y = _selection.Top - 34;
        if (y < 6)
        {
            y = _selection.Bottom + 8;
            if (y + 25 > ClientSize.Height - 6) return Rectangle.Empty;
        }
        return new Rectangle(x, y, width, 25);
    }

    private Rectangle GetHintBounds()
    {
        int width = Math.Max(40, Math.Min(368, _selection.Width - 20));
        const int height = 40;
        int x = _selection.Left + (_selection.Width - width) / 2;
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
        _showHint = true;
        _hintEndsAt = DateTime.UtcNow.AddMilliseconds(1650);
        Invalidate(GetHintBounds());
        Invalidate(GetToolbarBounds());
    }

    private void OnThemeChanged()
    {
        if (!IsDisposed) Invalidate();
    }

    private readonly record struct ScrollModeButton(
        ScrollModeAction Action,
        Rectangle Bounds,
        LucideIcon? Icon,
        string Text);

    private enum ScrollModeAction
    {
        None,
        Mode,
        AutoScroll,
        Save,
        Cancel,
        Confirm
    }
}
