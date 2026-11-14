using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ZSnaper.Services;

/// <summary>One-shot GPU capture; devices and duplicated outputs never remain alive between screenshots.</summary>
internal static class DesktopDuplicationCapture
{
    private static readonly object HdrCacheLock = new();
    private static Rectangle _hdrCachedBounds;
    private static bool _hdrCachedResult;
    private static long _hdrCacheExpiresTick;

    internal static bool HasHdrOutput(Rectangle bounds)
    {
        long now = Environment.TickCount64;
        lock (HdrCacheLock)
        {
            if (now < _hdrCacheExpiresTick && _hdrCachedBounds == bounds)
            {
                return _hdrCachedResult;
            }
        }

        bool detected = false;
        try
        {
            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
                using (output)
                {
                    using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                    if (output6 is null) continue;
                    OutputDescription1 info = output6.Description1;
                    var monitor = Rectangle.FromLTRB(info.DesktopCoordinates.Left, info.DesktopCoordinates.Top,
                        info.DesktopCoordinates.Right, info.DesktopCoordinates.Bottom);
                    if (info.AttachedToDesktop && monitor.IntersectsWith(bounds) &&
                        info.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020)
                    {
                        detected = true;
                        break;
                    }
                }

                if (detected) break;
            }
        }
        catch (Exception exception) { AppDiagnostics.LogException("Capture.HdrDetection", exception); }

        lock (HdrCacheLock)
        {
            _hdrCachedBounds = bounds;
            _hdrCachedResult = detected;
            _hdrCacheExpiresTick = Environment.TickCount64 + 2500;
        }

        return detected;
    }

    internal static bool TryCapture(Rectangle bounds, out Bitmap? bitmap)
    {
        bitmap = null;
        Bitmap? result = null;
        try
        {
            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Black);
            using var remaining = new Region(bounds);
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    ID3D11Device? device = null;
                    try
                    {
                        for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
                        {
                            using (output)
                            {
                                OutputDescription description = output.Description;
                                var monitor = Rectangle.FromLTRB(description.DesktopCoordinates.Left,
                                    description.DesktopCoordinates.Top, description.DesktopCoordinates.Right,
                                    description.DesktopCoordinates.Bottom);
                                Rectangle intersection = Rectangle.Intersect(bounds, monitor);
                                if (!description.AttachedToDesktop || intersection.IsEmpty) continue;
                                if (device is null)
                                    D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                        [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0], out device).CheckError();
                                using Bitmap frame = CaptureOutput(device!, output, description.Rotation);
                                if (frame.Size != monitor.Size)
                                    throw new InvalidOperationException("Display mode changed during capture");
                                graphics.DrawImage(frame,
                                    new Rectangle(intersection.X - bounds.X, intersection.Y - bounds.Y, intersection.Width, intersection.Height),
                                    new Rectangle(intersection.X - monitor.X, intersection.Y - monitor.Y, intersection.Width, intersection.Height),
                                    GraphicsUnit.Pixel);
                                remaining.Exclude(monitor);
                            }
                        }
                    }
                    finally { device?.Dispose(); }
                }
            }
            // Virtual desktops can contain gaps, but every real monitor must be captured.
            foreach (Screen screen in Screen.AllScreens)
                if (remaining.IsVisible(Rectangle.Intersect(bounds, screen.Bounds)))
                    throw new InvalidOperationException("A monitor could not be captured");
            bitmap = result;
            result = null;
            return true;
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("Capture.DesktopDuplication.Fallback", exception);
            return false;
        }
        finally { result?.Dispose(); }
    }

    private static Bitmap CaptureOutput(ID3D11Device device, IDXGIOutput output, ModeRotation rotation)
    {
        using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
        using var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
        using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
        bool hdr = output6?.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        using IDXGIOutputDuplication duplication = output5 is null || !hdr ? output1.DuplicateOutput(device) :
            output5.DuplicateOutput1(device, new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm });
        duplication.AcquireNextFrame(250, out _, out IDXGIResource? resource).CheckError();
        try
        {
            using (resource)
            using (ID3D11Texture2D texture = resource.QueryInterface<ID3D11Texture2D>())
            using (ID3D11DeviceContext context = device.ImmediateContext)
            {
                Texture2DDescription description = texture.Description;
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                description.MiscFlags = ResourceOptionFlags.None;
                using ID3D11Texture2D staging = device.CreateTexture2D(description);
                context.CopyResource(staging, texture);
                MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
                try
                {
                    Bitmap frame;
                    if (description.Format == Format.R16G16B16A16_Float)
                    {
                        float whiteScale = DisplayColorInfo.GetSdrWhiteScale(output.Description.DeviceName);
                        frame = HdrToneMapper.ConvertScRgb(mapped.DataPointer, (int)description.Width,
                            (int)description.Height, (int)mapped.RowPitch, whiteScale);
                        AppDiagnostics.LogMessage("Capture.Hdr", $"scRGB to SDR, white scale={whiteScale:0.###}");
                    }
                    else
                    {
                        if (description.Format != Format.B8G8R8A8_UNorm)
                            throw new NotSupportedException($"Unsupported capture format: {description.Format}");
                        using var surface = new Bitmap((int)description.Width, (int)description.Height,
                            (int)mapped.RowPitch, PixelFormat.Format32bppRgb, mapped.DataPointer);
                        frame = surface.Clone(new Rectangle(Point.Empty, surface.Size), PixelFormat.Format32bppRgb);
                    }
                    frame.RotateFlip(rotation switch
                    {
                        ModeRotation.Rotate90 => RotateFlipType.Rotate90FlipNone,
                        ModeRotation.Rotate180 => RotateFlipType.Rotate180FlipNone,
                        ModeRotation.Rotate270 => RotateFlipType.Rotate270FlipNone,
                        _ => RotateFlipType.RotateNoneFlipNone
                    });
                    return frame;
                }
                finally { context.Unmap(staging, 0); }
            }
        }
        finally { duplication.ReleaseFrame(); }
    }
}
