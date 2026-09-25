using Microsoft.Win32;
using System.Text.Json;
using ZSnaper.Models;

namespace ZSnaper.Services;

public enum AnimationLevel
{
    Fast,      // 精简：快速利落 (~100ms)
    Balanced,  // 默认：优雅均衡 (~200ms)
    Elegant    // 极度优雅：丝滑流体 (~320ms)
}

public enum ConfigStorageLocation
{
    UserData,
    ApplicationDirectory
}

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public class AppConfig
{
    public AppLogLevel LogLevel { get; set; } = AppLogLevel.Information;
    public ThemeMode Theme { get; set; } = ThemeMode.Light;
    public AnimationLevel AnimationMode { get; set; } = AnimationLevel.Balanced;
    public bool EnableGlowEffect { get; set; } = true;
    public bool EnableBackgroundGlow { get => EnableGlowEffect; set => EnableGlowEffect = value; }
    public string AccentColorHex { get; set; } = "#10B981"; // 翡翠绿
    public bool AutoCopyClipboard { get; set; } = true;
    public OcrProviderKind OcrProvider { get; set; } = OcrProviderKind.WindowsLocal;
    public string OcrApiEndpoint { get; set; } = string.Empty;
    public string OcrApiModel { get; set; } = string.Empty;
    public int OcrApiTimeoutSeconds { get; set; } = 60;
    public string OcrCustomPrompt { get; set; } = string.Empty;
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.FollowTheme;
    public string TrayIconSvgPath { get; set; } = string.Empty;
    public string TrayIconLightColorHex { get; set; } = "#383C40";
    public string TrayIconDarkColorHex { get; set; } = "#FFFFFF";
    public int TrayIconScalePercent { get; set; } = 128;
    public List<string> TrayIconCustomPalette { get; set; } =
        ["#383C40", "#FFFFFF", "#10B981", "#0EA5E9", "#8B5CF6", "#F97316", "#EF4444", "#F59E0B"];
    public TrayClickAction TrayLeftClickAction { get; set; } = TrayClickAction.OpenMainWindow;
    public TrayClickAction TrayMiddleClickAction { get; set; } = TrayClickAction.Capture;
    public bool AutoSavePictures { get; set; } = true;
    public bool AutoCleanOcrParagraphs { get; set; } = true;
    public bool ShowNotification { get; set; } = true;
    public bool ShowWindowsNotifications { get; set; } = true;
    public ToolbarPlacementMode ToolbarPlacement { get; set; } = ToolbarPlacementMode.Auto;
    public double ToolbarAutoHorizontalBias { get; set; } = 0.78d;
    public int ToolbarAutoSampleCount { get; set; }
    public List<CaptureToolbarItem> CaptureToolbarItems { get; set; } = CaptureToolbarDefaults.CreateItems();
    public List<CaptureToolbarItem> CaptureToolbarOrder { get; set; } = CaptureToolbarDefaults.CreateItems();
    public CaptureToolbarLayout CaptureToolbarLayout { get; set; } = CaptureToolbarLayout.Full;
    public ConfirmButtonBehavior ConfirmButtonBehavior { get; set; } = ConfirmButtonBehavior.Copy;
    public AnnotationToolBehavior AnnotationToolBehavior { get; set; } = AnnotationToolBehavior.Sticky;
    public string AnnotationColorHex { get; set; } = "#FF3B30";
    public string AnnotationFontFamily { get; set; } = "Microsoft YaHei UI";
    public float AnnotationFontSize { get; set; } = 18f;
    public int AnnotationFontStyle { get; set; } = (int)FontStyle.Regular;
    public float AnnotationPenWidth { get; set; } = 4f;
    public float AnnotationMosaicSize { get; set; } = 24f;
    public int AnnotationMosaicPixelSize { get; set; } = 10;
    public AnnotationArrowStyle AnnotationArrowStyle { get; set; } = AnnotationArrowStyle.Open;
    public string CustomSavePath { get; set; } = string.Empty;
    public bool AutoStartOnBoot { get; set; } = false;
    public string CaptureHotkey { get; set; } = "Alt+Q";
    public string OcrHotkey { get; set; } = "Alt+X";
    public string CaptureAndPinHotkey { get; set; } = string.Empty;
    public string CaptureCurrentScreenHotkey { get; set; } = string.Empty;
    public string PinClipboardImageHotkey { get; set; } = string.Empty;
    public string OpenMainWindowHotkey { get; set; } = string.Empty;
    public string OpenSaveFolderHotkey { get; set; } = string.Empty;
    public string ToggleThemeHotkey { get; set; } = string.Empty;
    public bool CaptureHotkeyForceBinding { get; set; }
    public bool OcrHotkeyForceBinding { get; set; }
    public bool CaptureAndPinHotkeyForceBinding { get; set; }
    public bool CaptureCurrentScreenHotkeyForceBinding { get; set; }
    public bool PinClipboardImageHotkeyForceBinding { get; set; }
    public bool OpenMainWindowHotkeyForceBinding { get; set; }
    public bool OpenSaveFolderHotkeyForceBinding { get; set; }
    public bool ToggleThemeHotkeyForceBinding { get; set; }
    public string UpdateChannel { get; set; } = "Beta";
    public bool AutoCheckUpdates { get; set; } = true;
    public int UpdateCheckIntervalHours { get; set; } = 24;
    public DateTimeOffset? LastUpdateCheckAt { get; set; }
}

public static class ConfigService
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ZSnaper";
    private const string StartupArgument = "--startup";
    private static readonly string UserConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZSnaper",
        "config.json");
    private static readonly string ApplicationConfigDirectory = Path.Combine(GetApplicationDirectory(), "config");
    private static readonly string ApplicationConfigPath = Path.Combine(ApplicationConfigDirectory, "config.json");
    private static readonly string ApplicationModeMarker = Path.Combine(ApplicationConfigDirectory, ".active");
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static AppConfig Current { get; private set; } = new();
    public static ConfigStorageLocation StorageLocation { get; private set; } = ConfigStorageLocation.UserData;
    public static string ActiveConfigPath => GetPath(StorageLocation);
    public static string ActiveConfigDirectory => Path.GetDirectoryName(ActiveConfigPath)!;

    public static event Action? ConfigChanged;

    static ConfigService()
    {
        Load();
    }

    public static void Load()
    {
        lock (SyncRoot)
        {
            StorageLocation = ConfigLocationStore.Resolve(ApplicationModeMarker);
            string configPath = ActiveConfigPath;
            bool recoveredFromBackup = false;
            if (ConfigFileStore.TryRead(configPath, JsonOptions, out AppConfig loaded))
            {
                Current = loaded;
            }
            else if (ConfigFileStore.TryRead(configPath + ".bak", JsonOptions, out loaded))
            {
                Current = loaded;
                recoveredFromBackup = true;
            }
            else if (StorageLocation == ConfigStorageLocation.ApplicationDirectory &&
                     ConfigFileStore.TryRead(UserConfigPath, JsonOptions, out loaded))
            {
                StorageLocation = ConfigStorageLocation.UserData;
                Current = loaded;
                AppDiagnostics.LogMessage("ConfigService.Load", "Application configuration was unavailable; loaded user data configuration.", Serilog.Events.LogEventLevel.Warning);
                try { File.Delete(ApplicationModeMarker); }
                catch (Exception exception) { AppDiagnostics.LogException("ConfigService.LoadMarker", exception); }
            }
            else
            {
                Current = new AppConfig();
            }

            AppConfigSanitizer.Normalize(Current);
            AppDiagnostics.SetMinimumLevel(Current.LogLevel);
            // 注册表是开机启动的真实来源，配置文件只负责保存 UI 状态。
            Current.AutoStartOnBoot = IsAutoStartEnabled();

            if (recoveredFromBackup)
            {
                TryRestoreRecoveredConfiguration();
            }
        }
    }

    public static bool Save()
    {
        bool saved;
        try
        {
            lock (SyncRoot)
            {
                AppConfigSanitizer.Normalize(Current);
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                string path = ActiveConfigPath;
                ConfigFileStore.WriteAtomic(path, json, path + ".bak", backupExisting: true);
            }

            saved = true;
            AppDiagnostics.SetMinimumLevel(Current.LogLevel);
            AppDiagnostics.LogMessage("Config.Save", "配置已保存", Serilog.Events.LogEventLevel.Debug);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("ConfigService.Save", exception);
            saved = false;
        }

        if (saved) NotifyConfigChanged();
        return saved;
    }

    public static bool TrySetStorageLocation(ConfigStorageLocation location, out string error)
    {
        error = string.Empty;
        if (!Enum.IsDefined(location))
        {
            error = "无效的配置存放位置。";
            return false;
        }

        lock (SyncRoot)
        {
            if (location == StorageLocation) return true;
            try
            {
                AppConfigSanitizer.Normalize(Current);
                string destination = GetPath(location);
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                ConfigLocationStore.Switch(location, destination, ApplicationModeMarker, json);

                StorageLocation = location;
                AppDiagnostics.LogMessage("ConfigService.StorageLocation", $"Configuration moved to {destination}.");
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException("ConfigService.StorageLocation", exception);
                error = exception is UnauthorizedAccessException
                    ? "目标目录不可写，请检查程序安装目录的权限。"
                    : $"切换配置目录失败：{exception.Message}";
                return false;
            }
        }

        NotifyConfigChanged();
        return true;
    }

    private static string GetPath(ConfigStorageLocation location) =>
        location == ConfigStorageLocation.ApplicationDirectory ? ApplicationConfigPath : UserConfigPath;

    private static string GetApplicationDirectory()
    {
        string directory = Path.GetFullPath(AppContext.BaseDirectory);
        string root = Path.GetPathRoot(directory) ?? string.Empty;
        if (!string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.TrimEnd(Path.DirectorySeparatorChar);
        }
        string name = Path.GetFileName(directory);
        return name.Equals("app", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("runtime", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(directory)?.FullName ?? directory
            : directory;
    }

    public static string GetEffectiveSavePath()
    {
        if (!string.IsNullOrWhiteSpace(Current.CustomSavePath) && Directory.Exists(Current.CustomSavePath))
        {
            return Current.CustomSavePath;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ZSnaper");
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false);
            return key?.GetValue(StartupValueName) is string command && IsValidStartupCommand(command);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enable)
            {
                string exePath = Application.ExecutablePath;
                key.SetValue(StartupValueName, $"\"{exePath}\" {StartupArgument}");
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }

            Current.AutoStartOnBoot = enable;
            Save();
            return IsAutoStartEnabled() == enable;
        }
        catch
        {
            return false;
        }
    }

    public static void ResetToDefaults()
    {
        Current = new AppConfig();
        Current.AutoStartOnBoot = IsAutoStartEnabled();
        Save();
    }

    public static int GetAnimationDuration(int baseDurationMs = 200)
    {
        return Current.AnimationMode switch
        {
            AnimationLevel.Fast => (int)(baseDurationMs * 0.55),
            AnimationLevel.Balanced => baseDurationMs,
            AnimationLevel.Elegant => (int)(baseDurationMs * 1.6),
            _ => baseDurationMs
        };
    }

    private static void NotifyConfigChanged()
    {
        Delegate[] subscribers = ConfigChanged?.GetInvocationList() ?? [];
        foreach (Action subscriber in subscribers.Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException("ConfigService.ConfigChanged", exception);
                // One UI subscriber must not block the remaining configuration listeners.
            }
        }
    }

    private static void TryRestoreRecoveredConfiguration()
    {
        try
        {
            string path = ActiveConfigPath;
            if (File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true);
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            ConfigFileStore.WriteAtomic(path, json, path + ".bak", backupExisting: false);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("ConfigService.Recover", exception);
            // The in-memory backup is still usable for this session.
        }
    }

    private static bool IsValidStartupCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        string trimmed = command.Trim();
        string executablePath;
        string arguments;
        if (trimmed.StartsWith('"'))
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1) return false;
            executablePath = trimmed[1..closingQuote];
            arguments = trimmed[(closingQuote + 1)..];
        }
        else
        {
            int separator = trimmed.IndexOf(' ');
            executablePath = separator < 0 ? trimmed : trimmed[..separator];
            arguments = separator < 0 ? string.Empty : trimmed[separator..];
        }

        bool hasStartupArgument = arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase));
        return string.Equals(Path.GetFileName(executablePath), "ZSnaper.exe", StringComparison.OrdinalIgnoreCase) &&
               hasStartupArgument &&
               File.Exists(executablePath);
    }
}
