using ZSnaper.Models;

namespace ZSnaper.Services;

public static class ThemeManager
{
    private static ThemeMode _currentMode = ThemeMode.Light;
    private static Color _accentColor = ColorTranslator.FromHtml("#10B981");
    private static bool _enableGlow = true;

    static ThemeManager()
    {
        _currentMode = ConfigService.Current.Theme;
        _enableGlow = ConfigService.Current.EnableBackgroundGlow;
        try
        {
            _accentColor = ColorTranslator.FromHtml(ConfigService.Current.AccentColorHex);
        }
        catch
        {
            _accentColor = ColorTranslator.FromHtml("#10B981");
        }
        RebuildPalette();
    }

    public static ThemeMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (_currentMode != value)
            {
                _currentMode = value;
                ConfigService.Current.Theme = value;
                ConfigService.Save();
                RebuildPalette();
                NotifyThemeChanged();
                AppDiagnostics.LogMessage("Theme.Change", $"Mode={value}");
            }
        }
    }

    public static Color AccentColor
    {
        get => _accentColor;
        set
        {
            if (_accentColor != value)
            {
                _accentColor = value;
                ConfigService.Current.AccentColorHex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
                ConfigService.Save();
                RebuildPalette();
                NotifyThemeChanged();
                AppDiagnostics.LogMessage("Theme.Change", "Accent color changed.", Serilog.Events.LogEventLevel.Debug);
            }
        }
    }

    public static bool EnableGlow
    {
        get => _enableGlow;
        set
        {
            if (_enableGlow != value)
            {
                _enableGlow = value;
                ConfigService.Current.EnableBackgroundGlow = value;
                ConfigService.Save();
                RebuildPalette();
                NotifyThemeChanged();
                AppDiagnostics.LogMessage("Theme.Change", $"BackgroundGlow={value}", Serilog.Events.LogEventLevel.Debug);
            }
        }
    }

    public static ThemePalette Palette { get; private set; } = null!;

    public static event Action? ThemeChanged;

    public static void ToggleTheme()
    {
        CurrentMode = CurrentMode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
    }

    private static void RebuildPalette()
    {
        Palette = ThemePalette.Create(_currentMode, _accentColor, _enableGlow);
    }

    private static void NotifyThemeChanged()
    {
        Delegate[] subscribers = ThemeChanged?.GetInvocationList() ?? [];
        foreach (Action subscriber in subscribers.Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException("ThemeManager.ThemeChanged", exception);
            }
        }
    }
}
