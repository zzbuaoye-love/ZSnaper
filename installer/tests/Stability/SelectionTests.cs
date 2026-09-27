using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ZSnaper.Forms;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Stability;

internal static partial class Program
{
    private static object GetOverlayField(OverlayForm overlay, string name) =>
        typeof(OverlayForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;

    private static void SetOverlayEnum(OverlayForm overlay, string field, string value)
    {
        Type type = typeof(OverlayForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        SetOverlayField(overlay, field, Enum.Parse(type, value));
    }

    private static void SelectRectangle(OverlayForm overlay, Rectangle bounds)
    {
        SetOverlayField(overlay, "_selection", bounds);
        SetOverlayField(overlay, "_hasSelection", bounds.Width >= 2 && bounds.Height >= 2);
    }

    private static MouseEventArgs LeftMouse(Point point) => new(MouseButtons.Left, 1, point.X, point.Y, 0);

    private static void TestSmartCandidateStability()
    {
        using var overlay = new OverlayFormProbe { ClientSize = new Size(800, 600) };
        _ = overlay.Handle;
        MethodInfo offer = typeof(OverlayForm).GetMethod("OfferSmartTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo commit = typeof(OverlayForm).GetMethod("TryCommitPendingSmartTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SmartSelectionTarget targetA = new(1, overlay.RectangleToScreen(new Rectangle(100, 100, 160, 180)), "A");
        SmartSelectionTarget targetB = new(1, overlay.RectangleToScreen(new Rectangle(300, 100, 160, 180)), "B");
        Point onB = overlay.PointToScreen(new Point(330, 140));
        SetOverlayField(overlay, "_smartTarget", targetA);
        SetOverlayField(overlay, "_smartFastTarget", targetB);
        SetOverlayField(overlay, "_pendingSmartPoint", new Point(330, 140));
        offer.Invoke(overlay, [targetB, onB, false]);
        Assert((SmartSelectionTarget)GetOverlayField(overlay, "_smartTarget") == targetA,
            "Candidate switched before the hover settled.");
        long started = (long)GetOverlayField(overlay, "_smartPendingSince");
        offer.Invoke(overlay, [targetB, onB, false]);
        Assert((long)GetOverlayField(overlay, "_smartPendingSince") == started,
            "Repeated samples reset the settling interval.");
        commit.Invoke(overlay, [started + 89]);
        Assert((SmartSelectionTarget)GetOverlayField(overlay, "_smartTarget") == targetA,
            "Candidate switched before the 90 ms interval.");
        commit.Invoke(overlay, [started + 90]);
        Assert((SmartSelectionTarget)GetOverlayField(overlay, "_smartTarget") == targetB,
            "Stable candidate did not become visible.");

        Point onA = overlay.PointToScreen(new Point(130, 140));
        SetOverlayField(overlay, "_smartFastTarget", targetA);
        SetOverlayField(overlay, "_pendingSmartPoint", new Point(130, 140));
        offer.Invoke(overlay, [targetA, onA, false]);
        started = (long)GetOverlayField(overlay, "_smartPendingSince");
        SetOverlayField(overlay, "_smartFastTarget", targetB);
        SetOverlayField(overlay, "_pendingSmartPoint", new Point(330, 140));
        offer.Invoke(overlay, [targetB, onB, false]);
        commit.Invoke(overlay, [started + 100]);
        Assert((SmartSelectionTarget)GetOverlayField(overlay, "_smartTarget") == targetB,
            "A brief visit to a neighbor changed the highlighted target.");

        offer.Invoke(overlay, [null, onB, false]);
        started = (long)GetOverlayField(overlay, "_smartPendingSince");
        SetOverlayField(overlay, "_smartFastTarget", targetB);
        offer.Invoke(overlay, [targetB, onB, false]);
        commit.Invoke(overlay, [started + 100]);
        Assert((SmartSelectionTarget)GetOverlayField(overlay, "_smartTarget") == targetB,
            "A transient native miss erased the highlight.");

        SmartSelectionTarget overlaidWindow = new(2,
            overlay.RectangleToScreen(new Rectangle(310, 110, 100, 100)), "Top window");
        SetOverlayField(overlay, "_smartFastTarget", overlaidWindow);
        offer.Invoke(overlay, [overlaidWindow, onB, false]);
        started = (long)GetOverlayField(overlay, "_smartPendingSince");
        commit.Invoke(overlay, [started + 90]);
        Assert((SmartSelectionTarget)GetOverlayField(overlay, "_smartTarget") == overlaidWindow,
            "An overlapping window could not take focus after settling.");

        // A deliberate click should use the new region even if the hover timer has not fired.
        SetOverlayField(overlay, "_smartTarget", targetA);
        SetOverlayField(overlay, "_smartFastTarget", targetB);
        offer.Invoke(overlay, [targetB, onB, false]);
        overlay.RaiseMouseDown(LeftMouse(new Point(330, 140)));
        Assert((Rectangle)GetOverlayField(overlay, "_pendingSmartBounds") ==
               overlay.RectangleToClient(targetB.ScreenBounds),
            "Clicking during the settling interval selected the old region.");
        overlay.RaiseMouseUp(LeftMouse(new Point(330, 140)));
        Assert((Rectangle)GetOverlayField(overlay, "_selection") ==
               overlay.RectangleToClient(targetB.ScreenBounds),
            "Smart click did not complete with the current target.");
    }

    private static void TestSelectionInteraction()
    {
        using var overlay = new OverlayFormProbe { ClientSize = new Size(800, 600), Location = new Point(-800, 0) };
        Rectangle original = new(200, 180, 300, 220);
        // All eight handles, grabbed off-center: no jump on press or on a stationary move.
        Point[] handles = [new(195, 175), new(350, 175), new(505, 175), new(195, 290),
            new(505, 290), new(195, 405), new(350, 405), new(505, 405)];
        foreach (Point handle in handles)
        {
            SelectRectangle(overlay, original);
            SetOverlayEnum(overlay, "_dragMode", "None");
            overlay.RaiseMouseDown(LeftMouse(handle));
            Assert((Rectangle)GetOverlayField(overlay, "_selection") == original, "Resize jumped on mouse down.");
            overlay.RaiseMouseMove(LeftMouse(handle));
            Assert((Rectangle)GetOverlayField(overlay, "_selection") == original, "Resize jumped on stationary move.");
            Point end = new(handle.X + 7, handle.Y + 6);
            overlay.RaiseMouseUp(LeftMouse(end));
            Rectangle expected = Rectangle.FromLTRB(
                handle.X < 200 ? 207 : 200, handle.Y < 180 ? 186 : 180,
                handle.X > 500 ? 507 : 500, handle.Y > 400 ? 406 : 400);
            Assert((Rectangle)GetOverlayField(overlay, "_selection") == expected, "Resize ignored release coordinates or pointer offset.");
        }

        SelectRectangle(overlay, original);
        overlay.RaiseMouseDown(LeftMouse(new(350, 290)));
        overlay.RaiseMouseUp(LeftMouse(new(2000, 1500)));
        Assert((Rectangle)GetOverlayField(overlay, "_selection") == new Rectangle(500, 380, 300, 220),
            "Moving outside the desktop changed size or failed to clamp.");

        SelectRectangle(overlay, original);
        overlay.RaiseMouseDown(LeftMouse(new(505, 290)));
        overlay.RaiseMouseUp(LeftMouse(new(-100, 290)));
        Assert(((Rectangle)GetOverlayField(overlay, "_selection")).Width == 2, "Resize lost its minimum size.");

        // Outside click still expands immediately rather than preserving a handle offset.
        SelectRectangle(overlay, original);
        overlay.RaiseMouseDown(LeftMouse(new(100, 100)));
        Assert((Rectangle)GetOverlayField(overlay, "_selection") == Rectangle.FromLTRB(100, 100, 500, 400),
            "Outside click no longer expands the selection.");
        overlay.RaiseMouseUp(LeftMouse(new(90, 90)));

        // A fast manual drag and a smart-candidate drag can have no intervening MouseMove.
        foreach (bool smart in new[] { false, true })
        {
            SelectRectangle(overlay, Rectangle.Empty);
            if (smart)
                SetOverlayField(overlay, "_smartTarget", new SmartSelectionTarget(0,
                    overlay.RectangleToScreen(new Rectangle(0, 0, 700, 500)), "Target"));
            overlay.RaiseMouseDown(LeftMouse(new(100, 100)));
            overlay.RaiseMouseUp(LeftMouse(new(300, 250)));
            Assert((Rectangle)GetOverlayField(overlay, "_selection") == new Rectangle(100, 100, 200, 150),
                "Fast drag lost its release position or became a smart click.");
        }
    }

    private static Bitmap CreateSelectionBackground(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        using var brush = new LinearGradientBrush(new Rectangle(Point.Empty, size), Color.LightSteelBlue,
            Color.WhiteSmoke, 35f);
        g.FillRectangle(brush, new Rectangle(Point.Empty, size));
        using var grid = new Pen(Color.FromArgb(80, Color.SlateGray));
        for (int x = 0; x < size.Width; x += 40) g.DrawLine(grid, x, 0, x, size.Height);
        for (int y = 0; y < size.Height; y += 40) g.DrawLine(grid, 0, y, size.Width, y);
        return bitmap;
    }

    private static Region SelectionDirtyRegion(OverlayForm overlay, Rectangle before, Rectangle after) =>
        (Region)typeof(OverlayForm).GetMethod("ComputeSelectionDirtyRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, [before, after])!;

    private static void TestSelectionIncrementalRendering()
    {
        using var overlay = new OverlayFormProbe { ClientSize = new Size(640, 480) };
        SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
        Rectangle previous = new(100, 100, 360, 240);
        SelectRectangle(overlay, previous);
        // Anti-aliased annotation edges expose repainting of the unchanged interior.
        ConfigService.Current.AnnotationColorHex = "#FF0000";
        SetOverlayEnum(overlay, "_activeTool", "Pen");
        overlay.RaiseMouseDown(LeftMouse(new(150, 160)));
        overlay.RaiseMouseMove(LeftMouse(new(400, 270)));
        overlay.RaiseMouseUp(LeftMouse(new(400, 270)));
        SetOverlayEnum(overlay, "_activeTool", "None");
        SetOverlayEnum(overlay, "_dragMode", "Move");
        using var incremental = new Bitmap(640, 480, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(incremental)) overlay.PaintFrame(g);
        Rectangle[] frames = [new(101, 101, 360, 240), new(105, 98, 355, 245), new(4, 4, 600, 450),
            new(4, 30, 600, 430), new(4, 40, 600, 420), new(4, 48, 600, 410),
            new(610, 20, 25, 380), new(620, 1, 2, 400), new(620, 1, 1, 400),
            new(100, 100, 2, 2), new(0, 0, 640, 480), Rectangle.Empty, new(200, 200, 300, 200)];
        foreach (Rectangle next in frames)
        {
            using Region dirty = SelectionDirtyRegion(overlay, previous, next);
            SelectRectangle(overlay, next);
            using (Graphics g = Graphics.FromImage(incremental))
            {
                g.SetClip(dirty, CombineMode.Replace);
                overlay.PaintFrame(g);
            }
            using var reference = new Bitmap(640, 480, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(reference)) overlay.PaintFrame(g);
            AssertPixelsEqual(incremental, reference, $"{previous} -> {next}");
            previous = next;
        }

        // Windows merges invalidations when several mouse samples arrive before a paint.
        using (var pending = new Region(Rectangle.Empty))
        {
            for (int i = 0; i < 40; i++)
            {
                Rectangle next = new(180 + i, 160 + i / 2, 300, 200);
                using Region dirty = SelectionDirtyRegion(overlay, previous, next);
                pending.Union(dirty);
                SelectRectangle(overlay, next);
                previous = next;
            }
            using (Graphics g = Graphics.FromImage(incremental))
            {
                g.SetClip(pending, CombineMode.Replace);
                overlay.PaintFrame(g);
            }
            using var reference = new Bitmap(640, 480, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(reference)) overlay.PaintFrame(g);
            AssertPixelsEqual(incremental, reference, "40 coalesced mouse samples");
        }

        SelectRectangle(overlay, new Rectangle(100, 100, 360, 240));
        SetOverlayEnum(overlay, "_dragMode", "None");
        using (Graphics g = Graphics.FromImage(incremental)) overlay.PaintFrame(g);
        incremental.Save(Path.Combine(AppContext.BaseDirectory, $"selection-{ThemeManager.CurrentMode}.png"));

        using var large = new OverlayFormProbe { ClientSize = new Size(3840, 2160) };
        using Region largeDirty = SelectionDirtyRegion(large, new(300, 200, 3000, 1700), new(301, 201, 3000, 1700));
        using var identity = new Matrix();
        double dirtyArea = largeDirty.GetRegionScans(identity).Sum(r => (double)r.Width * r.Height);
        double oldArea = (3001d + 56) * (1701 + 56);
        Console.WriteLine($"4K one-pixel move invalidated area: {dirtyArea:N0} vs previous {oldArea:N0} pixels ({dirtyArea / oldArea:P1}).");
        Assert(dirtyArea / oldArea < 0.1, "Small movement still invalidates the whole large selection.");
    }

    private static void AssertPixelsEqual(Bitmap actual, Bitmap expected, string scenario)
    {
        var bounds = new Rectangle(Point.Empty, actual.Size);
        BitmapData a = actual.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        BitmapData b = expected.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            byte[] rowA = new byte[actual.Width * 4];
            byte[] rowB = new byte[rowA.Length];
            for (int y = 0; y < actual.Height; y++)
            {
                Marshal.Copy(a.Scan0 + y * a.Stride, rowA, 0, rowA.Length);
                Marshal.Copy(b.Scan0 + y * b.Stride, rowB, 0, rowB.Length);
                for (int x = 0; x < rowA.Length; x++)
                    if (rowA[x] != rowB[x])
                        throw new InvalidOperationException($"Incremental paint differs at ({x / 4}, {y}): {scenario}");
            }
        }
        finally
        {
            actual.UnlockBits(a);
            expected.UnlockBits(b);
        }
    }

    private static void ShowSelectionPreview()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ConfigService.Current.ToolbarPlacement = ToolbarPlacementMode.Center;
        using var overlay = new OverlayFormProbe
        {
            Text = "ZSnaper Selection QA", ShowInTaskbar = true,
            ClientSize = new Size(1200, 800), Location = new Point(80, 80)
        };
        SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
        SelectRectangle(overlay, new Rectangle(240, 180, 640, 420));
        overlay.VisibleChanged += (_, _) => { if (!overlay.Visible) overlay.Close(); };
        using var timeout = new System.Windows.Forms.Timer { Interval = 300_000 };
        timeout.Tick += (_, _) => overlay.Close();
        timeout.Start();
        Application.Run(overlay);
    }
}
