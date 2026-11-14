using System.Drawing.Imaging;
using System.Reflection;
using ZSnaper.Forms;
using ZSnaper.Controls;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;
using ZSnaper.Interop;

namespace ZSnaper.Stability;

internal static partial class Program
{
    private static void TestForegroundGameCapture()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var overlay = new OverlayFormProbe();
        using var hotkeys = new HotkeyService();
        overlay.AttachHotkeyService(hotkeys);
        using var delay = new System.Windows.Forms.Timer { Interval = 20_000 };
        Exception? failure = null;
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            try
            {
                nint foreground = NativeMethods.GetForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(foreground, out uint id);
                using var game = System.Diagnostics.Process.GetProcessById((int)id);
                Assert(game.ProcessName == "TslGame", "PUBG must be the foreground window for this check");
                overlay.BeginCapture(preserveForeground: true);
                Assert(overlay.PreservesForeground && NativeMethods.GetForegroundWindow() == foreground,
                    "Capture minimized or deactivated the fullscreen game");
                string output = Path.Combine(AppContext.BaseDirectory, "ui-preview");
                Directory.CreateDirectory(output);
                Bitmap source = (Bitmap)GetOverlayField(overlay, "_screen");
                Size capturedSize = source.Size;
                source.Save(Path.Combine(output, "pubg-captured.png"), ImageFormat.Png);
                var selection = new Rectangle(400, 200, 600, 400);
                SelectRectangle(overlay, selection);
                overlay.RaiseKeyDown(new KeyEventArgs(Keys.Shift | Keys.Right));
                Assert((Rectangle)GetOverlayField(overlay, "_selection") == new Rectangle(410, 200, 600, 400),
                    "The game selection did not move in physical pixels");
                using var preview = new Bitmap(overlay.Width, overlay.Height);
                using (Graphics graphics = Graphics.FromImage(preview)) overlay.PaintFrame(graphics);
                preview.Save(Path.Combine(output, "pubg-selection.png"), ImageFormat.Png);
                overlay.RaiseKeyDown(new KeyEventArgs(Keys.Escape));
                Assert(!overlay.Visible && GetOverlayField(overlay, "_screen") is null,
                    "Game capture did not release its frozen frame");
                Console.WriteLine($"PUBG foreground capture and selection check passed: {capturedSize.Width} × {capturedSize.Height}");
            }
            catch (Exception exception) { failure = exception; }
            finally { Application.ExitThread(); }
        };
        delay.Start();
        Console.WriteLine("Activate PUBG within 20 seconds");
        Application.Run();
        if (failure is not null) throw failure;
    }

    private static void TestSelectionKeyboardAndLifecycle()
    {
        using var overlay = new OverlayFormProbe { ClientSize = new Size(800, 600) };
        SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
        SelectRectangle(overlay, new Rectangle(100, 100, 300, 200));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Right));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Shift | Keys.Down));
        Assert((Rectangle)GetOverlayField(overlay, "_selection") == new Rectangle(101, 110, 300, 200),
            "Arrow keys did not move selection in pixel increments");
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Right));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Shift | Keys.Down));
        Assert((Rectangle)GetOverlayField(overlay, "_selection") == new Rectangle(101, 110, 301, 210),
            "Resize shortcuts ignored modifier keys");
        SelectRectangle(overlay, new Rectangle(0, 0, 800, 600));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Shift | Keys.Right));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Left));
        Assert((Rectangle)GetOverlayField(overlay, "_selection") == new Rectangle(0, 0, 799, 600),
            "Keyboard adjustment exceeded capture bounds");

        Rectangle expected = new(80, 60, 300, 200);
        SelectRectangle(overlay, expected);
        overlay.RaiseMouseDown(new MouseEventArgs(MouseButtons.Right, 1, 200, 200, 0));
        Assert(!(bool)GetOverlayField(overlay, "_hasSelection") && GetOverlayField(overlay, "_screen") is Bitmap,
            "Right-click reselect released the active screenshot");
        SelectRectangle(overlay, expected);
        Bitmap? captured = null;
        overlay.Captured += (image, _, _) => captured = image;
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Enter));
        using (captured)
        using (Bitmap background = CreateSelectionBackground(overlay.ClientSize))
        {
            Assert(captured is not null && captured.Size == expected.Size, "Crop size changed after keyboard editing");
            Assert(captured!.GetPixel(25, 25) == background.GetPixel(expected.X + 25, expected.Y + 25),
                "Selection UI leaked into the captured image");
        }
        Assert(GetOverlayField(overlay, "_screen") is null && GetOverlayField(overlay, "_capturedCursor") is null,
            "Completed capture retained full desktop resources");

        SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Escape));
        Assert(GetOverlayField(overlay, "_screen") is null, "Cancelled capture retained desktop resources");

        SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
        bool changedDisplay = false;
        overlay.CaptureFailed += _ => changedDisplay = true;
        overlay.Show();
        MethodInfo wndProc = typeof(OverlayForm).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        wndProc.Invoke(overlay, [Message.Create(overlay.Handle, 0x007E, 0, 0)]);
        Assert(changedDisplay && !overlay.Visible && GetOverlayField(overlay, "_screen") is null,
            "Display changes left a stale screenshot or selection visible");
    }

    private static void TestHotkeyRecoveryAndDeduplication()
    {
        foreach (Keys key in new[] { Keys.D0, Keys.D1, Keys.D9, Keys.NumPad0, Keys.NumPad9,
                     Keys.Oemcomma, Keys.OemPeriod, Keys.OemQuestion, Keys.OemSemicolon, Keys.OemQuotes,
                     Keys.OemOpenBrackets, Keys.OemCloseBrackets, Keys.OemPipe, Keys.OemMinus, Keys.Oemplus })
        {
            HotkeyGesture expected = new(key, Keys.Control | Keys.Shift);
            Assert(HotkeyGesture.TryParse(expected.ConfigText, out HotkeyGesture restored) && restored == expected,
                $"Shortcut did not survive serialization: {expected.ConfigText}");
            Assert(HotkeyGesture.TryParse(expected.DisplayText, out restored) && restored == expected,
                $"Legacy shortcut did not migrate: {expected.DisplayText}");
        }
        ConfigService.Current.CaptureHotkey = "Ctrl+Shift+F10";
        ConfigService.Current.OcrHotkey = "Ctrl+Shift+F9";
        ConfigService.Current.CaptureHotkeyForceBinding = false;
        ConfigService.Current.OcrHotkeyForceBinding = false;
        using var service = new HotkeyService();
        service.RegisterConfiguredHotkeys(out bool capture, out bool ocr);
        Assert(capture && ocr, "Could not register isolated test hotkeys");
        service.RegisterConfiguredHotkeys(out capture, out ocr);
        Assert(capture && ocr, "Repeated registration disabled an existing hotkey");
        service.RecoverConfiguredHotkeys();
        Assert(service.IsCaptureRegistered && service.IsOcrRegistered, "Hotkeys did not recover after resume");

        object states = typeof(HotkeyService).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        object state = ((System.Collections.IDictionary)states)[HotkeyCommand.Capture]!;
        state.GetType().GetProperty("SuppressUntil")!.SetValue(state, 0L);
        int triggers = 0;
        service.CommandTriggered += _ => triggers++;
        MethodInfo dispatch = typeof(HotkeyService).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int id = (int)state.GetType().GetProperty("RegistrationId")!.GetValue(state)!;
        dispatch.Invoke(service, [Message.Create(service.Handle, 0x0312, id, 0)]);
        dispatch.Invoke(service, [Message.Create(service.Handle, 0x8003, (int)HotkeyCommand.Capture, (int)(Keys.Control | Keys.Shift | Keys.F10))]);
        Assert(triggers == 1, "Native and hook delivery executed the same gesture twice");
        service.BeginRecording(HotkeyCommand.Capture);
        state.GetType().GetProperty("LastTriggeredAt")!.SetValue(state, long.MinValue);
        dispatch.Invoke(service, [Message.Create(service.Handle, 0x8003, (int)HotkeyCommand.Capture, (int)(Keys.Control | Keys.Shift | Keys.F10))]);
        Assert(triggers == 1, "Hook fallback fired while recording");
        service.EndRecording();
        Assert(service.IsCaptureRegistered, "Recording did not restore registration");
    }

    private static void TestDesktopDuplication()
    {
        Rectangle bounds = Screen.PrimaryScreen!.Bounds;
        using (Bitmap gdi = CaptureService.CaptureScreen(new Rectangle(bounds.Location, new Size(128, 96))))
            Assert(gdi.Size == new Size(128, 96) && gdi.GetPixel(0, 0).A == 255, "GDI capture failed or returned undefined alpha");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert(DesktopDuplicationCapture.TryCapture(bounds, out Bitmap? frame), "DXGI desktop capture failed on the current display");
        using (frame)
        {
            Assert(frame!.Size == bounds.Size, "GPU capture dimensions differed from physical monitor bounds");
            Assert(frame.PixelFormat == PixelFormat.Format32bppRgb && frame.GetPixel(0, 0).A == 255,
                "GPU screenshot contained undefined alpha");
        }
        Console.WriteLine($"DXGI one-shot capture: {watch.ElapsedMilliseconds} ms, {bounds.Width} × {bounds.Height}");
        Console.WriteLine($"HDR active: {DesktopDuplicationCapture.HasHdrOutput(bounds)}, SDR white scale: {DisplayColorInfo.GetSdrWhiteScale(Screen.PrimaryScreen.DeviceName):0.###}");
    }

    private static void TestHdrToneMapping()
    {
        Assert(System.Runtime.InteropServices.Marshal.SizeOf<DisplayColorInfo.DisplayPath>() == 72 &&
            System.Runtime.InteropServices.Marshal.SizeOf<DisplayColorInfo.DeviceHeader>() == 20,
            "Display color interop structures have invalid sizes");
        Half[] pixels = [ (Half)0, (Half)0, (Half)0, (Half)1,
            (Half)0.25, (Half)0.25, (Half)0.25, (Half)1,
            (Half)1, (Half)1, (Half)1, (Half)1,
            (Half)2, (Half)2, (Half)2, (Half)1,
            (Half)4, (Half)4, (Half)4, (Half)1 ];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using Bitmap frame = HdrToneMapper.ConvertScRgb(pinned.AddrOfPinnedObject(), 5, 1, 40, 1f);
            Assert(frame.GetPixel(0, 0).R == 0 && Math.Abs(frame.GetPixel(1, 0).R - 137) <= 1,
                "HDR mapping did not preserve black or encode sRGB midtones");
            Assert(frame.GetPixel(2, 0).R < frame.GetPixel(3, 0).R && frame.GetPixel(3, 0).R < frame.GetPixel(4, 0).R,
                "HDR highlight detail was clipped");
            Assert(frame.GetPixel(4, 0).A == 255, "HDR mapping returned transparent pixels");
            for (int i = 0; i < pixels.Length; i++) pixels[i] *= (Half)2;
            using Bitmap brighter = HdrToneMapper.ConvertScRgb(pinned.AddrOfPinnedObject(), 5, 1, 40, 2f);
            Assert(brighter.GetPixel(1, 0) == frame.GetPixel(1, 0) && brighter.GetPixel(3, 0) == frame.GetPixel(3, 0),
                "Windows SDR white level was not applied consistently");
        }
        finally { pinned.Free(); }
    }

    private static void ShowExperiencePreview(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ConfigService.Current.Theme = args.Contains("--dark") ? ThemeMode.Dark : ThemeMode.Light;
        ConfigService.Current.CaptureHotkey = "Ctrl+Shift+F10";
        ConfigService.Current.OcrHotkey = "Ctrl+Shift+F9";
        ConfigService.Current.CaptureHotkeyForceBinding = false;
        ConfigService.Current.OcrHotkeyForceBinding = false;
        using var main = new MainForm { Text = "ZSnaper Experience QA" };
        if (args.Contains("--compact")) main.Size = main.MinimumSize;
        using var hotkeys = new HotkeyService();
        using var overlay = new OverlayForm { Text = "ZSnaper Capture QA" };
        overlay.AttachHotkeyService(hotkeys);
        overlay.Captured += (image, _, _) => image.Dispose();
        overlay.CaptureFailed += message => Console.WriteLine(message);
        main.RequestCapture += _ => overlay.BeginCapture(args.Contains("--preserve-foreground"));
        hotkeys.CommandTriggered += command =>
        {
            if (command is HotkeyCommand.Capture or HotkeyCommand.Ocr && !overlay.Visible)
                overlay.BeginCapture(args.Contains("--preserve-foreground"));
        };
        hotkeys.RegisterConfiguredHotkeys(out _, out _);
        using var timeout = new System.Windows.Forms.Timer { Interval = 300_000 };
        timeout.Tick += (_, _) => main.Close();
        timeout.Start();
        Application.Run(main);
    }

    private static void TestUiLayouts()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string output = Path.Combine(AppContext.BaseDirectory, "ui-preview");
        Directory.CreateDirectory(output);
        ThemeManager.AccentColor = ColorTranslator.FromHtml(AppConfig.DefaultAccentColorHex);
        using (var pill = GraphicsHelper.GetRoundedRectangle(new Rectangle(2, 2, 130, 28), 20))
            Assert(new RectangleF(1.99f, 1.99f, 130.02f, 28.02f).Contains(pill.GetBounds()),
                "Full button corners exceeded the control bounds");
        using (var toggle = new ModernToggleSwitch { Checked = false })
        {
            int changes = 0;
            toggle.CheckedChanged += (_, _) => changes++;
            MethodInfo keyDown = typeof(ModernToggleSwitch).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var space = new KeyEventArgs(Keys.Space);
            keyDown.Invoke(toggle, [space]);
            Assert(toggle.Checked && changes == 1 && space.SuppressKeyPress, "Space did not toggle the switch once");
            keyDown.Invoke(toggle, [new KeyEventArgs(Keys.Enter)]);
            Assert(!toggle.Checked && changes == 2, "Enter did not toggle the switch off");
            toggle.AccessibilityObject.DoDefaultAction();
            Assert(toggle.AccessibilityObject.State.HasFlag(AccessibleStates.Checked), "Accessible switch action/state is missing");
        }
        using (var theme = new ThemeSegmentedControl())
        using (var colors = new ModernAccentColorPicker())
        using (var tabs = new SettingsTabBar())
        {
            theme.AccessibilityObject.GetChild(1)!.DoDefaultAction();
            Assert(ThemeManager.CurrentMode == ThemeMode.Dark && theme.AccessibilityObject.GetChild(1)!.State.HasFlag(AccessibleStates.Checked),
                "Accessible theme selection failed");
            tabs.AccessibilityObject.GetChild(3)!.DoDefaultAction();
            Assert(tabs.SelectedIndex == 3 && tabs.AccessibilityObject.GetChild(3)!.Role == AccessibleRole.PageTab,
                "Settings tabs do not expose selectable pages");
            colors.AccessibilityObject.GetChild(0)!.DoDefaultAction();
            Assert(colors.AccessibilityObject.GetChild(0)!.Name == "黑白" && colors.AccessibilityObject.GetChild(0)!.State.HasFlag(AccessibleStates.Checked),
                "Accent presets do not expose names and selection");
        }
        bool originalShowSidebarLabels = ConfigService.Current.ShowSidebarLabels;
        ConfigService.Current.ShowSidebarLabels = true;
        try
        {
            foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeManager.CurrentMode = theme;
                Assert(ThemeManager.Palette.Mode == theme, "Requested preview theme was not applied");
                using var main = new MainForm();
                main.Show();
                Panel[] pages = (Panel[])typeof(MainForm).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                foreach (Size size in new[] { main.Size, main.MinimumSize })
                {
                    main.Size = size;
                    for (int index = 0; index < pages.Length; index++)
                    {
                        main.SwitchTab(index);
                        Application.DoEvents();
                        main.CompleteTransitions();
                        Panel page = pages[index];
                        Control[] visible = page.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
                        foreach (Control control in visible)
                            Assert(page.ClientRectangle.Contains(control.Bounds),
                                $"Page {index}: {control.GetType().Name} exceeds {size} content bounds: {control.Bounds}");
                        for (int a = 0; a < visible.Length; a++)
                            for (int b = a + 1; b < visible.Length; b++)
                                Assert(!visible[a].Bounds.IntersectsWith(visible[b].Bounds),
                                    $"Page {index}: '{visible[a].Text}' {visible[a].Bounds} overlaps '{visible[b].Text}' {visible[b].Bounds} at {size}");
                        using var bitmap = new Bitmap(main.Width, main.Height);
                        main.DrawToBitmap(bitmap, main.ClientRectangle);
                        bitmap.Save(Path.Combine(output, $"{theme}-{size.Width}-page-{index}.png"), ImageFormat.Png);
                    }
                }

                // Verify live animated page transition runs and settles cleanly without throwing or sticking
                main.SwitchTab(0);
                Application.DoEvents();
                main.CompleteTransitions();
                main.SwitchTab(4);
                Control overlay = (Control)typeof(MainForm).GetField("_pageTransitionOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (overlay.Visible && deadline.ElapsedMilliseconds < 1200)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
                Assert(!overlay.Visible, "Page transition overlay remained visible after settling");

                // Verify compact icon-only sidebar toggle updates rail width, labels, and page container bounds cleanly
                main.Size = new Size(820, 520);
                main.SwitchTab(4);
                Application.DoEvents();
                main.CompleteTransitions();
                Control sidebarPanel = (Control)typeof(MainForm).GetField("_sidebarPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                Panel pageContainer = (Panel)typeof(MainForm).GetField("_pageContainer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                int expandedSidebarWidth = sidebarPanel.Width;
                int expandedPageWidth = pageContainer.Width;
                ModernToggleSwitch sidebarLabelsToggle = Descendants(pages[4])
                    .OfType<ModernToggleSwitch>()
                    .First(control => control.AccessibleName == "显示侧栏图标文字");
                Assert(sidebarLabelsToggle.Checked, "Sidebar label toggle should start checked");

                sidebarLabelsToggle.Checked = false;
                Application.DoEvents();
                main.CompleteTransitions();
                Assert(!ConfigService.Current.ShowSidebarLabels, "Toggling sidebar labels did not update ConfigService");
                Assert(sidebarPanel.Width < expandedSidebarWidth, "Compact sidebar width did not shrink when hiding labels");
                Assert(pageContainer.Width > expandedPageWidth, "Page container did not expand to reclaim compact sidebar gutter");
                foreach (NavMenuButton navButton in Descendants(sidebarPanel).OfType<NavMenuButton>())
                    Assert(!navButton.ShowLabel, "NavMenuButton still shows labels after compact toggle");

                for (int index = 0; index < pages.Length; index++)
                {
                    main.SwitchTab(index);
                    Application.DoEvents();
                    main.CompleteTransitions();
                    Panel page = pages[index];
                    foreach (Control control in page.Controls.Cast<Control>().Where(c => c.Visible))
                        Assert(page.ClientRectangle.Contains(control.Bounds),
                            $"Compact mode Page {index}: {control.GetType().Name} exceeds content bounds: {control.Bounds}");
                    if (index is 0 or 4)
                    {
                        using var compactBmp = new Bitmap(main.Width, main.Height);
                        main.DrawToBitmap(compactBmp, main.ClientRectangle);
                        compactBmp.Save(Path.Combine(output, $"Compact-{theme}-{main.Width}-page-{index}.png"), ImageFormat.Png);
                    }
                }

                sidebarLabelsToggle.Checked = true;
                Application.DoEvents();
                main.CompleteTransitions();
                Assert(sidebarPanel.Width == expandedSidebarWidth, "Restoring sidebar labels did not restore sidebar width");
                main.Close();
            }
        }
        finally
        {
            ConfigService.Current.ShowSidebarLabels = originalShowSidebarLabels;
        }
        using var dpiForm = new MainForm();
        dpiForm.Show();
        Size initial = dpiForm.ClientSize;
        foreach (int dpi in new[] { 144, 192, 96 })
        {
            NativeMethods.RECT bounds = new() { Left = 50, Top = 50, Right = 50 + initial.Width * dpi / 96, Bottom = 50 + initial.Height * dpi / 96 };
            nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>());
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(bounds, buffer, false);
                NativeMethods.SendMessage(dpiForm.Handle, 0x02E0, dpi | (dpi << 16), buffer);
                Application.DoEvents();
                Console.WriteLine($"DPI {dpi}: form={dpiForm.ClientSize}, scale={dpiForm.AutoScaleDimensions}");
                Assert(dpiForm.DeviceDpi == dpi, "DPI transition did not reach the form");
                dpiForm.SwitchTab(4);
                Application.DoEvents();
                var pages = (Panel[])typeof(MainForm).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dpiForm)!;
                foreach (Control control in pages[4].Controls)
                    Assert(pages[4].ClientRectangle.Contains(control.Bounds), $"DPI {dpi}: settings {pages[4].ClientSize} control is clipped: {control.Bounds}");
                foreach (SettingItemRow row in Descendants(pages[4]).OfType<SettingItemRow>().Where(row => row.Visible && row.ActionControl is not null))
                    Assert(row.ActionControl!.Right == row.Width - GraphicsHelper.Dip(row, 16), "DPI scaling changed setting action padding");
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        }
        Assert(Math.Abs(dpiForm.Width - initial.Width) <= 2 && Math.Abs(dpiForm.Height - initial.Height) <= 2,
            "Moving between DPI scales accumulated window size drift");
        dpiForm.Close();
        Console.WriteLine("Keyboard/accessibility and simulated 100/150/200 percent DPI transitions passed");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void TestPrecisionAndAnnotationHistory()
    {
        using var overlay = new OverlayFormProbe { ClientSize = new Size(800, 600), Location = Point.Empty };
        _ = overlay.Handle;
        SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
        overlay.RaiseMouseMove(new MouseEventArgs(MouseButtons.None, 0, 800, 600, 0));
        Rectangle lens = (Rectangle)typeof(OverlayForm).GetMethod("GetMagnifierBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, [false])!;
        Assert(!lens.IsEmpty && overlay.ClientRectangle.Contains(lens), "Magnifier escaped the display edge");
        using (var image = new Bitmap(overlay.Width, overlay.Height))
        {
            using (Graphics graphics = Graphics.FromImage(image)) overlay.PaintFrame(graphics);
            string output = Path.Combine(AppContext.BaseDirectory, "ui-preview");
            Directory.CreateDirectory(output);
            image.Save(Path.Combine(output, "precision-selection.png"), ImageFormat.Png);
        }
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.F2));
        Assert(!(bool)GetOverlayField(overlay, "_showMagnifier"), "F2 did not toggle precision preview");
        SelectRectangle(overlay, new Rectangle(50, 50, 600, 400));
        SetOverlayEnum(overlay, "_activeTool", "Arrow");
        overlay.RaiseMouseDown(LeftMouse(new Point(150, 160)));
        overlay.RaiseMouseUp(LeftMouse(new Point(380, 260)));
        var annotations = (System.Collections.IList)GetOverlayField(overlay, "_annotations");
        object arrow = annotations[0]!;
        PointF End() => (PointF)arrow.GetType().GetProperty("End")!.GetValue(arrow)!;
        overlay.RaiseMouseDown(LeftMouse(new Point(380, 260)));
        overlay.RaiseMouseUp(LeftMouse(new Point(420, 290)));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Z));
        Assert(annotations.Count == 1 && End() == new PointF(380, 260), "Undo removed the arrow instead of undoing its tip edit");
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Y));
        Assert(End() == new PointF(420, 290), "Redo did not restore the edited endpoint");
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Z));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Z));
        Assert(annotations.Count == 0, "Undo did not remove the created arrow");
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Shift | Keys.Z));
        Assert(annotations.Count == 1, "Ctrl+Shift+Z did not restore the annotation");
        SetOverlayEnum(overlay, "_activeTool", "Pen");
        overlay.RaiseMouseDown(LeftMouse(new Point(180, 320)));
        overlay.RaiseMouseUp(LeftMouse(new Point(240, 330)));
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Y));
        Assert(annotations.Count == 2 && End() == new PointF(380, 260), "A new edit retained obsolete redo history");
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Escape));
        Assert(((System.Collections.IList)GetOverlayField(overlay, "_undoAnnotations")).Count == 0,
            "Cancelled capture retained annotation history");
    }

    private static void TestRecentCaptureHistory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ZSnaper-History-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        ConfigService.Current.RecentCaptureFiles = [];
        try
        {
            string[] files = Enumerable.Range(0, 10).Select(index => Path.Combine(root, $"capture-{index}.png")).ToArray();
            using var main = new MainForm();
            foreach (string file in files)
            {
                using (var image = new Bitmap(240, 180)) image.Save(file, ImageFormat.Png);
                main.UpdateHomeOverview(1, 0, file, false);
            }
            Assert(ConfigService.Current.RecentCaptureFiles.Count == 8 && ConfigService.Current.RecentCaptureFiles[0] == files[9],
                "Recent screenshots did not keep only the latest eight paths");
            main.UpdateHomeOverview(2, 0, files[5], false);
            Assert(ConfigService.Current.RecentCaptureFiles.Count == 8 && ConfigService.Current.RecentCaptureFiles[0] == files[5],
                "Repeated screenshots duplicated history");
            using (var restored = new MainForm())
                Assert(((System.Collections.IList)typeof(MainForm).GetField("_recentThumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restored)!).Count == 8,
                    "Saved screenshot thumbnails were not restored");
            MethodInfo remove = typeof(MainForm).GetMethod("RemoveRecentCapture", BindingFlags.Instance | BindingFlags.NonPublic)!;
            remove.Invoke(main, [files[5], false]);
            Assert(File.Exists(files[5]) && !ConfigService.Current.RecentCaptureFiles.Contains(files[5]), "Removing history deleted the original file");
            remove.Invoke(main, [files[9], true]);
            Assert(!File.Exists(files[9]) && !ConfigService.Current.RecentCaptureFiles.Contains(files[9]), "Delete did not remove the file and thumbnail");
            Assert(File.Exists(files[0]) && File.Exists(files[1]), "Limiting history deleted old screenshots");
            foreach (string file in files.Where(File.Exists)) File.Delete(file);
            Console.WriteLine("Recent capture limit, deduplication, restoration, removal, deletion and file-handle checks passed");
        }
        finally
        {
            ConfigService.Current.RecentCaptureFiles = [];
            ConfigService.Save();
            Directory.Delete(root, true);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint kind);

    private static void TestCaptureResourceProfile()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var main = new MainForm();
        using var hotkeys = new HotkeyService();
        using var overlay = new OverlayFormProbe();
        main.AttachHotkeyService(hotkeys);
        hotkeys.RegisterConfiguredHotkeys(out _, out _);
        overlay.Captured += (image, _, _) => image.Dispose();
        main.Show();
        Application.DoEvents();
        void CaptureCycle(int index)
        {
            overlay.BeginCapture();
            SelectRectangle(overlay, new Rectangle(100, 100, 600, 400));
            SetOverlayEnum(overlay, "_activeTool", "Mosaic");
            overlay.RaiseMouseDown(LeftMouse(new Point(180, 180)));
            overlay.RaiseMouseUp(LeftMouse(new Point(240, 210)));
            if (index % 2 == 0) overlay.RaiseKeyDown(new KeyEventArgs(Keys.Enter));
            else overlay.RaiseKeyDown(new KeyEventArgs(Keys.Escape));
            Application.DoEvents();
            Assert(GetOverlayField(overlay, "_screen") is null &&
                ((System.Collections.IDictionary)GetOverlayField(overlay, "_pixelatedScreens")).Count == 0,
                "Capture cycle retained its full-resolution frame or mosaic cache");
        }
        for (int i = 0; i < 4; i++) CaptureCycle(i);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        long memoryBefore = process.PrivateMemorySize64;
        uint gdiBefore = GetGuiResources(process.Handle, 0), userBefore = GetGuiResources(process.Handle, 1);
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var idle = System.Diagnostics.Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        Exception? failure = null;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
        process.Refresh();
        double idleCpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / idle.Elapsed.TotalMilliseconds * 100;
        var captures = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) CaptureCycle(i);
        captures.Stop();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        uint gdiAfter = GetGuiResources(process.Handle, 0), userAfter = GetGuiResources(process.Handle, 1);
        string report = $"Idle UI + hotkeys (10 s): {idleCpu:0.00}% of one CPU core\n" +
            $"20 full capture / mosaic / finish-cancel cycles: {captures.ElapsedMilliseconds} ms\n" +
            $"Private memory: {memoryBefore / 1048576d:0.0} -> {process.PrivateMemorySize64 / 1048576d:0.0} MiB\n" +
            $"GDI handles: {gdiBefore} -> {gdiAfter}; USER handles: {userBefore} -> {userAfter}\n";
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "resource-profile.txt"), report);
        Assert(gdiAfter <= gdiBefore + 2 && userAfter <= userBefore + 2, "Repeated captures leaked GUI handles");
            }
            catch (Exception exception) { failure = exception; }
            finally { main.Close(); }
        };
        timer.Start();
        Application.Run(main);
        if (failure is not null) throw failure;
    }
}
