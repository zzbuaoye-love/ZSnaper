using System.Drawing.Imaging;
using System.Reflection;
using ZSnaper.Forms;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Stability;

internal static partial class Program
{
    private static void TestArrowEditing()
    {
        using var overlay = new OverlayFormProbe { ClientSize = new Size(680, 490) };
        _ = overlay.Handle;
        SetOverlayField(overlay, "_screen", CreateSelectionBackground(overlay.ClientSize));
        SelectRectangle(overlay, new Rectangle(80, 50, 480, 320));
        SetOverlayEnum(overlay, "_activeTool", "Arrow");
        SetOverlayField(overlay, "_showStyleBar", true);

        overlay.RaiseMouseDown(LeftMouse(new Point(150, 160)));
        overlay.RaiseMouseMove(LeftMouse(new Point(380, 260)));
        overlay.RaiseMouseUp(LeftMouse(new Point(380, 260)));
        object arrow = GetOverlayField(overlay, "_selectedArrow");
        Type type = arrow.GetType();
        PointF Start() => (PointF)type.GetProperty("Start")!.GetValue(arrow)!;
        PointF End() => (PointF)type.GetProperty("End")!.GetValue(arrow)!;
        Assert(Start() == new PointF(150, 160) && End() == new PointF(380, 260),
            "Finished arrow endpoints changed unexpectedly.");

        // Grabbing a tip five pixels off center must preserve that offset.
        overlay.RaiseMouseDown(LeftMouse(new Point(385, 263)));
        overlay.RaiseMouseMove(LeftMouse(new Point(385, 263)));
        Assert(End() == new PointF(380, 260), "Arrow tip jumped at drag start.");
        overlay.RaiseMouseUp(LeftMouse(new Point(425, 293)));
        Assert(End() == new PointF(420, 290), "Arrow tip did not follow its drag offset.");
        Assert(Start() == new PointF(150, 160), "Editing a tip moved the other endpoint.");

        PointF oldStart = Start();
        PointF oldEnd = End();
        Point midpoint = new((int)((oldStart.X + oldEnd.X) / 2f),
            (int)((oldStart.Y + oldEnd.Y) / 2f));
        overlay.RaiseMouseDown(LeftMouse(midpoint));
        overlay.RaiseMouseUp(LeftMouse(new Point(midpoint.X + 30, midpoint.Y - 20)));
        Assert(Start() == new PointF(oldStart.X + 30, oldStart.Y - 20) &&
               End() == new PointF(oldEnd.X + 30, oldEnd.Y - 20),
            "Dragging the arrow body did not move both endpoints together.");

        oldStart = Start();
        oldEnd = End();
        midpoint = new((int)((oldStart.X + oldEnd.X) / 2f),
            (int)((oldStart.Y + oldEnd.Y) / 2f));
        overlay.RaiseMouseDown(LeftMouse(midpoint));
        overlay.RaiseMouseUp(LeftMouse(new Point(-1000, -1000)));
        Assert(Start().X >= 80 && End().X >= 80 && Start().Y >= 50 && End().Y >= 50,
            "Moving the arrow escaped the screenshot selection.");

        MethodInfo setColor = typeof(OverlayForm).GetMethod("SetAnnotationStyleColor",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        setColor.Invoke(overlay, [Color.MediumAquamarine]);
        Assert(((Color)type.GetProperty("Color")!.GetValue(arrow)!).ToArgb() ==
               Color.MediumAquamarine.ToArgb(), "Custom color did not update the selected arrow.");

        MethodInfo setStyle = typeof(OverlayForm).GetMethod("SetArrowStyle",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        setStyle.Invoke(overlay, [AnnotationArrowStyle.Double]);
        Assert((AnnotationArrowStyle)type.GetProperty("Style")!.GetValue(arrow)! ==
               AnnotationArrowStyle.Double, "Style change did not update the selected arrow.");

        MethodInfo getButtons = typeof(OverlayForm).GetMethod("GetStyleButtons",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Array buttons = (Array)getButtons.Invoke(overlay, null)!;
        Assert(buttons.Cast<object>().Any(button => button.GetType().GetProperty("Action")!
            .GetValue(button)!.ToString() == "ColorCustom"), "Custom color picker is missing from the arrow toolbar.");

        using (var narrow = new OverlayFormProbe { ClientSize = new Size(320, 560) })
        {
            SelectRectangle(narrow, new Rectangle(20, 20, 280, 270));
            SetOverlayEnum(narrow, "_activeTool", "Arrow");
            SetOverlayField(narrow, "_showStyleBar", true);
            Rectangle styleBounds = (Rectangle)typeof(OverlayForm).GetMethod("GetStyleBarBounds",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(narrow, null)!;
            Array compactButtons = (Array)getButtons.Invoke(narrow, null)!;
            object picker = compactButtons.Cast<object>().Single(button => button.GetType()
                .GetProperty("Action")!.GetValue(button)!.ToString() == "ColorCustom");
            Rectangle pickerBounds = (Rectangle)picker.GetType().GetProperty("Bounds")!.GetValue(picker)!;
            Assert(styleBounds.Contains(pickerBounds), "The custom color picker is clipped on a narrow screen.");
        }

        // Return the arrow to a central spot for visual inspection of handles and style controls.
        type.GetProperty("Start")!.SetValue(arrow, new PointF(160, 150));
        type.GetProperty("End")!.SetValue(arrow, new PointF(425, 280));
        using var preview = new Bitmap(680, 490, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(preview)) overlay.PaintFrame(graphics);
        preview.Save(Path.Combine(AppContext.BaseDirectory, $"arrow-edit-{ThemeManager.CurrentMode}.png"));

        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Escape));
        Assert(GetOverlayField(overlay, "_selectedArrow") is null &&
               (bool)GetOverlayField(overlay, "_hasSelection"),
            "Escape did not leave arrow editing while preserving the screenshot selection.");
        Point shaft = new(290, 214);
        overlay.RaiseMouseDown(LeftMouse(shaft));
        overlay.RaiseMouseUp(LeftMouse(shaft));
        Assert(ReferenceEquals(GetOverlayField(overlay, "_selectedArrow"), arrow),
            "An existing arrow could not be selected again.");
        overlay.RaiseKeyDown(new KeyEventArgs(Keys.Delete));
        Assert(GetOverlayField(overlay, "_selectedArrow") is null,
            "Delete did not remove the selected arrow.");
    }
}
