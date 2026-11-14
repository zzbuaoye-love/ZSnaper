using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZSnaper.Interop;

namespace ZSnaper.Services;

public static class CaptureService
{
    private static int _saveSequence;

    /// <summary>
    /// 截取指定虚拟屏幕区域
    /// </summary>
    public static Bitmap CaptureScreen(Rectangle bounds) => CaptureScreen(bounds, preferDesktopDuplication: false);

    public static Bitmap CaptureScreen(Rectangle bounds, bool preferDesktopDuplication)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "The capture area must have a positive size.");
        }

        bool hdr = DesktopDuplicationCapture.HasHdrOutput(bounds);
        if (preferDesktopDuplication || hdr)
        {
            if (DesktopDuplicationCapture.TryCapture(bounds, out Bitmap? frame)) return frame!;
            if (hdr) throw new InvalidOperationException("HDR 屏幕捕获失败，请稍后重试或切换为无边框全屏");
        }

        // Screen pixels have no useful alpha channel; an opaque surface also avoids
        // interpreting undefined driver alpha as transparent selection pixels.
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 保存截图启动瞬间的系统鼠标指针，以便用户按 ~ 后将其合成进最终图片。
    /// </summary>
    public static CapturedCursor? CaptureCursor()
    {
        var cursorInfo = new NativeMethods.CURSORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>()
        };

        if (!NativeMethods.GetCursorInfo(ref cursorInfo) ||
            (cursorInfo.flags & NativeMethods.CURSOR_SHOWING) == 0 ||
            cursorInfo.hCursor == nint.Zero)
        {
            return null;
        }

        nint cursorHandle = NativeMethods.CopyIcon(cursorInfo.hCursor);
        if (cursorHandle == nint.Zero) return null;

        if (!NativeMethods.GetIconInfo(cursorHandle, out NativeMethods.ICONINFO iconInfo))
        {
            NativeMethods.DestroyIcon(cursorHandle);
            return null;
        }

        try
        {
            return new CapturedCursor(
                cursorHandle,
                cursorInfo.ptScreenPos,
                new Point((int)iconInfo.xHotspot, (int)iconInfo.yHotspot));
        }
        finally
        {
            if (iconInfo.hbmColor != nint.Zero) NativeMethods.DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != nint.Zero) NativeMethods.DeleteObject(iconInfo.hbmMask);
        }
    }

    /// <summary>
    /// 以指定画布对应的屏幕原点绘制之前保存的鼠标指针。
    /// </summary>
    public static void DrawCursor(Graphics graphics, CapturedCursor cursor, Point canvasScreenOrigin)
    {
        int x = cursor.ScreenPosition.X - canvasScreenOrigin.X - cursor.Hotspot.X;
        int y = cursor.ScreenPosition.Y - canvasScreenOrigin.Y - cursor.Hotspot.Y;
        nint deviceContext = graphics.GetHdc();
        try
        {
            NativeMethods.DrawIconEx(
                deviceContext,
                x,
                y,
                cursor.Handle,
                0,
                0,
                0,
                nint.Zero,
                NativeMethods.DI_NORMAL | NativeMethods.DI_DEFAULTSIZE);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }
    }

    /// <summary>
    /// 保存截图到指定保存路径或系统图片目录下的 ZSnaper 文件夹中
    /// </summary>
    public static string SaveToPictures(Bitmap bmp)
    {
        return SaveToDirectory(bmp, ConfigService.GetEffectiveSavePath());
    }

    internal static string SaveToDirectory(Bitmap bmp, string directory)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        int sequence = (int)((uint)Interlocked.Increment(ref _saveSequence) % 1000);
        string filePath = Path.Combine(directory, $"ZSnaper_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{sequence:D3}.png");
        bmp.Save(filePath, ImageFormat.Png);
        return filePath;
    }

    public static bool TrySaveToPictures(Bitmap bmp, out string? filePath, out string? errorMessage)
    {
        try
        {
            filePath = SaveToPictures(bmp);
            errorMessage = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
        {
            filePath = null;
            errorMessage = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 尝试将位图复制到系统剪贴板
    /// </summary>
    public static bool TryCopyToClipboard(Bitmap bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        return TryClipboardOperation(() => Clipboard.SetImage(bmp));
    }

    /// <summary>
    /// 从剪贴板读取一份与剪贴板生命周期无关的图片副本。
    /// </summary>
    public static bool TryGetImageFromClipboard(out Bitmap? bitmap)
    {
        Bitmap? result = null;
        bool succeeded = TryClipboardOperation(() =>
        {
            if (!Clipboard.ContainsImage()) return false;
            using Image? image = Clipboard.GetImage();
            if (image is null) return false;
            result = new Bitmap(image);
            return true;
        });
        bitmap = result;
        return succeeded && bitmap is not null;
    }

    /// <summary>
    /// 尝试将文本复制到系统剪贴板
    /// </summary>
    public static bool TryCopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return TryClipboardOperation(() => Clipboard.SetText(text));
    }

    private static bool TryClipboardOperation(Action operation) =>
        TryClipboardOperation(() =>
        {
            operation();
            return true;
        });

    private static bool TryClipboardOperation(Func<bool> operation)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return operation();
            }
            catch (ExternalException) when (attempt < 2)
            {
                Thread.Sleep(15 * (attempt + 1));
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}

public sealed class CapturedCursor(nint handle, Point screenPosition, Point hotspot) : IDisposable
{
    public nint Handle { get; private set; } = handle;
    public Point ScreenPosition { get; } = screenPosition;
    public Point Hotspot { get; } = hotspot;

    public void Dispose()
    {
        if (Handle == nint.Zero) return;
        NativeMethods.DestroyIcon(Handle);
        Handle = nint.Zero;
        GC.SuppressFinalize(this);
    }

    ~CapturedCursor()
    {
        if (Handle != nint.Zero) NativeMethods.DestroyIcon(Handle);
    }
}
