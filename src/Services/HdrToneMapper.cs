using System.Drawing.Imaging;

namespace ZSnaper.Services;

internal static class HdrToneMapper
{
    internal static unsafe Bitmap ConvertScRgb(nint pixels, int width, int height, int rowPitch, float sdrWhiteScale)
    {
        if (width <= 0 || height <= 0 || rowPitch < checked(width * 8)) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(sdrWhiteScale) || sdrWhiteScale <= 0) throw new ArgumentOutOfRangeException(nameof(sdrWhiteScale));
        var result = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        BitmapData? target = null;
        try
        {
            target = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < height; y++)
            {
                Half* source = (Half*)((byte*)pixels + y * rowPitch);
                byte* row = (byte*)target.Scan0 + y * target.Stride;
                for (int x = 0; x < width; x++)
                {
                    float r = Sanitize((float)source[x * 4]) / sdrWhiteScale;
                    float g = Sanitize((float)source[x * 4 + 1]) / sdrWhiteScale;
                    float b = Sanitize((float)source[x * 4 + 2]) / sdrWhiteScale;
                    float peak = Math.Max(r, Math.Max(g, b));
                    // Preserve shadows/midtones and compress highlights together to retain hue.
                    float scale = peak <= 0.75f ? 1f : (0.75f + 0.25f * (peak - 0.75f) / (peak - 0.5f)) / peak;
                    row[x * 4] = EncodeSrgb(b * scale);
                    row[x * 4 + 1] = EncodeSrgb(g * scale);
                    row[x * 4 + 2] = EncodeSrgb(r * scale);
                    row[x * 4 + 3] = 255;
                }
            }
            result.UnlockBits(target);
            target = null;
            return result;
        }
        catch
        {
            if (target is not null) result.UnlockBits(target);
            result.Dispose();
            throw;
        }
    }

    private static float Sanitize(float value) => float.IsNaN(value) || value < 0 ? 0 : Math.Min(value, 65504f);
    private static byte EncodeSrgb(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        float encoded = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255), 0, 255);
    }
}
