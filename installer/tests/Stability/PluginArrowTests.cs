using System.Collections;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Reflection;
using ZSnaper.Forms;
using ZSnaper.Models;
using ZSnaper.Plugins;
using ZSnaper.Services;

namespace ZSnaper.Stability;

internal static partial class Program
{
    private static void TestPluginArrows(bool preview, string? packageSource = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "ZSnaper-arrow-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string package = Path.Combine(root, "arrows.zsp");
            var manifest = new PluginManifest
            {
                Id = "zsnaper.arrow-tools", Name = "箭头扩展", Version = "1.0.0",
                Entry = new() { Assembly = "ArrowTools.Plugin.dll", Type = "ArrowTools.Plugin.ArrowToolsPlugin" },
                Requires = new() { PluginApi = ">=1.1.0 <2.0.0" }
            };
            if (packageSource is not null) File.Copy(packageSource, package);
            else using (ZipArchive zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                    writer.Write(PluginManifestJson.Serialize(manifest));
                zip.CreateEntryFromFile(typeof(ArrowTools.Plugin.ArrowToolsPlugin).Assembly.Location, "ArrowTools.Plugin.dll");
            }
            Assert(!PluginPackageService.Inspect(package, "0.0.6", "1.0.0").IsValid,
                "API 1.0 must reject an overlay plugin");
            using var runtime = new PluginRuntimeManager(new PluginInstaller(Path.Combine(root, "store")));
            Assert(runtime.Install(package).Success, "Arrow package installation failed");
            Assert(runtime.EnableAsync(manifest.Id).GetAwaiter().GetResult().Success, "Arrow plugin enable failed");
            Assert(runtime.GetArrowTools().Count == 2 && runtime.GetActions().Count == 0,
                "Plugin must register overlay tools, not post-capture actions");
            ConfigService.Current.AnnotationPenWidth = 4;
            ConfigService.Current.AnnotationColorHex = "#FF3B30";
            ThemeManager.CurrentMode = ThemeMode.Light;
            using var overlay = new OverlayFormProbe
            {
                ClientSize = new Size(920, 660), Text = "ZSnaper Arrow Plugin QA",
                ShowInTaskbar = preview, Location = new Point(100, 100)
            };
            _ = overlay.Handle;
            overlay.AttachPluginManager(runtime);
            SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
            SelectRectangle(overlay, new Rectangle(50, 45, 820, 410));
            IList annotations = (IList)GetOverlayField(overlay, "_annotations");

            object Invoke(string name, params object[] args) => typeof(OverlayForm)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, args)!;
            void Tool(string label)
            {
                SetOverlayEnum(overlay, "_activeTool", "Arrow");
                SetOverlayField(overlay, "_showStyleBar", true);
                Invoke("DeselectArrow");
                int index = runtime.GetArrowTools().ToList().FindIndex(tool => tool.Definition.Label == label);
                Array buttons = (Array)Invoke("GetStyleButtons");
                object button = buttons.Cast<object>().Single(b => Convert.ToInt32(b.GetType()
                    .GetProperty("Action")!.GetValue(b)!) == 1000 + index);
                Rectangle bounds = (Rectangle)button.GetType().GetProperty("Bounds")!.GetValue(button)!;
                Point center = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                overlay.RaiseMouseDown(LeftMouse(center));
                overlay.RaiseMouseUp(LeftMouse(center));
            }
            void Draw(string label, Point start, Point end)
            {
                Tool(label);
                overlay.RaiseMouseDown(LeftMouse(start));
                overlay.RaiseMouseMove(LeftMouse(end));
                overlay.RaiseMouseUp(LeftMouse(end));
            }
            Draw("空心箭头", new(130, 130), new(390, 215));
            Draw("折线箭头", new(500, 125), new(750, 310));
            Assert(annotations.Count == 2, "Overlay buttons did not create two annotations");
            Assert(((Array)Invoke("GetToolbarButtons")).Cast<object>().All(b =>
                Convert.ToInt32(b.GetType().GetProperty("Action")!.GetValue(b)!) < 1000),
                "Plugin arrows must stay in native arrow settings, not add toolbar buttons");
            object elbow = annotations[1]!;
            Type type = elbow.GetType();
            PointF Start() => (PointF)type.GetProperty("Start")!.GetValue(elbow)!;
            PointF End() => (PointF)type.GetProperty("End")!.GetValue(elbow)!;
            // Hit the elbow, far away from the straight start/end segment.
            overlay.RaiseMouseDown(LeftMouse(new Point(500, 310)));
            overlay.RaiseMouseUp(LeftMouse(new Point(515, 325)));
            Assert(Start() == new PointF(515, 140) && End() == new PointF(765, 325),
                "Plugin path hit-testing or body movement failed");
            overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Z));
            Assert(Start() == new PointF(500, 125), "Undo failed for plugin arrow movement");
            overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Y));
            Assert(Start() == new PointF(515, 140), "Redo failed for plugin arrow movement");
            overlay.RaiseMouseDown(LeftMouse(new Point(765, 325)));
            overlay.RaiseMouseUp(LeftMouse(new Point(800, 345)));
            Assert(End() == new PointF(800, 345), "Plugin endpoint editing failed");
            Invoke("SetAnnotationStyleColor", Color.MediumAquamarine);
            Assert(((Color)type.GetProperty("Color")!.GetValue(elbow)!).ToArgb() == Color.MediumAquamarine.ToArgb(),
                "Plugin arrows did not reuse native color editing");
            object widthPreset = ((Array)Invoke("GetStyleButtons")).Cast<object>().First(button =>
                button.GetType().GetProperty("Action")!.GetValue(button)!.ToString() == "SetPrimaryPreset" &&
                (float)button.GetType().GetProperty("Value")!.GetValue(button)! == 8f);
            Invoke("ExecuteStyleAction", widthPreset);
            Assert((float)type.GetProperty("Width")!.GetValue(elbow)! == 8f,
                "Plugin arrows did not reuse native width editing");
            // Switching between plugin/native styles edits the selected arrow and is undoable.
            Invoke("SetArrowStyle", AnnotationArrowStyle.Double);
            Assert(type.GetProperty("PluginTool")!.GetValue(elbow) is null, "Native style did not replace plugin style");
            overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Z));
            Assert(type.GetProperty("PluginTool")!.GetValue(elbow) is not null, "Undo did not restore plugin style");
            // Reselect the same elbow for the deletion test after undo deselects it.
            Invoke("SelectArrow", elbow);
            overlay.RaiseKeyDown(new KeyEventArgs(Keys.Delete));
            Assert(annotations.Count == 1, "Plugin arrow deletion failed");
            overlay.RaiseKeyDown(new KeyEventArgs(Keys.Control | Keys.Z));
            Assert(annotations.Count == 2, "Plugin arrow deletion undo failed");
            using (var narrow = new OverlayFormProbe { ClientSize = new Size(320, 560) })
            {
                narrow.AttachPluginManager(runtime);
                SelectRectangle(narrow, new Rectangle(20, 20, 280, 270));
                SetOverlayEnum(narrow, "_activeTool", "Arrow");
                SetOverlayField(narrow, "_showStyleBar", true);
                Rectangle bounds = (Rectangle)typeof(OverlayForm).GetMethod("GetStyleBarBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(narrow, null)!;
                Array buttons = (Array)typeof(OverlayForm).GetMethod("GetStyleButtons",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(narrow, null)!;
                foreach (object button in buttons)
                    if (Convert.ToInt32(button.GetType().GetProperty("Action")!.GetValue(button)!) >= 1000)
                        Assert(bounds.Contains((Rectangle)button.GetType().GetProperty("Bounds")!.GetValue(button)!),
                            "Plugin arrow style is clipped on a narrow display");
            }
            foreach (ThemeMode mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeManager.CurrentMode = mode;
                using var image = new Bitmap(overlay.Width, overlay.Height);
                using (Graphics graphics = Graphics.FromImage(image)) overlay.PaintFrame(graphics);
                image.Save(Path.Combine(AppContext.BaseDirectory, $"plugin-arrows-{mode}.png"));
            }
            ThemeManager.CurrentMode = ThemeMode.Light;
            if (preview)
            {
                overlay.VisibleChanged += (_, _) => { if (!overlay.Visible) overlay.Close(); };
                using var timeout = new System.Windows.Forms.Timer { Interval = 300_000 };
                timeout.Tick += (_, _) => overlay.Close();
                timeout.Start();
                Application.Run(overlay);
            }
            else
            {
                Bitmap? exported = null;
                overlay.Captured += (image, _, _) => exported = image;
                Invoke("CompleteCapture", CaptureCompletionAction.Default);
                using (exported)
                {
                    Assert(exported is { Width: 820, Height: 410 }, "Plugin arrows broke capture export");
                    // A point on the horizontal segment must be green in the exported PNG.
                    Color pixel = exported!.GetPixel(600 - 50, 345 - 45);
                    Assert(pixel.G > pixel.R && pixel.G > pixel.B, "Export is missing the plugin arrow pixels");
                    exported.Save(Path.Combine(AppContext.BaseDirectory, "plugin-arrows-export.png"), ImageFormat.Png);
                }
            }
            Assert(runtime.DisableAsync(manifest.Id).GetAwaiter().GetResult().Success && runtime.GetArrowTools().Count == 0,
                "Disabling the plugin did not unregister its overlay tools");
            overlay.AttachPluginManager(runtime);
            SetOverlayEnum(overlay, "_activeTool", "Arrow");
            SetOverlayField(overlay, "_showStyleBar", true);
            Assert(((Array)Invoke("GetStyleButtons")).Cast<object>().All(b =>
                Convert.ToInt32(b.GetType().GetProperty("Action")!.GetValue(b)!) < 1000),
                "Disabled plugin left arrow styles behind");
            Assert(runtime.RemoveAsync(manifest.Id).GetAwaiter().GetResult().Success,
                "Plugin files could not be removed after unloading");
        }
        finally { Directory.Delete(root, true); }
    }
}
