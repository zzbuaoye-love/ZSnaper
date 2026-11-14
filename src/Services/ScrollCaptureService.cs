using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ZSnaper.Interop;

namespace ZSnaper.Services;

internal enum ScrollCaptureStopReason
{
    Completed,
    Cancelled,
    NotScrollable,
    SizeLimit
}

internal sealed record ScrollCaptureResult(
    Bitmap? Image,
    ScrollCaptureStopReason Reason,
    int FrameCount,
    string? ErrorMessage = null);

internal enum ScrollCaptureFrameStatus
{
    Unchanged,
    Added,
    NoOverlap,
    SizeLimit,
    Pending,
    ScrolledUp
}

internal sealed class ScrollCaptureAssembler : IDisposable
{
    private const int MaximumHeight = 60_000;
    private const long MaximumPixels = 100_000_000;

    private readonly List<Bitmap> _pieces = [];
    private Bitmap _previous;
    private ScrollCaptureService.FrameSample _previousSample;
    private Bitmap? _stickyFooter;
    private int _stickyFooterHeight;

    public ScrollCaptureAssembler(Bitmap initialFrame)
    {
        _previous = (Bitmap)initialFrame.Clone();
        _previousSample = ScrollCaptureService.CreateFrameSample(_previous);
        _pieces.Add((Bitmap)initialFrame.Clone());
        CapturedHeight = initialFrame.Height;
        FrameCount = 1;
        Width = initialFrame.Width;
    }

    public int Width { get; }
    public int CapturedHeight { get; private set; }
    public int FrameCount { get; private set; }
    public int LastShift { get; private set; }

    public ScrollCaptureFrameStatus AddFrame(Bitmap current, int? expectedShift = null, bool allowSmallShift = false)
    {
        if (current.Width != Width || current.Height != _previous.Height || current.Height < 32)
        {
            LastShift = 0;
            return ScrollCaptureFrameStatus.NoOverlap;
        }

        ScrollCaptureService.FrameSample currentSample = ScrollCaptureService.CreateFrameSample(current);
        ScrollCaptureService.ShiftAnalysis analysis = ScrollCaptureService.AnalyzeShift(
            _previousSample,
            currentSample,
            expectedShift,
            allowSmallShift);

        switch (analysis.Status)
        {
            case ScrollCaptureService.ShiftStatus.Unchanged:
                LastShift = 0;
                return ScrollCaptureFrameStatus.Unchanged;

            case ScrollCaptureService.ShiftStatus.Pending:
                LastShift = analysis.Shift;
                return ScrollCaptureFrameStatus.Pending;

            case ScrollCaptureService.ShiftStatus.ScrolledUp:
                LastShift = -analysis.Shift;
                return ScrollCaptureFrameStatus.ScrolledUp;

            case ScrollCaptureService.ShiftStatus.NoOverlap:
                LastShift = 0;
                return ScrollCaptureFrameStatus.NoOverlap;
        }

        int shift = analysis.Shift;
        LastShift = shift;

        UpdateStickyFooterState(current, shift, analysis.StickyBottom);

        int footerHeight = _stickyFooterHeight;
        int availableHeight = Math.Min(
            MaximumHeight - CapturedHeight,
            (int)Math.Max(0, (MaximumPixels - (long)Width * CapturedHeight) / Width));
        if (availableHeight <= 0)
        {
            return ScrollCaptureFrameStatus.SizeLimit;
        }

        int appendHeight = Math.Min(shift, availableHeight);
        int sourceTop = Math.Clamp(current.Height - footerHeight - appendHeight, 0, Math.Max(0, current.Height - appendHeight));
        Rectangle source = new(0, sourceTop, current.Width, appendHeight);
        _pieces.Add(current.Clone(source, PixelFormat.Format32bppArgb));

        if (_stickyFooterHeight > 0)
        {
            Rectangle footerRect = new(0, current.Height - _stickyFooterHeight, current.Width, _stickyFooterHeight);
            _stickyFooter?.Dispose();
            _stickyFooter = current.Clone(footerRect, PixelFormat.Format32bppArgb);
        }

        CapturedHeight += appendHeight;
        FrameCount++;

        _previous.Dispose();
        _previous = (Bitmap)current.Clone();
        _previousSample = currentSample;
        return appendHeight < shift
            ? ScrollCaptureFrameStatus.SizeLimit
            : ScrollCaptureFrameStatus.Added;
    }

    public Bitmap BuildImage()
    {
        var result = new Bitmap(Width, CapturedHeight, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;

        int y = 0;
        foreach (Bitmap piece in _pieces)
        {
            graphics.DrawImageUnscaled(piece, 0, y);
            y += piece.Height;
        }

        if (_stickyFooter is not null)
        {
            graphics.DrawImageUnscaled(_stickyFooter, 0, y);
        }

        return result;
    }

    public void Dispose()
    {
        _previous.Dispose();
        _stickyFooter?.Dispose();
        foreach (Bitmap piece in _pieces) piece.Dispose();
        _pieces.Clear();
    }

    private void UpdateStickyFooterState(Bitmap current, int shift, int detectedStickyBottom)
    {
        if (_stickyFooterHeight == 0)
        {
            // Promote a verified static bottom bar on the very first stitched step so
            // piece 0 does not embed a duplicate footer mid-image.
            if (FrameCount == 1 &&
                detectedStickyBottom >= 12 &&
                detectedStickyBottom < _pieces[0].Height - 16 &&
                detectedStickyBottom <= current.Height - shift - 16)
            {
                Bitmap initial = _pieces[0];
                int bodyHeight = initial.Height - detectedStickyBottom;
                Bitmap trimmed = initial.Clone(new Rectangle(0, 0, Width, bodyHeight), PixelFormat.Format32bppArgb);
                Bitmap footer = initial.Clone(new Rectangle(0, bodyHeight, Width, detectedStickyBottom), PixelFormat.Format32bppArgb);
                _pieces[0] = trimmed;
                initial.Dispose();
                _stickyFooter = footer;
                _stickyFooterHeight = detectedStickyBottom;
                // Total CapturedHeight remains unchanged here (trimmed + footer == initial.Height).
            }

            return;
        }

        // If the footer height changed or disappeared later, commit the prior sticky
        // footer before appending normal full-bottom strips so no pixels are lost.
        if (detectedStickyBottom == 0 ||
            Math.Abs(detectedStickyBottom - _stickyFooterHeight) > 6 ||
            current.Height - _stickyFooterHeight - shift < 0)
        {
            if (_stickyFooter is not null)
            {
                _pieces.Add(_stickyFooter);
                _stickyFooter = null;
            }

            _stickyFooterHeight = 0;
        }
    }
}

/// <summary>
/// Captures a fixed screen viewport while its underlying control scrolls and joins
/// the newly revealed rows. UI Automation with proportional percent stepping is
/// preferred; adaptive mouse wheel + settle detection covers inner webviews, JS
/// scroll containers, and custom controls.
/// </summary>
internal static class ScrollCaptureService
{
    private const int MaximumFrames = 120;
    private const int InitialSettleCheckDelayMs = 90;
    private const int SettlePollIntervalMs = 75;
    private const int MaximumSettleWaitMs = 560;
    private const int MinimumManualCommitShift = 6;

    internal enum ShiftStatus
    {
        Unchanged,
        Matched,
        Pending,
        ScrolledUp,
        NoOverlap
    }

    internal readonly record struct ShiftAnalysis(
        ShiftStatus Status,
        int Shift,
        int StickyTop,
        int StickyBottom);

    internal sealed class FrameSample
    {
        internal FrameSample(
            int width,
            int height,
            int columnCount,
            byte[] rgb,
            double[] rowComplexity)
        {
            Width = width;
            Height = height;
            ColumnCount = columnCount;
            Rgb = rgb;
            RowComplexity = rowComplexity;
        }

        internal int Width { get; }
        internal int Height { get; }
        internal int ColumnCount { get; }
        internal byte[] Rgb { get; }
        internal double[] RowComplexity { get; }
    }

    public static async Task<ScrollCaptureResult> CaptureAsync(
        Rectangle screenBounds,
        CancellationToken cancellationToken = default)
    {
        if (screenBounds.Width < 40 || screenBounds.Height < 40)
        {
            return new ScrollCaptureResult(
                null,
                ScrollCaptureStopReason.NotScrollable,
                0,
                "选区太小，无法进行滚动截图");
        }

        try
        {
            if (!await DelayWithEscapeAsync(140, cancellationToken))
            {
                return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, 0);
            }

            using Bitmap initial = CaptureService.CaptureScreen(screenBounds);
            using var assembler = new ScrollCaptureAssembler(initial);
            ScrollCaptureResult run = await ContinueCaptureAsync(
                screenBounds,
                assembler,
                cancellationToken);
            if (run.Reason == ScrollCaptureStopReason.Cancelled || assembler.FrameCount <= 1)
            {
                return run;
            }

            return run with { Image = assembler.BuildImage(), FrameCount = assembler.FrameCount };
        }
        catch (OperationCanceledException)
        {
            return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, 0);
        }
    }

    public static Task<ScrollCaptureResult> ContinueCaptureAsync(
        Rectangle screenBounds,
        ScrollCaptureAssembler assembler,
        CancellationToken cancellationToken = default,
        Action<int, int>? progress = null,
        Scroller? preparedScroller = null,
        IReadOnlySet<nint>? ignoredWindows = null)
    {
        return Task.Run(
            () => ContinueCaptureCoreAsync(
                screenBounds,
                assembler,
                cancellationToken,
                progress,
                preparedScroller,
                ignoredWindows),
            cancellationToken);
    }

    private static async Task<ScrollCaptureResult> ContinueCaptureCoreAsync(
        Rectangle screenBounds,
        ScrollCaptureAssembler assembler,
        CancellationToken cancellationToken,
        Action<int, int>? progress,
        Scroller? preparedScroller,
        IReadOnlySet<nint>? ignoredWindows)
    {
        Point center = new(
            screenBounds.Left + screenBounds.Width / 2,
            screenBounds.Top + screenBounds.Height / 2);
        bool ownsScroller = preparedScroller is null;
        Scroller? scroller = preparedScroller ?? CreateScroller(center, screenBounds.Height, ignoredWindows);
        if (scroller is null)
        {
            return new ScrollCaptureResult(
                null,
                ScrollCaptureStopReason.NotScrollable,
                assembler.FrameCount,
                "未找到可滚动的窗口或控件");
        }

        Scroller? ownedFallbackScroller = null;
        bool switchedToWheel = false;

        try
        {
            for (int frame = assembler.FrameCount; frame < MaximumFrames; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EscapePressed())
                {
                    return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, assembler.FrameCount);
                }

                ScrollStep step = scroller.ScrollNext();
                if (!step.Moved)
                {
                    // UIA ScrollPattern may bind to a non-rendering container or fail on custom JS scrollers;
                    // switch automatically to adaptive wheel scrolling before giving up.
                    if (step.AllowWheelFallback &&
                        !switchedToWheel &&
                        scroller.CanFallbackToWheel &&
                        TrySwitchToWheelFallback(
                            center,
                            screenBounds.Height,
                            ignoredWindows,
                            ref scroller,
                            ref ownedFallbackScroller))
                    {
                        switchedToWheel = true;
                        step = scroller.ScrollNext();
                    }

                    if (!step.Moved)
                    {
                        break;
                    }
                }

                (bool continued, Bitmap? settled) = await WaitForSettledFrameAsync(
                    screenBounds,
                    step.IsSmoothWheel ? InitialSettleCheckDelayMs : 110,
                    cancellationToken);
                if (!continued)
                {
                    settled?.Dispose();
                    return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, assembler.FrameCount);
                }

                using Bitmap current = settled ?? CaptureService.CaptureScreen(screenBounds);
                ScrollCaptureFrameStatus status = assembler.AddFrame(current, step.ExpectedPixelShift, allowSmallShift: true);

                // If ScrollPattern moved UIA state cleanly but overshot selection height (common with inner viewports),
                // roll back to the pre-step UIA percentage and retry once at half step.
                if (status == ScrollCaptureFrameStatus.NoOverlap &&
                    step.UsedSetScrollPercent &&
                    step.PreviousPercent is double prevPercent &&
                    scroller.TryRestoreAndStepSmaller(prevPercent, 0.45, out ScrollStep retryStep))
                {
                    if (!await DelayWithEscapeAsync(150, cancellationToken))
                    {
                        return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, assembler.FrameCount);
                    }

                    using Bitmap retried = CaptureService.CaptureScreen(screenBounds);
                    status = assembler.AddFrame(retried, retryStep.ExpectedPixelShift, allowSmallShift: true);
                    step = retryStep;
                }

                // Seamlessly switch from ScrollPattern to adaptive mouse wheel whenever UIA stalls or stops advancing.
                if (status == ScrollCaptureFrameStatus.Unchanged &&
                    !switchedToWheel &&
                    scroller.CanFallbackToWheel &&
                    TrySwitchToWheelFallback(
                        center,
                        screenBounds.Height,
                        ignoredWindows,
                        ref scroller,
                        ref ownedFallbackScroller))
                {
                    switchedToWheel = true;
                    ScrollStep fallbackStep = scroller.ScrollNext();
                    if (fallbackStep.Moved)
                    {
                        (bool fallbackOk, Bitmap? fallbackBitmap) = await WaitForSettledFrameAsync(
                            screenBounds,
                            InitialSettleCheckDelayMs,
                            cancellationToken);
                        if (!fallbackOk)
                        {
                            fallbackBitmap?.Dispose();
                            return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, assembler.FrameCount);
                        }

                        using Bitmap fb = fallbackBitmap ?? CaptureService.CaptureScreen(screenBounds);
                        status = assembler.AddFrame(fb, fallbackStep.ExpectedPixelShift, allowSmallShift: true);
                        step = fallbackStep;
                    }
                }

                if (status == ScrollCaptureFrameStatus.Unchanged)
                {
                    if (step.AllowPostMessageRetry && scroller.RetryWithPostMessage())
                    {
                        (bool pmOk, Bitmap? pmBitmap) = await WaitForSettledFrameAsync(
                            screenBounds,
                            InitialSettleCheckDelayMs,
                            cancellationToken);
                        if (!pmOk)
                        {
                            pmBitmap?.Dispose();
                            return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, assembler.FrameCount);
                        }

                        using Bitmap pmFrame = pmBitmap ?? CaptureService.CaptureScreen(screenBounds);
                        status = assembler.AddFrame(pmFrame, expectedShift: null, allowSmallShift: true);
                    }

                    if (status is ScrollCaptureFrameStatus.Unchanged or ScrollCaptureFrameStatus.ScrolledUp)
                    {
                        break;
                    }
                }

                if (status == ScrollCaptureFrameStatus.ScrolledUp)
                {
                    break;
                }

                if (status == ScrollCaptureFrameStatus.NoOverlap)
                {
                    return new ScrollCaptureResult(
                        null,
                        ScrollCaptureStopReason.NotScrollable,
                        assembler.FrameCount,
                        "页面变化过快，无法找到连续的拼接位置");
                }

                if (status == ScrollCaptureFrameStatus.Added || status == ScrollCaptureFrameStatus.SizeLimit)
                {
                    scroller.ReportObservedShift(assembler.LastShift, screenBounds.Height);
                    progress?.Invoke(assembler.FrameCount, assembler.CapturedHeight);
                }

                if (status == ScrollCaptureFrameStatus.SizeLimit || step.ReachedEnd)
                {
                    return new ScrollCaptureResult(
                        null,
                        status == ScrollCaptureFrameStatus.SizeLimit
                            ? ScrollCaptureStopReason.SizeLimit
                            : ScrollCaptureStopReason.Completed,
                        assembler.FrameCount);
                }
            }

            ScrollCaptureStopReason reason = assembler.FrameCount >= MaximumFrames
                ? ScrollCaptureStopReason.SizeLimit
                : assembler.FrameCount > 1
                    ? ScrollCaptureStopReason.Completed
                    : ScrollCaptureStopReason.NotScrollable;
            string? error = assembler.FrameCount > 1
                ? null
                : "选区内容没有发生滚动，请将选区中心放在页面或列表内容上";
            return new ScrollCaptureResult(null, reason, assembler.FrameCount, error);
        }
        catch (OperationCanceledException)
        {
            return new ScrollCaptureResult(null, ScrollCaptureStopReason.Cancelled, assembler.FrameCount);
        }
        catch (ElementNotAvailableException)
        {
            return new ScrollCaptureResult(null, ScrollCaptureStopReason.NotScrollable, assembler.FrameCount, "滚动目标已关闭或不可用");
        }
        catch (COMException)
        {
            return new ScrollCaptureResult(null, ScrollCaptureStopReason.NotScrollable, assembler.FrameCount, "目标应用中断了滚动截图");
        }
        finally
        {
            ownedFallbackScroller?.Dispose();
            if (ownsScroller && !ReferenceEquals(scroller, ownedFallbackScroller))
            {
                scroller.Dispose();
            }
        }
    }

    internal static Scroller? CreateScroller(
        Point screenPoint,
        int selectionHeight = 400,
        IReadOnlySet<nint>? ignoredWindows = null)
    {
        WheelScrollController? wheel = WheelScrollController.TryCreate(screenPoint, selectionHeight, ignoredWindows);
        if (wheel is { IsChromiumHost: true })
        {
            // Chromium/Electron/WebView2 renderers handle adaptive wheel scrolling immediately
            // without triggering multi-second UIA accessibility tree initialization stalls.
            return new Scroller(null, wheel, preferAutomation: false);
        }

        ScrollPatternController? automation = ScrollPatternController.TryCreate(
            screenPoint,
            selectionHeight,
            stepTimeoutMs: 2200);
        if (automation is not null)
        {
            return new Scroller(automation, wheel, preferAutomation: true);
        }

        return wheel is null ? null : new Scroller(null, wheel, preferAutomation: false);
    }

    internal static double CalculateTargetScrollPercent(
        double currentPercent,
        double viewSizePercent,
        double viewportPixelHeight,
        double desiredPixelStep)
    {
        if (viewSizePercent <= 0.01d || viewSizePercent >= 99.95d || viewportPixelHeight <= 1d)
        {
            return Math.Min(100d, currentPercent + 10d);
        }

        // UIA VerticalScrollPercent spans (total - viewport), while VerticalViewSize is
        // viewport / total * 100. Convert desired pixel step into the same denominator.
        double scrollableSpanRatio = (100d - viewSizePercent) / viewSizePercent;
        double deltaPercent = (desiredPixelStep / viewportPixelHeight) * 100d / Math.Max(0.001d, scrollableSpanRatio);
        return Math.Min(100d, currentPercent + Math.Max(0.05d, deltaPercent));
    }

    internal static int EstimateExpectedPixelShift(
        double deltaPercent,
        double viewSizePercent,
        double viewportPixelHeight)
    {
        if (deltaPercent <= 0d || viewSizePercent <= 0.01d || viewSizePercent >= 99.95d || viewportPixelHeight <= 1d)
        {
            return 0;
        }

        double pixels = viewportPixelHeight * (deltaPercent / 100d) * ((100d - viewSizePercent) / viewSizePercent);
        return Math.Max(1, (int)Math.Round(pixels));
    }

    internal static bool AreVisuallySame(Bitmap first, Bitmap second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
        {
            return false;
        }

        FrameSample firstSample = CreateFrameSample(first);
        FrameSample secondSample = CreateFrameSample(second);
        return AreSamplesVisuallySame(firstSample, secondSample);
    }

    /// <summary>
    /// Finds how many rows the viewport advanced while isolating static sticky headers/footers
    /// and prioritizing rows with visual texture so sparse pages match reliably.
    /// </summary>
    internal static int EstimateVerticalShift(Bitmap previous, Bitmap current, int? expectedShift = null)
    {
        if (previous.Width != current.Width || previous.Height != current.Height || previous.Height < 32)
        {
            return 0;
        }

        FrameSample prevSample = CreateFrameSample(previous);
        FrameSample currSample = CreateFrameSample(current);
        ShiftAnalysis analysis = AnalyzeShift(prevSample, currSample, expectedShift, allowSmallShift: true);
        return analysis.Status == ShiftStatus.Matched ? analysis.Shift : 0;
    }

    internal static unsafe FrameSample CreateFrameSample(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int left = width >= 80 ? Math.Clamp(width / 32, 2, 16) : 0;
        int rightMargin = width >= 80 ? Math.Clamp(width / 16, 18, 28) : 0;
        int right = Math.Max(left + 4, width - rightMargin);
        int span = Math.Max(1, right - left);
        int cols = Math.Clamp(width / 10, 12, 64);
        if (span < cols)
        {
            cols = Math.Max(1, span);
        }

        int[] sampleXs = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            sampleXs[c] = cols == 1
                ? left
                : left + c * (span - 1) / (cols - 1);
        }

        byte[] rgb = new byte[height * cols * 3];
        double[] rowComplexity = new double[height];

        Rectangle bounds = new(0, 0, width, height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;
            int rowStride = cols * 3;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = basePtr + y * stride;
                int dstOffset = y * rowStride;
                int prevDstOffset = (y - 1) * rowStride;
                long horzEdge = 0;
                long vertEdge = 0;

                for (int c = 0; c < cols; c++)
                {
                    int srcOffset = sampleXs[c] * 4;
                    byte b = srcRow[srcOffset];
                    byte g = srcRow[srcOffset + 1];
                    byte r = srcRow[srcOffset + 2];

                    int p = dstOffset + c * 3;
                    rgb[p] = r;
                    rgb[p + 1] = g;
                    rgb[p + 2] = b;

                    if (c > 0)
                    {
                        horzEdge +=
                            Math.Abs(r - rgb[p - 3]) +
                            Math.Abs(g - rgb[p - 2]) +
                            Math.Abs(b - rgb[p - 1]);
                    }

                    if (y > 0)
                    {
                        int above = prevDstOffset + c * 3;
                        vertEdge +=
                            Math.Abs(r - rgb[above]) +
                            Math.Abs(g - rgb[above + 1]) +
                            Math.Abs(b - rgb[above + 2]);
                    }
                }

                double meanHorz = cols > 1 ? horzEdge / (3d * (cols - 1)) : 0d;
                double meanVert = y > 0 ? vertEdge / (3d * cols) : 0d;
                rowComplexity[y] = Math.Max(meanHorz, meanVert);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new FrameSample(width, height, cols, rgb, rowComplexity);
    }

    internal static ShiftAnalysis AnalyzeShift(
        FrameSample previous,
        FrameSample current,
        int? expectedShift,
        bool allowSmallShift)
    {
        int height = previous.Height;
        if (height != current.Height || previous.ColumnCount != current.ColumnCount || height < 32)
        {
            return new ShiftAnalysis(ShiftStatus.NoOverlap, 0, 0, 0);
        }

        int cols = previous.ColumnCount;
        ZeroShiftMetrics zero = MeasureZeroShift(previous, current);

        if (!HasMeaningfulVisualChange(zero, cols))
        {
            return new ShiftAnalysis(ShiftStatus.Unchanged, 0, 0, 0);
        }

        (int stickyTop, int stickyBottom) = DetectStickyBands(previous, current, zero);
        int activeHeight = height - stickyTop - stickyBottom;
        if (activeHeight < 48)
        {
            stickyTop = 0;
            stickyBottom = 0;
            activeHeight = height;
        }

        int minimumOverlap = expectedShift is null
            ? Math.Clamp(activeHeight / 6, Math.Min(20, activeHeight / 2), Math.Max(24, activeHeight / 2))
            : Math.Clamp(activeHeight / 10, Math.Min(16, activeHeight / 2), Math.Max(20, activeHeight / 2));
        int minSearchShift = 2;
        int maxShift = Math.Max(minSearchShift, activeHeight - minimumOverlap);

        ShiftCandidate best = FindBestShift(
            previous,
            current,
            stickyTop,
            stickyBottom,
            minSearchShift,
            maxShift,
            expectedShift);

        ShiftMetrics atZeroActive = ScoreShift(previous, current, 0, stickyTop, stickyBottom);

        // If hover highlight / video / cursor flicker updated only part of the frame while stationary
        // rows stayed intact (S=0 remains better), do not mistake in-place UI updates for an overshoot jump.
        if (!IsCandidateBetterThanZero(best, atZeroActive))
        {
            bool stationaryRowsIntact = atZeroActive.GoodRowFraction >= 0.50d &&
                                        (atZeroActive.InformativeRowCount == 0 ||
                                         atZeroActive.InformativeGoodFraction >= 0.45d);
            if (stationaryRowsIntact || zero.ChangedRows < Math.Max(4, height / 20))
            {
                return new ShiftAnalysis(ShiftStatus.Unchanged, 0, stickyTop, stickyBottom);
            }
        }

        if (!IsValidShiftMatch(best, atZeroActive, expectedShift))
        {
            // Check whether user scrolled upwards instead of downwards so manual capture does not falsely warn "too fast".
            ShiftCandidate reverse = FindBestShift(
                current,
                previous,
                stickyTop,
                stickyBottom,
                minSearchShift,
                maxShift,
                expectedShift: null);
            ShiftMetrics reverseZero = ScoreShift(current, previous, 0, stickyTop, stickyBottom);
            if (IsCandidateBetterThanZero(reverse, reverseZero) &&
                IsValidShiftMatch(reverse, reverseZero, expectedShift: null))
            {
                return new ShiftAnalysis(ShiftStatus.ScrolledUp, reverse.Shift, stickyTop, stickyBottom);
            }

            // Guard against in-place hovers/popups/animations that leave most of the full frame
            // unchanged at S=0 (including when DetectStickyBands isolated only the hovered patch).
            ShiftMetrics atZeroFull = (stickyTop == 0 && stickyBottom == 0)
                ? atZeroActive
                : ScoreShift(previous, current, 0, stickyTop: 0, stickyBottom: 0);
            if (atZeroFull.GoodRowFraction >= 0.55d &&
                (atZeroFull.InformativeRowCount == 0 || atZeroFull.InformativeGoodFraction >= 0.50d))
            {
                return new ShiftAnalysis(ShiftStatus.Unchanged, 0, 0, 0);
            }

            return new ShiftAnalysis(ShiftStatus.NoOverlap, 0, stickyTop, stickyBottom);
        }

        if (expectedShift is null && !allowSmallShift && best.Shift < MinimumManualCommitShift)
        {
            return new ShiftAnalysis(ShiftStatus.Pending, best.Shift, stickyTop, stickyBottom);
        }

        return new ShiftAnalysis(ShiftStatus.Matched, best.Shift, stickyTop, stickyBottom);
    }

    private readonly record struct ZeroShiftMetrics(
        double[] RowDiff,
        int[] RowChangedCols,
        double GlobalMeanDiff,
        int ChangedRows,
        int TotalChangedCols,
        int MaxRowChangedCols);

    private readonly record struct ShiftMetrics(
        double CompositeScore,
        double MeanDiff,
        double InformativeMeanDiff,
        double GoodRowFraction,
        double InformativeGoodFraction,
        int InformativeRowCount);

    private readonly record struct ShiftCandidate(int Shift, ShiftMetrics Metrics);

    private static bool AreSamplesVisuallySame(FrameSample first, FrameSample second)
    {
        ZeroShiftMetrics zero = MeasureZeroShift(first, second);
        return !HasMeaningfulVisualChange(zero, first.ColumnCount);
    }

    private static bool HasMeaningfulVisualChange(ZeroShiftMetrics zero, int cols)
    {
        if (zero.GlobalMeanDiff >= 0.45d)
        {
            return true;
        }

        // Catch sparse text or single divider lines shifting cleanly even on blank white pages.
        int rowColThreshold = Math.Max(3, cols / 8);
        return zero.ChangedRows >= 2 &&
               zero.MaxRowChangedCols >= rowColThreshold &&
               zero.TotalChangedCols >= Math.Max(10, cols / 3);
    }

    private static ZeroShiftMetrics MeasureZeroShift(FrameSample first, FrameSample second)
    {
        int height = first.Height;
        int cols = first.ColumnCount;
        int rowStride = cols * 3;
        byte[] a = first.Rgb;
        byte[] b = second.Rgb;

        double[] rowDiff = new double[height];
        int[] rowChangedCols = new int[height];
        double totalRowDiff = 0d;
        int changedRows = 0;
        int totalChangedCols = 0;
        int maxRowChangedCols = 0;

        for (int y = 0; y < height; y++)
        {
            int offset = y * rowStride;
            long rowSum = 0;
            int changedCols = 0;

            for (int c = 0; c < cols; c++)
            {
                int p = offset + c * 3;
                int dr = Math.Abs(a[p] - b[p]);
                int dg = Math.Abs(a[p + 1] - b[p + 1]);
                int db = Math.Abs(a[p + 2] - b[p + 2]);
                rowSum += dr + dg + db;
                if (Math.Max(dr, Math.Max(dg, db)) >= 10)
                {
                    changedCols++;
                }
            }

            double mean = rowSum / (3d * cols);
            rowDiff[y] = mean;
            rowChangedCols[y] = changedCols;
            totalRowDiff += mean;
            totalChangedCols += changedCols;
            if (changedCols > maxRowChangedCols)
            {
                maxRowChangedCols = changedCols;
            }

            if (mean >= 1.5d || changedCols >= Math.Max(2, cols / 10))
            {
                changedRows++;
            }
        }

        return new ZeroShiftMetrics(
            rowDiff,
            rowChangedCols,
            totalRowDiff / Math.Max(1, height),
            changedRows,
            totalChangedCols,
            maxRowChangedCols);
    }

    private static (int StickyTop, int StickyBottom) DetectStickyBands(
        FrameSample previous,
        FrameSample current,
        ZeroShiftMetrics zero)
    {
        int height = previous.Height;
        int maxBand = Math.Min(height * 2 / 5, Math.Max(24, height - 64));
        if (maxBand < 12)
        {
            return (0, 0);
        }

        int cols = previous.ColumnCount;
        int staticColTolerance = Math.Max(1, cols / 24);

        // Top sticky band
        int topPrefix = 0;
        int topTexturedRows = 0;
        while (topPrefix < maxBand &&
               zero.RowDiff[topPrefix] <= 1.6d &&
               zero.RowChangedCols[topPrefix] <= staticColTolerance)
        {
            if (previous.RowComplexity[topPrefix] >= 1.8d)
            {
                topTexturedRows++;
            }

            topPrefix++;
        }

        int stickyTop = 0;
        if (topPrefix >= 12 && topTexturedRows >= 3 && CountChangedRows(zero, topPrefix, height) >= 8)
        {
            stickyTop = topPrefix;
        }

        // Bottom sticky band
        int maxBottomStart = Math.Max(stickyTop + 64, height - maxBand);
        int bottomStart = height;
        int bottomTexturedRows = 0;
        while (bottomStart > maxBottomStart &&
               zero.RowDiff[bottomStart - 1] <= 1.6d &&
               zero.RowChangedCols[bottomStart - 1] <= staticColTolerance)
        {
            bottomStart--;
            if (previous.RowComplexity[bottomStart] >= 1.8d)
            {
                bottomTexturedRows++;
            }
        }

        int stickyBottom = 0;
        int bottomLen = height - bottomStart;
        if (bottomLen >= 12 && bottomTexturedRows >= 3 && CountChangedRows(zero, stickyTop, bottomStart) >= 8)
        {
            stickyBottom = bottomLen;
        }

        return (stickyTop, stickyBottom);
    }

    private static int CountChangedRows(ZeroShiftMetrics zero, int startY, int endY)
    {
        int count = 0;
        for (int y = startY; y < endY; y++)
        {
            if (zero.RowDiff[y] >= 2.0d)
            {
                count++;
            }
        }

        return count;
    }

    private static ShiftCandidate FindBestShift(
        FrameSample previous,
        FrameSample current,
        int stickyTop,
        int stickyBottom,
        int minShift,
        int maxShift,
        int? expectedShift)
    {
        if (expectedShift is > 0)
        {
            int radius = Math.Max(64, (previous.Height - stickyTop - stickyBottom) / 4);
            int narrowStart = Math.Clamp(expectedShift.Value - radius, minShift, maxShift);
            int narrowEnd = Math.Clamp(expectedShift.Value + radius, minShift, maxShift);

            ShiftCandidate narrowed = SearchShiftRange(
                previous,
                current,
                stickyTop,
                stickyBottom,
                narrowStart,
                narrowEnd);
            if (narrowed.Metrics.CompositeScore <= 8d && narrowed.Metrics.GoodRowFraction >= 0.78d)
            {
                return narrowed;
            }
        }

        return SearchShiftRange(previous, current, stickyTop, stickyBottom, minShift, maxShift);
    }

    private static ShiftCandidate SearchShiftRange(
        FrameSample previous,
        FrameSample current,
        int stickyTop,
        int stickyBottom,
        int startShift,
        int endShift)
    {
        int bestShift = startShift;
        ShiftMetrics bestMetrics = new(double.MaxValue, double.MaxValue, double.MaxValue, 0d, 0d, 0);

        for (int shift = startShift; shift <= endShift; shift++)
        {
            ShiftMetrics metrics = ScoreShift(previous, current, shift, stickyTop, stickyBottom);
            if (metrics.CompositeScore < bestMetrics.CompositeScore - 1e-6d)
            {
                bestMetrics = metrics;
                bestShift = shift;
            }
        }

        return new ShiftCandidate(bestShift, bestMetrics);
    }

    private static ShiftMetrics ScoreShift(
        FrameSample previous,
        FrameSample current,
        int shift,
        int stickyTop,
        int stickyBottom)
    {
        int overlapStart = stickyTop;
        int overlapEnd = previous.Height - stickyBottom - shift;
        int overlap = overlapEnd - overlapStart;
        if (overlap <= 8)
        {
            return new ShiftMetrics(double.MaxValue, double.MaxValue, double.MaxValue, 0d, 0d, 0);
        }

        // Skip tiny boundary trim near sticky seams and frame borders to resist 1-2px AA jitter.
        int trim = Math.Min(2, Math.Max(0, overlap / 16));
        int yStart = overlapStart + trim;
        int yEnd = overlapEnd - trim;
        if (yEnd <= yStart)
        {
            return new ShiftMetrics(double.MaxValue, double.MaxValue, double.MaxValue, 0d, 0d, 0);
        }

        int cols = previous.ColumnCount;
        int rowStride = cols * 3;
        byte[] prevRgb = previous.Rgb;
        byte[] currRgb = current.Rgb;
        double[] prevComp = previous.RowComplexity;
        double[] currComp = current.RowComplexity;

        // Subsample dense shifts moderately only when height is very large (> 600) -> keeping accuracy within 0ms!
        int yStep = overlap > 600 ? 2 : 1;

        double totalCappedDiff = 0d;
        double infCappedDiff = 0d;
        int totalRows = 0;
        int goodRows = 0;
        int infRows = 0;
        int infGoodRows = 0;

        for (int y = yStart; y < yEnd; y += yStep)
        {
            int currOffset = y * rowStride;
            int prevOffset = (y + shift) * rowStride;
            long rowSum = 0;

            for (int p = 0; p < rowStride; p++)
            {
                rowSum += Math.Abs(prevRgb[prevOffset + p] - currRgb[currOffset + p]);
            }

            double rowDiff = rowSum / (double)rowStride;
            double capped = Math.Min(42d, rowDiff);
            totalCappedDiff += capped;
            totalRows++;
            if (rowDiff <= 4.5d)
            {
                goodRows++;
            }

            if (currComp[y] >= 1.8d || prevComp[y + shift] >= 1.8d)
            {
                infCappedDiff += capped;
                infRows++;
                if (rowDiff <= 6.5d)
                {
                    infGoodRows++;
                }
            }
        }

        if (totalRows == 0)
        {
            return new ShiftMetrics(double.MaxValue, double.MaxValue, double.MaxValue, 0d, 0d, 0);
        }

        double meanDiff = totalCappedDiff / totalRows;
        double goodFrac = goodRows / (double)totalRows;
        double infMeanDiff = infRows > 0 ? infCappedDiff / infRows : meanDiff;
        double infGoodFrac = infRows > 0 ? infGoodRows / (double)infRows : goodFrac;

        // Emphasize informative (textured) rows while rewarding high row consensus so hover
        // buttons or single-row carets never derail valid page alignment.
        double consensusPenalty = infRows >= 6
            ? (1d - infGoodFrac) * 18d + (1d - goodFrac) * 6d
            : (1d - goodFrac) * 22d;
        double baseError = infRows >= 6 ? infMeanDiff * 0.65d + meanDiff * 0.35d : meanDiff;
        return new ShiftMetrics(
            baseError + consensusPenalty,
            meanDiff,
            infMeanDiff,
            goodFrac,
            infGoodFrac,
            infRows);
    }

    private static bool IsCandidateBetterThanZero(ShiftCandidate best, ShiftMetrics zeroActive)
    {
        if (best.Shift <= 0)
        {
            return false;
        }

        if (best.Metrics.InformativeRowCount >= 6 && zeroActive.InformativeRowCount >= 6)
        {
            return best.Metrics.InformativeMeanDiff < zeroActive.InformativeMeanDiff * 0.72d ||
                   best.Metrics.InformativeGoodFraction > zeroActive.InformativeGoodFraction + 0.12d;
        }

        return best.Metrics.MeanDiff < zeroActive.MeanDiff * 0.80d ||
               best.Metrics.GoodRowFraction > zeroActive.GoodRowFraction + 0.10d;
    }

    private static bool IsValidShiftMatch(
        ShiftCandidate best,
        ShiftMetrics zeroActive,
        int? expectedShift)
    {
        if (best.Shift <= 0)
        {
            return false;
        }

        ShiftMetrics m = best.Metrics;
        if (expectedShift is > 0)
        {
            if (m.InformativeRowCount >= 6)
            {
                return m.InformativeGoodFraction >= 0.58d && m.InformativeMeanDiff <= 22d;
            }

            return m.GoodRowFraction >= 0.68d && m.MeanDiff <= 18d;
        }

        // Manual scroll verification: accept strong alignment with generous tolerance for
        // mouse hover tinting or blinking carets, while blocking zero-overlap jumps.
        if (m.InformativeRowCount >= 6)
        {
            bool strongConsensus = m.InformativeGoodFraction >= 0.68d && m.InformativeMeanDiff <= 18d;
            bool beatsZero = m.InformativeMeanDiff <= zeroActive.InformativeMeanDiff * 0.68d ||
                             m.InformativeGoodFraction >= zeroActive.InformativeGoodFraction + 0.15d;
            return strongConsensus && beatsZero;
        }

        bool uniformConsensus = m.GoodRowFraction >= 0.80d && m.MeanDiff <= 10d;
        bool uniformBeatsZero = m.MeanDiff < zeroActive.MeanDiff * 0.78d ||
                                m.GoodRowFraction > zeroActive.GoodRowFraction + 0.10d;
        return uniformConsensus && uniformBeatsZero;
    }

    private static bool TrySwitchToWheelFallback(
        Point center,
        int selectionHeight,
        IReadOnlySet<nint>? ignoredWindows,
        ref Scroller scroller,
        ref Scroller? ownedFallbackScroller)
    {
        Scroller? wheel = CreateWheelOnlyScroller(center, selectionHeight, ignoredWindows);
        if (wheel is null)
        {
            return false;
        }

        ownedFallbackScroller?.Dispose();
        ownedFallbackScroller = wheel;
        scroller = wheel;
        return true;
    }

    private static Scroller? CreateWheelOnlyScroller(
        Point screenPoint,
        int selectionHeight,
        IReadOnlySet<nint>? ignoredWindows)
    {
        WheelScrollController? wheel = WheelScrollController.TryCreate(screenPoint, selectionHeight, ignoredWindows);
        return wheel is null ? null : new Scroller(null, wheel, preferAutomation: false);
    }

    private static async Task<(bool Continued, Bitmap? SettledFrame)> WaitForSettledFrameAsync(
        Rectangle screenBounds,
        int initialDelayMs,
        CancellationToken cancellationToken)
    {
        if (!await DelayWithEscapeAsync(initialDelayMs, cancellationToken))
        {
            return (false, null);
        }

        Bitmap previous = CaptureService.CaptureScreen(screenBounds);
        int elapsed = initialDelayMs;

        try
        {
            while (elapsed < MaximumSettleWaitMs)
            {
                if (!await DelayWithEscapeAsync(SettlePollIntervalMs, cancellationToken))
                {
                    previous.Dispose();
                    return (false, null);
                }

                elapsed += SettlePollIntervalMs;
                Bitmap current = CaptureService.CaptureScreen(screenBounds);
                if (AreVisuallySame(previous, current))
                {
                    previous.Dispose();
                    return (true, current);
                }

                previous.Dispose();
                previous = current;
            }

            return (true, previous);
        }
        catch
        {
            previous.Dispose();
            throw;
        }
    }

    private static async Task<bool> DelayWithEscapeAsync(int milliseconds, CancellationToken cancellationToken)
    {
        int remaining = milliseconds;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EscapePressed()) return false;
            int slice = Math.Min(25, remaining);
            await Task.Delay(slice, cancellationToken);
            remaining -= slice;
        }

        return !EscapePressed();
    }

    private static bool EscapePressed() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_ESCAPE) & 0x8000) != 0;

    internal readonly record struct ScrollStep(
        bool Moved,
        bool ReachedEnd,
        int? ExpectedPixelShift,
        bool UsedSetScrollPercent = false,
        double? PreviousPercent = null,
        bool AllowWheelFallback = false,
        bool IsSmoothWheel = false,
        bool AllowPostMessageRetry = false);

    internal sealed class Scroller : IDisposable
    {
        private readonly ScrollPatternController? _automation;
        private readonly WheelScrollController? _wheel;
        private readonly bool _preferAutomation;

        internal Scroller(
            ScrollPatternController? automation,
            WheelScrollController? wheel,
            bool preferAutomation = true)
        {
            _automation = automation;
            _wheel = wheel;
            _preferAutomation = preferAutomation;
        }

        internal bool CanFallbackToWheel => _preferAutomation && _automation is not null && _wheel is not null;

        internal ScrollStep ScrollNext() =>
            _preferAutomation && _automation is not null
                ? _automation.ScrollNext()
                : _wheel!.ScrollNext();

        internal bool TryRestoreAndStepSmaller(double previousPercent, double scale, out ScrollStep retryStep)
        {
            if (_preferAutomation && _automation is not null)
            {
                return _automation.TryRestoreAndStepSmaller(previousPercent, scale, out retryStep);
            }

            retryStep = default;
            return false;
        }

        internal bool RetryWithPostMessage() =>
            !_preferAutomation && _wheel is not null && _wheel.SendPostMessageWheel();

        internal void ReportObservedShift(int actualShift, int selectionHeight)
        {
            if (!_preferAutomation)
            {
                _wheel?.ReportObservedShift(actualShift, selectionHeight);
            }
        }

        public void Dispose() => _wheel?.Dispose();
    }

    internal sealed class ScrollPatternController
    {
        private readonly ScrollPattern _pattern;
        private readonly double _viewportPixelHeight;
        private readonly double _desiredStepPixels;
        private readonly int _stepTimeoutMs;
        private double _stepScale = 1.0d;
        private bool _forceSmallIncrement;

        private const int DiscoveryTimeoutMs = 1800;
        private const int DefaultStepTimeoutMs = 15000;
        private static readonly object ExecutorLock = new();
        private static UiaWorkerExecutor _executor = new();

        private sealed class UiaWorkerExecutor
        {
            private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();

            public UiaWorkerExecutor()
            {
                var thread = new Thread(RunLoop)
                {
                    IsBackground = true,
                    Name = "ZSnaper.UiaStaWorker"
                };
                // Isolate WPF UIAutomationClient on a dedicated background STA thread with a message pump
                // so COM proxies never block the WinForms UI thread or stall in non-pumped MTA threads.
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            private void RunLoop()
            {
                while (!_queue.IsCompleted)
                {
                    while (_queue.TryTake(out Action? item))
                    {
                        item();
                        Application.DoEvents();
                    }

                    if (_queue.IsCompleted)
                    {
                        break;
                    }

                    Thread.Sleep(5);
                    Application.DoEvents();
                }
            }

            public bool TryEnqueue(Action item)
            {
                if (_queue.IsAddingCompleted) return false;
                try
                {
                    _queue.Add(item);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            public void Retire() => _queue.CompleteAdding();
        }

        private ScrollPatternController(
            ScrollPattern pattern,
            double viewportPixelHeight,
            double desiredStepPixels,
            int stepTimeoutMs)
        {
            _pattern = pattern;
            _viewportPixelHeight = viewportPixelHeight;
            _desiredStepPixels = desiredStepPixels;
            _stepTimeoutMs = stepTimeoutMs;
        }

        public static ScrollPatternController? TryCreate(
            Point screenPoint,
            int selectionHeight = 400,
            int stepTimeoutMs = DefaultStepTimeoutMs) =>
            RunMtaWithTimeout(
                () => TryCreateCore(screenPoint, selectionHeight, stepTimeoutMs),
                DiscoveryTimeoutMs,
                fallback: null,
                out _);

        private static ScrollPatternController? TryCreateCore(
            Point screenPoint,
            int selectionHeight,
            int stepTimeoutMs)
        {
            try
            {
                AutomationElement? element = AutomationElement.FromPoint(
                    new System.Windows.Point(screenPoint.X, screenPoint.Y));
                TreeWalker walker = TreeWalker.RawViewWalker;

                for (int depth = 0; element is not null && depth < 24; depth++)
                {
                    if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out object? value) &&
                        value is ScrollPattern pattern &&
                        pattern.Current.VerticallyScrollable)
                    {
                        double viewportHeight = element.Current.BoundingRectangle.Height;
                        if (!double.IsNaN(viewportHeight) && !double.IsInfinity(viewportHeight) && viewportHeight > 1d)
                        {
                            double effectiveViewport = selectionHeight > 32
                                ? Math.Min(selectionHeight, viewportHeight)
                                : viewportHeight;
                            double desiredStep = Math.Clamp(effectiveViewport * 0.48d, 36d, 480d);
                            return new ScrollPatternController(pattern, viewportHeight, desiredStep, stepTimeoutMs);
                        }
                    }

                    element = walker.GetParent(element);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }

            return null;
        }

        public ScrollStep ScrollNext()
        {
            ScrollStep step = RunMtaWithTimeout(
                () => ExecuteStep(_desiredStepPixels * _stepScale),
                _stepTimeoutMs,
                fallback: new ScrollStep(false, false, null, AllowWheelFallback: true),
                out bool timedOut);
            return timedOut
                ? new ScrollStep(false, false, null, AllowWheelFallback: true)
                : step;
        }

        public bool TryRestoreAndStepSmaller(double previousPercent, double scale, out ScrollStep retryStep)
        {
            (bool ok, ScrollStep step) result = RunMtaWithTimeout(
                () =>
                {
                    bool restored = TryRestoreAndStepSmallerCore(previousPercent, scale, out ScrollStep innerStep);
                    return (restored, innerStep);
                },
                _stepTimeoutMs,
                fallback: (false, default(ScrollStep)),
                out _);
            retryStep = result.step;
            return result.ok;
        }

        private bool TryRestoreAndStepSmallerCore(double previousPercent, double scale, out ScrollStep retryStep)
        {
            retryStep = default;
            try
            {
                _pattern.SetScrollPercent(ScrollPattern.NoScroll, previousPercent);
                _stepScale = Math.Max(0.25d, _stepScale * scale);
                retryStep = ExecuteStep(_desiredStepPixels * _stepScale);
                return retryStep.Moved;
            }
            catch
            {
                return false;
            }
        }

        private sealed class MtaCallState<T>(T fallback)
        {
            public readonly ManualResetEventSlim Done = new(false);
            public T Result = fallback;
            public Exception? Error;
            private int _disposed;

            public void Complete()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    try { Done.Set(); } catch (ObjectDisposedException) { }
                }
            }

            public void SafeDispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Done.Dispose();
                }
            }
        }

        // UIA COM calls executed directly on an STA UI thread can stall indefinitely on browser/Electron
        // accessibility trees; isolating them on a persistent MTA worker with timeout keeps scrolling responsive.
        private static T RunMtaWithTimeout<T>(Func<T> work, int timeoutMs, T fallback, out bool timedOut)
        {
            UiaWorkerExecutor executor;
            lock (ExecutorLock)
            {
                executor = _executor;
            }

            var call = new MtaCallState<T>(fallback);
            if (!executor.TryEnqueue(() =>
                {
                    try
                    {
                        call.Result = work();
                    }
                    catch (Exception ex)
                    {
                        call.Error = ex;
                    }
                    finally
                    {
                        call.Complete();
                    }
                }))
            {
                call.SafeDispose();
                timedOut = false;
                return fallback;
            }

            if (!call.Done.WaitHandle.WaitOne(timeoutMs))
            {
                lock (ExecutorLock)
                {
                    if (ReferenceEquals(_executor, executor))
                    {
                        executor.Retire();
                        _executor = new UiaWorkerExecutor();
                    }
                }

                timedOut = true;
                return fallback;
            }

            T result = call.Result;
            Exception? error = call.Error;
            call.SafeDispose();

            if (error is not null)
            {
                AppDiagnostics.LogException("ScrollCapture.UiaWorker", error);
                timedOut = false;
                return fallback;
            }

            timedOut = false;
            return result;
        }

        private ScrollStep ExecuteStep(double targetPixels)
        {
            ScrollPattern.ScrollPatternInformation before = _pattern.Current;
            if (!before.VerticallyScrollable || before.VerticalScrollPercent >= 99.95d)
            {
                return new ScrollStep(false, true, null);
            }

            if (!_forceSmallIncrement &&
                before.VerticalViewSize > 0.01d &&
                before.VerticalViewSize < 99.95d)
            {
                double targetPercent = CalculateTargetScrollPercent(
                    before.VerticalScrollPercent,
                    before.VerticalViewSize,
                    _viewportPixelHeight,
                    targetPixels);
                try
                {
                    _pattern.SetScrollPercent(ScrollPattern.NoScroll, targetPercent);
                    ScrollPattern.ScrollPatternInformation after = ReadUpdatedScrollInfo(before.VerticalScrollPercent);
                    double deltaPercent = after.VerticalScrollPercent - before.VerticalScrollPercent;
                    if (deltaPercent >= 0.005d)
                    {
                        int expected = EstimateExpectedPixelShift(deltaPercent, after.VerticalViewSize, _viewportPixelHeight);
                        bool reachedEnd = after.VerticalScrollPercent >= 99.95d;
                        return new ScrollStep(
                            Moved: true,
                            ReachedEnd: reachedEnd,
                            ExpectedPixelShift: expected > 0 ? expected : null,
                            UsedSetScrollPercent: true,
                            PreviousPercent: before.VerticalScrollPercent);
                    }
                }
                catch (InvalidOperationException)
                {
                    _forceSmallIncrement = true;
                }
                catch (COMException)
                {
                    _forceSmallIncrement = true;
                }
            }

            // Fallback within ScrollPattern using small increments (avoiding LargeIncrement which jumps 100%
            // of a browser viewport with zero overlap).
            double startPercent = before.VerticalScrollPercent;
            try
            {
                int accumulatedPx = 0;
                for (int i = 0; i < 4; i++)
                {
                    double stepStartPercent = _pattern.Current.VerticalScrollPercent;
                    _pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                    ScrollPattern.ScrollPatternInformation now = ReadUpdatedScrollInfo(stepStartPercent);
                    double deltaPercent = Math.Max(0d, now.VerticalScrollPercent - startPercent);
                    accumulatedPx = EstimateExpectedPixelShift(deltaPercent, now.VerticalViewSize, _viewportPixelHeight);
                    if (now.VerticalScrollPercent >= 99.95d)
                    {
                        return new ScrollStep(
                            deltaPercent >= 0.005d,
                            true,
                            accumulatedPx > 0 ? accumulatedPx : null,
                            AllowWheelFallback: deltaPercent < 0.005d);
                    }

                    if (accumulatedPx >= targetPixels * 0.6d)
                    {
                        return new ScrollStep(true, false, accumulatedPx > 0 ? accumulatedPx : null);
                    }

                    if (deltaPercent < 0.001d && i == 0)
                    {
                        return new ScrollStep(false, false, null, AllowWheelFallback: true);
                    }
                }

                ScrollPattern.ScrollPatternInformation finalState = _pattern.Current;
                double totalDelta = Math.Max(0d, finalState.VerticalScrollPercent - startPercent);
                return totalDelta >= 0.005d
                    ? new ScrollStep(true, finalState.VerticalScrollPercent >= 99.95d, accumulatedPx > 0 ? accumulatedPx : null)
                    : new ScrollStep(false, false, null, AllowWheelFallback: true);
            }
            catch (InvalidOperationException)
            {
                return new ScrollStep(false, false, null, AllowWheelFallback: true);
            }
            catch (COMException)
            {
                return new ScrollStep(false, false, null, AllowWheelFallback: true);
            }
        }

        private ScrollPattern.ScrollPatternInformation ReadUpdatedScrollInfo(double previousPercent)
        {
            ScrollPattern.ScrollPatternInformation info = _pattern.Current;
            for (int attempt = 0; attempt < 4 && Math.Abs(info.VerticalScrollPercent - previousPercent) < 0.005d; attempt++)
            {
                Thread.Sleep(25);
                info = _pattern.Current;
            }

            return info;
        }
    }

    internal sealed class WheelScrollController : IDisposable
    {
        private readonly Point _scrollPoint;
        private readonly nint _rootWindow;
        private readonly nint _childWindow;
        private readonly NativeMethods.POINT _originalPointer;
        private readonly bool _restorePointer;
        private int _currentWheelDelta;
        private int _lastSentDelta;
        private bool _hoverWarmed;

        private WheelScrollController(
            Point scrollPoint,
            nint rootWindow,
            nint childWindow,
            int initialWheelDelta)
        {
            _scrollPoint = scrollPoint;
            _rootWindow = rootWindow;
            _childWindow = childWindow;
            _currentWheelDelta = initialWheelDelta;
            _restorePointer = NativeMethods.GetCursorPos(out _originalPointer);
        }

        public static WheelScrollController? TryCreate(
            Point screenPoint,
            int selectionHeight = 400,
            IReadOnlySet<nint>? ignoredWindows = null)
        {
            (nint root, nint child) = NativeMethods.FindTargetWindowAtPoint(screenPoint, ignoredWindows);
            if (root == nint.Zero)
            {
                var pt = new NativeMethods.POINT { X = screenPoint.X, Y = screenPoint.Y };
                nint fallbackTarget = NativeMethods.WindowFromPoint(pt);
                if (fallbackTarget == nint.Zero)
                {
                    return null;
                }

                root = NativeMethods.GetAncestor(fallbackTarget, NativeMethods.GA_ROOT);
                child = fallbackTarget;
                if (root == nint.Zero)
                {
                    root = fallbackTarget;
                }
            }

            // Full single-notch initial delta ensures native and Chromium controls scroll cleanly on probe step 1.
            int initialDelta = selectionHeight < 460 ? -120 : -240;

            return new WheelScrollController(screenPoint, root, child, initialDelta);
        }

        internal bool IsChromiumHost
        {
            get
            {
                string childClass = ReadClassName(_childWindow);
                if (childClass.Equals("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal) ||
                    childClass.Equals("CefBrowserWindow", StringComparison.Ordinal))
                {
                    return true;
                }

                string rootClass = ReadClassName(_rootWindow);
                return rootClass.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
            }
        }

        private static string ReadClassName(nint hwnd)
        {
            if (hwnd == nint.Zero) return string.Empty;
            var sb = new System.Text.StringBuilder(128);
            int len = NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        public ScrollStep ScrollNext()
        {
            if (_rootWindow != nint.Zero && NativeMethods.GetForegroundWindow() != _rootWindow)
            {
                NativeMethods.SetForegroundWindow(_rootWindow);
            }

            if (!_hoverWarmed)
            {
                nint hoverTarget = _childWindow != nint.Zero ? _childWindow : _rootWindow;
                if (hoverTarget != nint.Zero)
                {
                    NativeMethods.WarmMouseHover(hoverTarget, _scrollPoint);
                }

                _hoverWarmed = true;
            }

            NativeMethods.SetCursorPos(_scrollPoint.X, _scrollPoint.Y);
            _lastSentDelta = _currentWheelDelta;
            if (!NativeMethods.SendMouseWheelInput(_currentWheelDelta))
            {
                SendPostMessageWheel();
            }

            return new ScrollStep(
                Moved: true,
                ReachedEnd: false,
                ExpectedPixelShift: null,
                IsSmoothWheel: true,
                AllowPostMessageRetry: true);
        }

        public bool SendPostMessageWheel()
        {
            nint target = _childWindow != nint.Zero ? _childWindow : _rootWindow;
            if (target == nint.Zero)
            {
                return false;
            }

            int delta = _lastSentDelta != 0 ? _lastSentDelta : -120;
            // For small sub-notch initial probes that native controls ignore, upgrade to at least 1 notch.
            if (Math.Abs(delta) < 120)
            {
                delta = -120;
                _currentWheelDelta = -120;
            }

            if (!_hoverWarmed)
            {
                // Chromium/Electron ignore WM_MOUSEWHEEL until a prior WM_MOUSEMOVE reaches the renderer.
                NativeMethods.WarmMouseHover(target, _scrollPoint);
                Thread.Sleep(35);
                _hoverWarmed = true;
            }

            nint wParam = (nint)((delta & 0xFFFF) << 16);
            return NativeMethods.PostMouseWheelAtScreenPoint(target, _scrollPoint, wParam);
        }

        public void ReportObservedShift(int actualShift, int selectionHeight)
        {
            if (actualShift <= 0 || _lastSentDelta == 0)
            {
                return;
            }

            int desiredShift = Math.Clamp((int)(selectionHeight * 0.45d), 40, 420);
            double ratio = desiredShift / (double)actualShift;
            int scaled = (int)Math.Round(Math.Abs(_lastSentDelta) * Math.Clamp(ratio, 0.45d, 2.0d));
            int clamped = Math.Clamp(scaled, 60, 600);
            _currentWheelDelta = -clamped;
        }

        public void Dispose()
        {
            if (_restorePointer)
            {
                NativeMethods.SetCursorPos(_originalPointer.X, _originalPointer.Y);
            }
        }
    }
}
