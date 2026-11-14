using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SkiaSharp;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Controls;

internal enum PageTransitionAxis
{
    Vertical,
    Horizontal
}

/// <summary>
/// Hardware-SIMD Skia-composited crossfade + directional slide shield overlay for page switches.
/// Uses ControlStyles.Opaque to prevent sibling bleed-through and ghosting.
/// </summary>
internal sealed class PageTransitionOverlay : Control
{
    private readonly SmoothValueAnimator _progressAnimator;
    private readonly SkiaRasterLayer _rasterLayer = new();
    private SKBitmap? _outgoingBitmap;
    private SKBitmap? _incomingBitmap;
    private Color _backdropColor;
    private PageTransitionAxis _axis;
    private int _direction;
    private bool _rasterDirty = true;
    private bool _isFrozenOutgoing;

    public bool IsTransitioning => Visible && (_isFrozenOutgoing || _progressAnimator.IsAnimating);

    public PageTransitionOverlay()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.Opaque |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
        Visible = false;
        TabStop = false;

        _progressAnimator = new SmoothValueAnimator(this, 1f, HandleFrame, defaultDurationMs: 155, settleTolerance: 0.002f);
    }

    public static Bitmap? TryCaptureSnapshot(Control control)
    {
        if (control.IsDisposed || control.Width <= 0 || control.Height <= 0 || !control.IsHandleCreated)
        {
            return null;
        }

        try
        {
            var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.Clear(ThemeManager.Palette.BackgroundColor);
            }

            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shields the container with the frozen outgoing snapshot before child visibility/layout swaps occur.
    /// </summary>
    public void FreezeOutgoing(Bitmap? outgoing, Rectangle bounds)
    {
        _progressAnimator.Stop();
        ClearBitmaps();

        if (outgoing is null || bounds.Width <= 0 || bounds.Height <= 0 || Parent is null || !Parent.IsHandleCreated)
        {
            outgoing?.Dispose();
            Visible = false;
            return;
        }

        try
        {
            _outgoingBitmap = ConvertAndDisposeBitmap(outgoing);
            if (_outgoingBitmap is null)
            {
                Visible = false;
                return;
            }

            _backdropColor = ThemeManager.Palette.BackgroundColor;
            _isFrozenOutgoing = true;
            _progressAnimator.SnapTo(0f);
            _rasterDirty = true;
            Bounds = bounds;
            Visible = true;
            BringToFront();
            Update();
        }
        catch
        {
            FinishTransition();
        }
    }

    /// <summary>
    /// Starts the smooth reveal from the already-frozen outgoing frame to the settled incoming page.
    /// </summary>
    public void BeginReveal(Bitmap? incoming, PageTransitionAxis axis, int direction)
    {
        if (incoming is null || Parent is null || !Parent.IsHandleCreated)
        {
            incoming?.Dispose();
            FinishTransition();
            return;
        }

        try
        {
            _incomingBitmap?.Dispose();
            _incomingBitmap = ConvertAndDisposeBitmap(incoming);
            if (_incomingBitmap is null)
            {
                FinishTransition();
                return;
            }

            _axis = axis;
            _direction = direction == 0 ? 1 : Math.Sign(direction);
            _isFrozenOutgoing = false;
            _rasterDirty = true;
            BringToFront();

            _progressAnimator.SnapTo(0f);
            _progressAnimator.SetTarget(1f);
        }
        catch
        {
            FinishTransition();
        }
    }

    public void CompleteImmediately()
    {
        _isFrozenOutgoing = false;
        _progressAnimator.SnapTo(1f);
        FinishTransition();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        CompleteImmediately();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _rasterDirty = true;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // Opaque Skia raster surface renders the complete frame; skip GDI erase pass.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Visible || Width <= 0 || Height <= 0) return;

        if (_rasterDirty || _rasterLayer.Size != ClientSize)
        {
            _rasterLayer.Render(ClientSize, RenderFrame);
            _rasterDirty = false;
        }

        _rasterLayer.Draw(e.Graphics, Point.Empty);
    }

    private void RenderFrame(SKCanvas canvas)
    {
        canvas.Clear(SkiaDrawing.ToSkColor(_backdropColor));

        if (_isFrozenOutgoing || _incomingBitmap is null)
        {
            if (_outgoingBitmap is not null)
            {
                DrawBitmapAligned(canvas, _outgoingBitmap, 1f, 0f);
            }
            return;
        }

        float progress = Math.Clamp(_progressAnimator.Value, 0f, 1f);
        float travel = GraphicsHelper.Dip(this, 10);

        if (_outgoingBitmap is not null)
        {
            float outAlpha = MathF.Pow(Math.Clamp(1f - progress * 1.15f, 0f, 1f), 1.35f);
            if (outAlpha > 0.01f)
            {
                float outShift = -_direction * travel * 0.45f * progress;
                DrawBitmapAligned(canvas, _outgoingBitmap, outAlpha, outShift);
            }
        }

        float inShift = _direction * travel * (1f - progress);
        float inAlpha = Math.Clamp(progress, 0f, 1f);
        DrawBitmapAligned(canvas, _incomingBitmap, inAlpha, inShift);
    }

    private void DrawBitmapAligned(SKCanvas canvas, SKBitmap bitmap, float opacity, float axisShift)
    {
        byte alphaByte = (byte)Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255);
        if (alphaByte == 0) return;

        float dx = _axis == PageTransitionAxis.Horizontal ? axisShift : 0f;
        float dy = _axis == PageTransitionAxis.Vertical ? axisShift : 0f;

        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, alphaByte),
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Low
        };

        var dest = SKRect.Create(dx, dy, ClientSize.Width, ClientSize.Height);
        canvas.DrawBitmap(bitmap, dest, paint);
    }

    private static SKBitmap? ConvertAndDisposeBitmap(Bitmap bitmap)
    {
        try
        {
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var owned = new SKBitmap(info);
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                using var source = new SKPixmap(info, data.Scan0, data.Stride);
                if (!source.ReadPixels(info, owned.GetPixels(), owned.RowBytes, 0, 0))
                {
                    owned.Dispose();
                    return null;
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return owned;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private void HandleFrame()
    {
        if (!Visible || _isFrozenOutgoing) return;

        if (!_progressAnimator.IsAnimating && _progressAnimator.Value >= 0.995f)
        {
            FinishTransition();
            return;
        }

        _rasterDirty = true;
        Invalidate();
    }

    private void FinishTransition()
    {
        _isFrozenOutgoing = false;
        Visible = false;
        ClearBitmaps();
    }

    private void ClearBitmaps()
    {
        _outgoingBitmap?.Dispose();
        _outgoingBitmap = null;
        _incomingBitmap?.Dispose();
        _incomingBitmap = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _progressAnimator.Dispose();
            ClearBitmaps();
            _rasterLayer.Dispose();
        }
        base.Dispose(disposing);
    }
}
