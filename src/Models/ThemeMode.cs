namespace ZSnaper.Models;

public enum ThemeMode
{
    Light,
    Dark
}

public class ThemePalette
{
    public ThemeMode Mode { get; init; }

    // 背景底色与光晕配色
    public Color BackgroundColor { get; init; }
    public Color GlowColor1 { get; init; }
    public Color GlowColor2 { get; init; }

    // 侧边栏与容器
    public Color SidebarBg { get; init; }
    public Color SidebarBorder { get; init; }
    public Color PrimaryContainer { get; init; }
    public Color OnPrimaryContainer { get; init; }
    public Color SurfaceContainerHigh { get; init; }
    public Color CardBg { get; init; }
    public Color CardBorder { get; init; }

    // 文字颜色
    public Color TextPrimary { get; init; }
    public Color TextSecondary { get; init; }
    public Color TextMuted { get; init; }

    // 交互元素配色
    public Color AccentColor { get; init; }
    public Color AccentForeground { get; init; }
    public Color NavItemHover { get; init; }
    public Color NavItemActive { get; init; }
    public Color NavItemActiveText { get; init; }
    public Color SeparatorColor { get; init; }
    public Color WindowControlHover { get; init; }
    public Color WindowControlText { get; init; }
    public Color InputBg { get; init; }
    public Color InputBorder { get; init; }

    public static ThemePalette Create(ThemeMode mode, Color accentColor, bool enableGlow)
    {
        bool isLight = mode == ThemeMode.Light;

        // 黑白预设沿用 #1E293B 配置值：浅色使用纯黑，深色使用纯白
        bool isClassicMonoPreset = accentColor.R == 30 && accentColor.G == 41 && accentColor.B == 59;
        bool isMono = isClassicMonoPreset ||
                      (accentColor.R == accentColor.G && accentColor.G == accentColor.B) ||
                      (accentColor.R > 240 && accentColor.G > 240 && accentColor.B > 240) ||
                      (accentColor.R < 30 && accentColor.G < 30 && accentColor.B < 30);

        Color glow1, glow2;
        if (!enableGlow)
        {
            glow1 = Color.Transparent;
            glow2 = Color.Transparent;
        }
        else if (isMono)
        {
            glow1 = isLight ? Color.FromArgb(180, 180, 180) : Color.FromArgb(45, 45, 45);
            glow2 = isLight ? Color.FromArgb(210, 210, 210) : Color.FromArgb(30, 30, 30);
        }
        else
        {
            // 背景光晕色系跟随强调色 (Glow follows Accent Color)
            glow1 = accentColor;
            // 互补或邻近光晕色 (例如绿色配青色，蓝色配天蓝，紫色配粉紫，橙色配琥珀黄)
            glow2 = GetComplementaryGlow(accentColor);
        }

        if (isLight)
        {
            Color lightAccent = isMono ? Color.Black : accentColor;
            return new ThemePalette
            {
                Mode = ThemeMode.Light,
                BackgroundColor = isMono ? Color.FromArgb(255, 255, 255) : Color.FromArgb(253, 252, 255),
                GlowColor1 = glow1,
                GlowColor2 = glow2,

                SidebarBg = isMono ? Color.FromArgb(245, 245, 245) : Color.FromArgb(244, 241, 249),
                SidebarBorder = isMono ? Color.FromArgb(232, 232, 232) : Color.FromArgb(230, 224, 240),
                CardBg = isMono ? Color.FromArgb(249, 249, 249) : Color.FromArgb(248, 245, 252),
                CardBorder = isMono ? Color.FromArgb(227, 227, 227) : Color.FromArgb(221, 215, 232),
                PrimaryContainer = BlendWithSurface(lightAccent, Color.White, isMono ? 0.06f : 0.11f),
                OnPrimaryContainer = isMono ? Color.FromArgb(22, 22, 22) : Color.FromArgb(24, 22, 30),
                SurfaceContainerHigh = isMono ? Color.FromArgb(238, 238, 238) : Color.FromArgb(237, 232, 245),

                TextPrimary = isMono ? Color.FromArgb(22, 22, 22) : Color.FromArgb(24, 22, 30),
                TextSecondary = isMono ? Color.FromArgb(80, 80, 80) : Color.FromArgb(76, 71, 86),
                TextMuted = isMono ? Color.FromArgb(117, 117, 117) : Color.FromArgb(112, 106, 122),

                AccentColor = lightAccent,
                AccentForeground = GetContrastingForeground(lightAccent),
                NavItemHover = isMono ? Color.FromArgb(16, 23, 23, 23) : Color.FromArgb(16, 61, 83, 111),
                NavItemActive = lightAccent,
                NavItemActiveText = isMono ? Color.Black : Color.FromArgb(15, 23, 42),
                SeparatorColor = isMono ? Color.FromArgb(24, 23, 23, 23) : Color.FromArgb(26, 40, 30, 60),
                WindowControlHover = Color.FromArgb(15, 0, 0, 0),
                WindowControlText = isMono ? Color.FromArgb(90, 90, 90) : Color.FromArgb(71, 85, 105),
                InputBg = Color.FromArgb(255, 255, 255),
                InputBorder = isMono ? Color.FromArgb(154, 154, 154) : Color.FromArgb(136, 130, 148)
            };
        }
        else
        {
            Color darkAccent = isMono ? Color.White : accentColor;
            return new ThemePalette
            {
                Mode = ThemeMode.Dark,
                BackgroundColor = isMono ? Color.FromArgb(17, 17, 17) : Color.FromArgb(17, 15, 22),
                GlowColor1 = glow1,
                GlowColor2 = glow2,

                SidebarBg = isMono ? Color.FromArgb(26, 26, 26) : Color.FromArgb(26, 24, 33),
                SidebarBorder = isMono ? Color.FromArgb(46, 46, 46) : Color.FromArgb(48, 43, 58),
                CardBg = isMono ? Color.FromArgb(24, 24, 24) : Color.FromArgb(24, 22, 30),
                CardBorder = isMono ? Color.FromArgb(55, 55, 55) : Color.FromArgb(56, 51, 68),
                PrimaryContainer = BlendWithSurface(darkAccent,
                    isMono ? Color.FromArgb(30, 30, 30) : Color.FromArgb(28, 25, 36), 0.16f),
                OnPrimaryContainer = isMono ? Color.FromArgb(240, 240, 240) : Color.FromArgb(235, 230, 242),
                SurfaceContainerHigh = isMono ? Color.FromArgb(39, 39, 39) : Color.FromArgb(39, 36, 48),

                TextPrimary = isMono ? Color.FromArgb(238, 238, 238) : Color.FromArgb(235, 230, 242),
                TextSecondary = isMono ? Color.FromArgb(187, 187, 187) : Color.FromArgb(190, 184, 202),
                TextMuted = isMono ? Color.FromArgb(140, 140, 140) : Color.FromArgb(146, 140, 158),

                AccentColor = darkAccent,
                AccentForeground = isMono ? Color.Black : GetContrastingForeground(darkAccent),
                NavItemHover = Color.FromArgb(22, 255, 255, 255),
                NavItemActive = darkAccent,
                NavItemActiveText = Color.White,
                SeparatorColor = Color.FromArgb(34, 255, 255, 255),
                WindowControlHover = Color.FromArgb(30, 255, 255, 255),
                WindowControlText = isMono ? Color.FromArgb(213, 213, 213) : Color.FromArgb(201, 209, 217),
                InputBg = isMono ? Color.FromArgb(31, 31, 31) : Color.FromArgb(31, 28, 38),
                InputBorder = isMono ? Color.FromArgb(97, 97, 97) : Color.FromArgb(106, 100, 118)
            };
        }
    }

    private static Color BlendWithSurface(Color accent, Color surface, float amount) => Color.FromArgb(
        (int)Math.Round(surface.R + (accent.R - surface.R) * amount),
        (int)Math.Round(surface.G + (accent.G - surface.G) * amount),
        (int)Math.Round(surface.B + (accent.B - surface.B) * amount));

    private static Color GetComplementaryGlow(Color accent)
    {
        // 智能衍生邻近色
        float h, s, v;
        ColorToHsv(accent, out h, out s, out v);
        h = (h + 30f) % 360f; // 邻近 30 度色相
        return HsvToColor(h, s, v);
    }

    private static Color GetContrastingForeground(Color background)
    {
        static double ToLinear(byte component)
        {
            double value = component / 255d;
            return value <= 0.04045d
                ? value / 12.92d
                : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }

        double backgroundLuminance =
            0.2126d * ToLinear(background.R) +
            0.7152d * ToLinear(background.G) +
            0.0722d * ToLinear(background.B);
        Color darkForeground = Color.FromArgb(15, 23, 42);
        double darkLuminance =
            0.2126d * ToLinear(darkForeground.R) +
            0.7152d * ToLinear(darkForeground.G) +
            0.0722d * ToLinear(darkForeground.B);

        double whiteContrast = 1.05d / (backgroundLuminance + 0.05d);
        double darkContrast = (backgroundLuminance + 0.05d) / (darkLuminance + 0.05d);
        return darkContrast >= whiteContrast ? darkForeground : Color.White;
    }

    private static void ColorToHsv(Color color, out float hue, out float saturation, out float value)
    {
        int max = Math.Max(color.R, Math.Max(color.G, color.B));
        int min = Math.Min(color.R, Math.Min(color.G, color.B));

        hue = color.GetHue();
        saturation = (max == 0) ? 0 : 1f - (1f * min / max);
        value = max / 255f;
    }

    private static Color HsvToColor(float hue, float saturation, float value)
    {
        int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
        float f = hue / 60 - MathF.Floor(hue / 60);

        value = value * 255;
        int v = Convert.ToInt32(value);
        int p = Convert.ToInt32(value * (1 - saturation));
        int q = Convert.ToInt32(value * (1 - f * saturation));
        int t = Convert.ToInt32(value * (1 - (1 - f) * saturation));

        return hi switch
        {
            0 => Color.FromArgb(v, t, p),
            1 => Color.FromArgb(q, v, p),
            2 => Color.FromArgb(p, v, t),
            3 => Color.FromArgb(p, q, v),
            4 => Color.FromArgb(t, p, v),
            _ => Color.FromArgb(v, p, q),
        };
    }
}
