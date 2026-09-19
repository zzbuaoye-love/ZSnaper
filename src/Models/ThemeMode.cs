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

        // “经典纯色”预设以 #1E293B 持久化：亮色使用深灰，暗色映射为近白。
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
            glow1 = isLight ? Color.FromArgb(180, 200, 220) : Color.FromArgb(45, 55, 72);
            glow2 = isLight ? Color.FromArgb(200, 215, 230) : Color.FromArgb(30, 41, 59);
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
            Color lightAccent = isMono ? Color.FromArgb(24, 28, 38) : accentColor;
            return new ThemePalette
            {
                Mode = ThemeMode.Light,
                BackgroundColor = Color.FromArgb(243, 243, 243),
                GlowColor1 = glow1,
                GlowColor2 = glow2,

                SidebarBg = Color.FromArgb(243, 243, 243),
                SidebarBorder = Color.FromArgb(30, 0, 0, 0),
                CardBg = Color.FromArgb(255, 255, 255),
                CardBorder = Color.FromArgb(229, 229, 229),

                TextPrimary = Color.FromArgb(26, 26, 26),
                TextSecondary = Color.FromArgb(82, 82, 82),
                TextMuted = Color.FromArgb(112, 112, 112),

                AccentColor = lightAccent,
                AccentForeground = GetContrastingForeground(lightAccent),
                NavItemHover = Color.FromArgb(12, 0, 0, 0),
                NavItemActive = lightAccent,
                NavItemActiveText = Color.FromArgb(15, 23, 42),
                SeparatorColor = Color.FromArgb(25, 0, 0, 0),
                WindowControlHover = Color.FromArgb(15, 0, 0, 0),
                WindowControlText = Color.FromArgb(71, 85, 105),
                InputBg = Color.FromArgb(255, 255, 255),
                InputBorder = Color.FromArgb(210, 210, 210)
            };
        }
        else
        {
            // 暗色模式：纯黑白沉浸底色 + 自定义强调色
            Color darkAccent = isMono ? Color.FromArgb(245, 245, 250) : accentColor;
            return new ThemePalette
            {
                Mode = ThemeMode.Dark,
                BackgroundColor = Color.FromArgb(32, 32, 32),
                GlowColor1 = glow1,
                GlowColor2 = glow2,

                SidebarBg = Color.FromArgb(32, 32, 32),
                SidebarBorder = Color.FromArgb(35, 255, 255, 255),
                CardBg = Color.FromArgb(50, 50, 50),
                CardBorder = Color.FromArgb(62, 62, 62),

                TextPrimary = Color.FromArgb(245, 245, 245),
                TextSecondary = Color.FromArgb(205, 205, 205),
                TextMuted = Color.FromArgb(163, 163, 163),

                AccentColor = darkAccent,
                AccentForeground = GetContrastingForeground(darkAccent),
                NavItemHover = Color.FromArgb(20, 255, 255, 255),
                NavItemActive = darkAccent,
                NavItemActiveText = Color.White,
                SeparatorColor = Color.FromArgb(35, 255, 255, 255),
                WindowControlHover = Color.FromArgb(30, 255, 255, 255),
                WindowControlText = Color.FromArgb(201, 209, 217),
                InputBg = Color.FromArgb(43, 43, 43),
                InputBorder = Color.FromArgb(76, 76, 76)
            };
        }
    }

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
