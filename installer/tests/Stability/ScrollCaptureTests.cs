using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using ZSnaper.Forms;
using ZSnaper.Interop;
using ZSnaper.Services;

namespace ZSnaper.Stability;

internal static partial class Program
{
    private static void TestScrollCapture()
    {
        TestStandardManualStitch();
        TestSparseWebpageDetectionAndStitch();
        TestStickyHeaderAndFooterStitch();
        TestHoverTintVsActualScroll();
        TestSmoothScrollPendingAndUpwardScroll();
        TestNoOverlapRejection();
        TestScrollPatternPercentMath();
        TestScrollCaptureFormHoleAndActivation();
    }

    private static void TestStandardManualStitch()
    {
        using Bitmap document = CreateRichDocumentBitmap(width: 520, height: 1400, startSeed: 1);
        int viewH = 420;
        using Bitmap f0 = CropRegion(document, 0, viewH);
        using Bitmap f1 = CropRegion(document, 130, viewH);
        using Bitmap f2 = CropRegion(document, 290, viewH);
        using Bitmap f3 = CropRegion(document, 470, viewH);

        using var assembler = new ScrollCaptureAssembler(f0);
        Assert(assembler.AddFrame(f1) == ScrollCaptureFrameStatus.Added && assembler.LastShift == 130,
            $"Expected 130px shift on frame 1, got {assembler.LastShift}.");
        Assert(assembler.AddFrame(f2) == ScrollCaptureFrameStatus.Added && assembler.LastShift == 160,
            $"Expected 160px shift on frame 2, got {assembler.LastShift}.");
        Assert(assembler.AddFrame(f3) == ScrollCaptureFrameStatus.Added && assembler.LastShift == 180,
            $"Expected 180px shift on frame 3, got {assembler.LastShift}.");

        using Bitmap stitched = assembler.BuildImage();
        Assert(stitched.Height == viewH + 470, $"Unexpected stitched height {stitched.Height}.");
        AssertBitmapRowsMatch(document, stitched, width: 520, height: stitched.Height);
    }

    private static void TestSparseWebpageDetectionAndStitch()
    {
        // Mostly blank white canvas with sparse dark paragraphs + 1px hairline separator.
        using var document = new Bitmap(640, 1200, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(document))
        {
            g.Clear(Color.White);
            using var darkBrush = new SolidBrush(Color.FromArgb(30, 34, 42));
            using var accentBrush = new SolidBrush(Color.FromArgb(42, 120, 210));
            using var pen = new Pen(Color.FromArgb(200, 206, 214), 1f);
            for (int y = 90; y < 1100; y += 220)
            {
                g.FillRectangle(darkBrush, 80, y, 220, 14);
                g.FillRectangle(accentBrush, 80, y + 24, 160, 10);
                g.DrawLine(pen, 80, y + 48, 520, y + 48);
            }
        }

        int viewH = 440;
        using Bitmap f0 = CropRegion(document, 0, viewH);
        using Bitmap f1 = CropRegion(document, 72, viewH);
        using Bitmap f2 = CropRegion(document, 156, viewH);

        Assert(!ScrollCaptureService.AreVisuallySame(f0, f1),
            "Sparse white page scroll must not be mistaken for identical frame.");

        using var assembler = new ScrollCaptureAssembler(f0);
        Assert(assembler.AddFrame(f1) == ScrollCaptureFrameStatus.Added &&
               Math.Abs(assembler.LastShift - 72) <= 1,
            $"Sparse page failed manual shift detection: {assembler.LastShift}.");
        Assert(assembler.AddFrame(f2) == ScrollCaptureFrameStatus.Added &&
               Math.Abs(assembler.LastShift - 84) <= 1,
            $"Sparse page second frame failed detection: {assembler.LastShift}.");
    }

    private static void TestStickyHeaderAndFooterStitch()
    {
        int width = 560;
        int viewH = 460;
        int headerH = 60;
        int footerH = 46;
        int scrollableH = viewH - headerH - footerH;

        using Bitmap body = CreateRichDocumentBitmap(width, 1300, startSeed: 7);
        using Bitmap header = CreateBannerBitmap(width, headerH, Color.FromArgb(22, 32, 48), Color.FromArgb(90, 195, 255));
        using Bitmap footer = CreateBannerBitmap(width, footerH, Color.FromArgb(34, 28, 42), Color.FromArgb(255, 190, 92));

        Bitmap ComposeViewport(int scrollOffset)
        {
            var bmp = new Bitmap(width, viewH, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.DrawImageUnscaled(header, 0, 0);
            g.DrawImage(body,
                new Rectangle(0, headerH, width, scrollableH),
                new Rectangle(0, scrollOffset, width, scrollableH),
                GraphicsUnit.Pixel);
            g.DrawImageUnscaled(footer, 0, viewH - footerH);
            return bmp;
        }

        using Bitmap f0 = ComposeViewport(0);
        using Bitmap f1 = ComposeViewport(140);
        using Bitmap f2 = ComposeViewport(290);

        using var assembler = new ScrollCaptureAssembler(f0);
        Assert(assembler.AddFrame(f1) == ScrollCaptureFrameStatus.Added &&
               Math.Abs(assembler.LastShift - 140) <= 1,
            $"Sticky header/footer shift 1 expected ~140, got {assembler.LastShift}.");
        Assert(assembler.AddFrame(f2) == ScrollCaptureFrameStatus.Added &&
               Math.Abs(assembler.LastShift - 150) <= 1,
            $"Sticky header/footer shift 2 expected ~150, got {assembler.LastShift}.");

        using Bitmap stitched = assembler.BuildImage();
        Assert(stitched.Height == viewH + 290,
            $"Expected sticky stitched height {viewH + 290}, got {stitched.Height}.");

        // Header appears once at top; footer appears once at bottom; middle matches body continuously.
        AssertBitmapRegionMatches(header, stitched, 0, 0, width, headerH);
        AssertBitmapRegionMatches(body, stitched, srcY: 0, dstY: headerH, width, scrollableH + 290);
        AssertBitmapRegionMatches(footer, stitched, srcY: 0, dstY: stitched.Height - footerH, width, footerH);
    }

    private static void TestHoverTintVsActualScroll()
    {
        using Bitmap document = CreateRichDocumentBitmap(width: 520, height: 1100, startSeed: 3);
        int viewH = 440;
        using Bitmap f0 = CropRegion(document, 0, viewH);

        // Hovered card without scrolling must be treated as Unchanged (not NoOverlap warning).
        using Bitmap hoveredOnly = (Bitmap)f0.Clone();
        using (Graphics g = Graphics.FromImage(hoveredOnly))
        using (var hoverBrush = new SolidBrush(Color.FromArgb(95, 64, 156, 255)))
        {
            g.FillRectangle(hoverBrush, 60, 180, 380, 44);
        }

        using var hoverAssembler = new ScrollCaptureAssembler(f0);
        ScrollCaptureFrameStatus hoverStatus = hoverAssembler.AddFrame(hoveredOnly);
        Assert(hoverStatus == ScrollCaptureFrameStatus.Unchanged,
            $"In-place hover card must be Unchanged, got {hoverStatus}.");

        // Scrolled 135px PLUS mouse hovered a 36px link box inside the scrolled viewport.
        using Bitmap scrolledWithHover = CropRegion(document, 135, viewH);
        using (Graphics g = Graphics.FromImage(scrolledWithHover))
        using (var hoverBrush = new SolidBrush(Color.FromArgb(90, 255, 220, 80)))
        {
            g.FillRectangle(hoverBrush, 70, 210, 340, 32);
        }

        using var scrollAssembler = new ScrollCaptureAssembler(f0);
        ScrollCaptureFrameStatus scrollStatus = scrollAssembler.AddFrame(scrolledWithHover);
        Assert(scrollStatus == ScrollCaptureFrameStatus.Added &&
               Math.Abs(scrollAssembler.LastShift - 135) <= 1,
            $"Scroll with hover tint expected ~135, got {scrollStatus} / {scrollAssembler.LastShift}.");
    }

    private static void TestSmoothScrollPendingAndUpwardScroll()
    {
        using Bitmap document = CreateRichDocumentBitmap(width: 500, height: 1200, startSeed: 5);
        int viewH = 400;
        using Bitmap f0 = CropRegion(document, 60, viewH);
        using Bitmap smooth3Px = CropRegion(document, 63, viewH);
        using Bitmap committed42Px = CropRegion(document, 102, viewH);
        using Bitmap scrolledBackUp = CropRegion(document, 62, viewH);

        using var assembler = new ScrollCaptureAssembler(f0);
        ScrollCaptureFrameStatus pending = assembler.AddFrame(smooth3Px);
        Assert(pending == ScrollCaptureFrameStatus.Pending,
            $"Expected Pending for tiny 3px smooth scroll, got {pending}.");
        Assert(assembler.FrameCount == 1, "Pending frame must not increment FrameCount.");

        ScrollCaptureFrameStatus added = assembler.AddFrame(committed42Px);
        Assert(added == ScrollCaptureFrameStatus.Added && Math.Abs(assembler.LastShift - 42) <= 1,
            $"Expected 42px stitch after Pending wait, got {added} / {assembler.LastShift}.");

        ScrollCaptureFrameStatus upStatus = assembler.AddFrame(scrolledBackUp);
        Assert(upStatus == ScrollCaptureFrameStatus.ScrolledUp,
            $"Expected ScrolledUp when user scrolled upward, got {upStatus}.");
    }

    private static void TestNoOverlapRejection()
    {
        using Bitmap docA = CreateRichDocumentBitmap(width: 480, height: 500, startSeed: 11);
        using Bitmap docB = CreateRichDocumentBitmap(width: 480, height: 500, startSeed: 99);
        using Bitmap f0 = CropRegion(docA, 0, 360);
        using Bitmap fDisjoint = CropRegion(docB, 0, 360);

        using var assembler = new ScrollCaptureAssembler(f0);
        Assert(assembler.AddFrame(fDisjoint) == ScrollCaptureFrameStatus.NoOverlap,
            "Disjoint frame should return NoOverlap.");
    }

    private static void TestScrollPatternPercentMath()
    {
        // Chromium observed live metrics: V=605px, ViewSize=10.21959% (total ~5920px).
        double target = ScrollCaptureService.CalculateTargetScrollPercent(0d, 10.21959d, 605d, 290d);
        int expectedPx = ScrollCaptureService.EstimateExpectedPixelShift(target, 10.21959d, 605d);
        Assert(target > 4d && target < 7d && Math.Abs(expectedPx - 290) <= 1,
            $"Chromium percent math mismatch: target={target:F4}%, expectedPx={expectedPx}.");

        // Inner overflow container: V=240px, ViewSize=25% (total=960px), step=100px.
        double innerTarget = ScrollCaptureService.CalculateTargetScrollPercent(10d, 25d, 240d, 100d);
        int innerPx = ScrollCaptureService.EstimateExpectedPixelShift(innerTarget - 10d, 25d, 240d);
        Assert(Math.Abs(innerPx - 100) <= 1,
            $"Inner scroller percent math mismatch: innerTarget={innerTarget:F4}%, innerPx={innerPx}.");
    }

    private static void TestScrollCaptureFormHoleAndActivation()
    {
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Rectangle selectionScreen = new(
            virtualScreen.Left + 80,
            virtualScreen.Top + 70,
            260,
            200);
        using Bitmap bg = new(virtualScreen.Width, virtualScreen.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bg))
        {
            g.Clear(Color.FromArgb(45, 55, 65));
        }
        using Bitmap initial = new(selectionScreen.Width, selectionScreen.Height, PixelFormat.Format32bppArgb);

        using var form = new ScrollCaptureForm(selectionScreen, bg, initial);
        _ = form.Handle;

        const int wsExNoActivate = 0x08000000;
        const int wsExToolWindow = 0x00000080;
        int exStyle = NativeMethods.GetWindowLong(form.Handle, NativeMethods.GWL_EXSTYLE);
        Assert((exStyle & wsExNoActivate) != 0 && (exStyle & wsExToolWindow) != 0,
            $"ScrollCaptureForm missing WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW (0x{exStyle:X}).");

        // Verify WM_MOUSEACTIVATE does not steal foreground focus from target app.
        nint activateResult = NativeMethods.SendMessage(form.Handle, NativeMethods.WM_MOUSEACTIVATE, 0, 0);
        Assert(activateResult == (nint)NativeMethods.MA_NOACTIVATE,
            $"Expected MA_NOACTIVATE (3), got {activateResult}.");

        // Verify OS window Region physically punches out _selection while preserving the toolbar,
        // and selection border AA pixels stay outside the TransparentColor hole.
        Rectangle hole = form.SelectionBoundsInClient;
        Rectangle toolbar = form.ToolbarBoundsInClient;
        Assert(form.Region is not null &&
               !form.Region.IsVisible(new Point(hole.Left + hole.Width / 2, hole.Top + hole.Height / 2)) &&
               form.Region.IsVisible(new Point(toolbar.Left + toolbar.Width / 2, toolbar.Top + toolbar.Height / 2)),
            $"ScrollCaptureForm Region must exclude selection hole while keeping toolbar solid (hole={hole}, toolbar={toolbar}).");

        using Bitmap rendered = new(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb);
        form.DrawToBitmap(rendered, new Rectangle(Point.Empty, form.ClientSize));
        Color topEdgeInside = rendered.GetPixel(hole.Left + hole.Width / 2, hole.Top + 1);
        Color leftEdgeInside = rendered.GetPixel(hole.Left + 1, hole.Top + hole.Height / 2);
        Color rightEdgeInside = rendered.GetPixel(hole.Right - 1, hole.Top + hole.Height / 2);
        Assert(topEdgeInside.ToArgb() == Color.FromArgb(1, 2, 3).ToArgb() &&
               leftEdgeInside.ToArgb() == Color.FromArgb(1, 2, 3).ToArgb() &&
               rightEdgeInside.ToArgb() == Color.FromArgb(1, 2, 3).ToArgb(),
            $"Selection border bled into TransparentColor hole: top={topEdgeInside}, left={leftEdgeInside}, right={rightEdgeInside}.");

        // Verify ignored handles exclude other open ZSnaper forms cleanly and BeginMode never blocks on UIA.
        using var dummyOwner = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(40, 40)
        };
        dummyOwner.Show();
        HashSet<nint> ignored = form.BuildIgnoredWindowHandles();
        Assert(ignored.Contains(form.Handle) && ignored.Contains(dummyOwner.Handle),
            "BuildIgnoredWindowHandles must exclude both ScrollCaptureForm and open application forms.");
        var beginWatch = Stopwatch.StartNew();
        form.BeginMode();
        beginWatch.Stop();
        Assert(beginWatch.ElapsedMilliseconds < 1000,
            $"BeginMode blocked UI thread ({beginWatch.ElapsedMilliseconds}ms); UIA must never run synchronously on startup.");
    }

    private static Bitmap CreateRichDocumentBitmap(int width, int height, int startSeed)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(248, 249, 252));
        for (int y = 0; y < height; y += 22)
        {
            int idx = y / 22 + startSeed;
            Color band = Color.FromArgb(245 - (idx * 7 % 20), 246 - (idx * 5 % 20), 250 - (idx * 11 % 20));
            using var rowBg = new SolidBrush(band);
            g.FillRectangle(rowBg, 0, y, width, 22);

            int barW = 90 + (idx * 37 % Math.Max(40, width - 160));
            Color barColor = Color.FromArgb(30 + (idx * 41 % 160), 50 + (idx * 29 % 140), 90 + (idx * 53 % 140));
            using var barBrush = new SolidBrush(barColor);
            g.FillRectangle(barBrush, 24 + (idx * 13 % 30), y + 5, barW, 11);

            if (idx % 3 == 0)
            {
                using var badgeBrush = new SolidBrush(Color.FromArgb(220, 90, 70));
                g.FillRectangle(badgeBrush, width - 86, y + 4, 48, 12);
            }
        }

        return bmp;
    }

    private static Bitmap CreateBannerBitmap(int width, int height, Color backColor, Color accentColor)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(backColor);
        using var accent = new SolidBrush(accentColor);
        g.FillRectangle(accent, 18, 10, 120, Math.Max(10, height - 20));
        g.FillRectangle(accent, width - 90, 12, 60, Math.Max(8, height - 24));
        return bmp;
    }

    private static Bitmap CropRegion(Bitmap source, int topY, int height) =>
        source.Clone(new Rectangle(0, topY, source.Width, height), PixelFormat.Format32bppArgb);

    private static void AssertBitmapRowsMatch(Bitmap expected, Bitmap actual, int width, int height) =>
        AssertBitmapRegionMatches(expected, actual, srcY: 0, dstY: 0, width, height);

    private static void AssertBitmapRegionMatches(Bitmap expected, Bitmap actual, int srcY, int dstY, int width, int height)
    {
        for (int y = 0; y < height; y += 3)
        {
            for (int x = 16; x < width - 16; x += 24)
            {
                Color a = expected.GetPixel(x, srcY + y);
                Color b = actual.GetPixel(x, dstY + y);
                int diff = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                Assert(diff <= 3,
                    $"Pixel mismatch at x={x}, srcY={srcY + y}, dstY={dstY + y}: expected={a}, actual={b}.");
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    private static void TestBrowserScrollProbe()
    {
        string? browserExe = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        }.FirstOrDefault(File.Exists);
        Assert(browserExe is not null, "No Chrome/Edge installation found for browser probe.");

        string tempRoot = Path.Combine(Path.GetTempPath(), "ZSnaper-BrowserProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string htmlPath = Path.Combine(tempRoot, "scroll_probe.html");
        string profileDir = Path.Combine(tempRoot, "profile");

        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html><head><meta charset='utf-8'><title>ZSnaperScrollProbe</title><style>
body{margin:0;font:16px/1.6 sans-serif;}
header{position:sticky;top:0;height:56px;background:#1d263b;color:#fff;padding:14px 20px;box-sizing:border-box;z-index:9;}
#inner{margin:20px;height:220px;overflow-y:auto;border:2px solid #4a6fa5;padding:12px;background:#f7f9fc;}
.row{height:48px;border-bottom:1px solid #d8dee9;display:flex;align-items:center;}
</style></head><body>
<header>ZSnaper Sticky Header</header>
<div id='inner'>
""");
        for (int i = 1; i <= 40; i++)
        {
            sb.Append($"<div class='row'>Inner scrollable item #{i}</div>\n");
        }
        sb.Append("</div>\n");
        for (int i = 1; i <= 90; i++)
        {
            sb.Append($"<div class='row'>Main document item #{i} - browser auto and manual wheel verification</div>\n");
        }
        sb.Append("</body></html>");
        File.WriteAllText(htmlPath, sb.ToString());

        Process? proc = null;
        uint browserWindowPid = 0;
        try
        {
            string url = new Uri(htmlPath).AbsoluteUri;
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = browserExe!,
                Arguments = $"--user-data-dir=\"{profileDir}\" --no-first-run --no-default-browser-check --window-position=60,60 --window-size=900,720 --app=\"{url}\"",
                UseShellExecute = false
            });
            Assert(proc is not null, "Failed to launch browser.");

            nint browserHwnd = nint.Zero;
            NativeMethods.RECT winRect = default;
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && browserHwnd == nint.Zero)
            {
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
                    var titleBuf = new StringBuilder(256);
                    if (GetWindowText(hwnd, titleBuf, titleBuf.Capacity) <= 0) return true;
                    if (!titleBuf.ToString().Contains("ZSnaperScrollProbe", StringComparison.Ordinal)) return true;
                    if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT candidate) &&
                        candidate.Right - candidate.Left >= 600 &&
                        candidate.Bottom - candidate.Top >= 500)
                    {
                        browserHwnd = hwnd;
                        winRect = candidate;
                        NativeMethods.GetWindowThreadProcessId(hwnd, out browserWindowPid);
                        return false;
                    }
                    return true;
                }, nint.Zero);

                if (browserHwnd == nint.Zero) Thread.Sleep(200);
            }

            Assert(browserHwnd != nint.Zero, "Timed out waiting for ZSnaperScrollProbe browser window.");
            Thread.Sleep(450);
            NativeMethods.GetWindowRect(browserHwnd, out winRect);
            Rectangle browserBounds = winRect.ToRectangle();
            Point innerPt = new(browserBounds.Left + 260, browserBounds.Top + 210);
            Point docPt = new(browserBounds.Left + 260, browserBounds.Top + 480);

            (nint foundRoot, nint foundChild) = NativeMethods.FindTargetWindowAtPoint(docPt);
            var childCls = new StringBuilder(128);
            if (foundChild != nint.Zero) NativeMethods.GetClassName(foundChild, childCls, childCls.Capacity);
            Console.WriteLine($"TargetWindow root=0x{foundRoot:X}, child=0x{foundChild:X} ({childCls}), expected=0x{browserHwnd:X}, bounds={browserBounds}");
            Assert(foundRoot == browserHwnd && foundChild != nint.Zero && foundChild != browserHwnd,
                $"FindTargetWindowAtPoint mismatch over browser (root=0x{foundRoot:X}, child=0x{foundChild:X}).");

            // Verify production CreateScroller selects immediate non-blocking Chromium scroller without UIA freeze.
            var createSw = Stopwatch.StartNew();
            using ScrollCaptureService.Scroller? fastScroller = ScrollCaptureService.CreateScroller(docPt, selectionHeight: 450);
            createSw.Stop();
            Console.WriteLine($"CreateScroller on Chromium finished in {createSw.ElapsedMilliseconds}ms.");
            Assert(fastScroller is not null && createSw.ElapsedMilliseconds < 400,
                $"CreateScroller over Chromium took {createSw.ElapsedMilliseconds}ms or returned null.");

            // Wake Chromium UIA tree and locate ScrollPattern on inner container and document
            // via MTA worker (matching OverlayForm / ContinueCaptureAsync runtime threading).
            ScrollCaptureService.ScrollPatternController? innerScroller = null;
            ScrollCaptureService.ScrollPatternController? docScroller = null;
            DateTime uiaDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < uiaDeadline && (innerScroller is null || docScroller is null))
            {
                try
                {
                    Task.Run(() =>
                    {
                        AutomationElement? rootAe = AutomationElement.FromHandle(foundRoot);
                        _ = rootAe?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    }).Wait(1500);
                }
                catch { }

                innerScroller ??= ScrollCaptureService.ScrollPatternController.TryCreate(innerPt, selectionHeight: 200);
                docScroller ??= ScrollCaptureService.ScrollPatternController.TryCreate(docPt, selectionHeight: 450);
                if (innerScroller is null || docScroller is null)
                {
                    Thread.Sleep(250);
                }
            }

            Assert(innerScroller is not null, "Inner overflow container ScrollPattern was not found.");
            Assert(docScroller is not null, "Document ScrollPattern was not found.");

            // Verify proportional percent steps keep strong overlap instead of jumping 100%.
            var innerSw = Stopwatch.StartNew();
            ScrollCaptureService.ScrollStep innerStep = innerScroller!.ScrollNext();
            Console.WriteLine($"Inner step ({innerSw.ElapsedMilliseconds}ms): moved={innerStep.Moved}, expectedPx={innerStep.ExpectedPixelShift}, allowWheel={innerStep.AllowWheelFallback}");
            Assert(innerStep.Moved && innerStep.ExpectedPixelShift is > 30 and < 180,
                $"Inner overflow ScrollPattern step abnormal: moved={innerStep.Moved}, px={innerStep.ExpectedPixelShift}.");

            ScrollCaptureService.ScrollStep docStep1 = docScroller!.ScrollNext();
            ScrollCaptureService.ScrollStep docStep2 = docScroller.ScrollNext();
            Console.WriteLine($"Doc step1: moved={docStep1.Moved}, expectedPx={docStep1.ExpectedPixelShift}; step2: moved={docStep2.Moved}, expectedPx={docStep2.ExpectedPixelShift}");
            Assert(docStep1.Moved && docStep1.ExpectedPixelShift is > 80 and < 320,
                $"Document ScrollPattern step1 overshot or failed: moved={docStep1.Moved}, px={docStep1.ExpectedPixelShift}.");
            Assert(docStep2.Moved && docStep2.ExpectedPixelShift is > 80 and < 320,
                $"Document ScrollPattern step2 overshot or failed: moved={docStep2.Moved}, px={docStep2.ExpectedPixelShift}.");

            // Also verify wheel methods advance Chromium Document.
            Console.WriteLine($"Testing wheel against browserExe={browserExe}");
            using var wheel = ScrollCaptureService.WheelScrollController.TryCreate(docPt, selectionHeight: 450);
            Assert(wheel is not null, "WheelScrollController.TryCreate failed over browser.");
            AutomationElement docAe = AutomationElement.FromHandle(foundRoot)!
                .FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))!;
            var docPattern = (ScrollPattern)docAe.GetCurrentPattern(ScrollPattern.Pattern);

            double beforePct = docPattern.Current.VerticalScrollPercent;
            bool pmPosted = wheel!.SendPostMessageWheel();
            Thread.Sleep(450);
            double afterPm = docPattern.Current.VerticalScrollPercent;
            Console.WriteLine($"WheelScrollController.SendPostMessageWheel: posted={pmPosted}, pct={beforePct:F2}% -> {afterPm:F2}%");
            Assert(pmPosted && afterPm > beforePct + 0.1d,
                $"SendPostMessageWheel did not scroll browser document ({beforePct:F2}% -> {afterPm:F2}%).");

            // Also verify wheel.ScrollNext() (SetForegroundWindow + SetCursorPos + SendInput).
            wheel.ScrollNext();
            Thread.Sleep(500);
            double afterSendInput = docPattern.Current.VerticalScrollPercent;
            Console.WriteLine($"WheelScrollController.ScrollNext (SendInput): pct={afterPm:F2}% -> {afterSendInput:F2}%");
            Assert(afterSendInput > afterPm + 0.1d,
                $"WheelScrollController.ScrollNext (SendInput) did not scroll browser document ({afterPm:F2}% -> {afterSendInput:F2}%).");

            // Verify live ScrollCaptureForm sitting TopMost over Chrome allows wheel scrolling inside selection hole.
            docPattern.SetScrollPercent(ScrollPattern.NoScroll, 15d);
            Thread.Sleep(350);
            Rectangle selScreen = new(browserBounds.Left + 80, browserBounds.Top + 310, 520, 320);
            Rectangle vs = SystemInformation.VirtualScreen;
            using var bg = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
            using var init = new Bitmap(selScreen.Width, selScreen.Height, PixelFormat.Format32bppArgb);
            using var form = new ScrollCaptureForm(selScreen, bg, init);
            form.BeginMode();
            for (int i = 0; i < 15; i++)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            Point center = form.SelectionCenter;
            (nint resRoot, nint resChild) = form.ResolveTargetWindow();
            Assert(resRoot == browserHwnd && resChild != nint.Zero,
                $"ScrollCaptureForm.ResolveTargetWindow failed over live browser (root=0x{resRoot:X}, child=0x{resChild:X}).");

            double beforeOverlayPct = docPattern.Current.VerticalScrollPercent;
            NativeMethods.WarmMouseHover(resChild, center);
            Thread.Sleep(45);
            NativeMethods.PostMouseWheelAtScreenPoint(resChild, center, unchecked((nint)(int)(((-120) & 0xFFFF) << 16)), warmHover: false);
            for (int i = 0; i < 20; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            double afterOverlayPct = docPattern.Current.VerticalScrollPercent;
            Console.WriteLine($"ScrollCaptureForm overlay wheel: pct={beforeOverlayPct:F2}% -> {afterOverlayPct:F2}%");
            Assert(afterOverlayPct > beforeOverlayPct + 0.5d,
                $"Chrome document failed to scroll while ScrollCaptureForm overlay was open ({beforeOverlayPct:F2}% -> {afterOverlayPct:F2}%).");

            form.CancelFromOwner();
            Application.DoEvents();

            Console.WriteLine("Browser scroll probe passed.");
        }
        finally
        {
            if (browserWindowPid != 0)
            {
                try
                {
                    using Process owner = Process.GetProcessById((int)browserWindowPid);
                    owner.Kill(entireProcessTree: true);
                    owner.WaitForExit(3000);
                }
                catch { }
            }
            try
            {
                if (proc is { HasExited: false })
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
            catch { }
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
