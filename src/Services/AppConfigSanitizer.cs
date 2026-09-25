using ZSnaper.Models;

namespace ZSnaper.Services;

internal static class AppConfigSanitizer
{
    private static readonly HotkeyGesture DefaultCaptureHotkey = new(Keys.Q, Keys.Alt);
    private static readonly HotkeyGesture DefaultOcrHotkey = new(Keys.X, Keys.Alt);

    public static void Normalize(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!Enum.IsDefined(config.LogLevel)) config.LogLevel = AppLogLevel.Information;
        if (!Enum.IsDefined(config.Theme)) config.Theme = ThemeMode.Light;
        if (!Enum.IsDefined(config.AnimationMode)) config.AnimationMode = AnimationLevel.Balanced;
        if (!Enum.IsDefined(config.ToolbarPlacement)) config.ToolbarPlacement = ToolbarPlacementMode.Auto;
        if (!Enum.IsDefined(config.CaptureToolbarLayout)) config.CaptureToolbarLayout = CaptureToolbarLayout.Full;
        if (!Enum.IsDefined(config.ConfirmButtonBehavior)) config.ConfirmButtonBehavior = ConfirmButtonBehavior.Copy;
        if (!Enum.IsDefined(config.AnnotationToolBehavior)) config.AnnotationToolBehavior = AnnotationToolBehavior.Sticky;
        if (!Enum.IsDefined(config.AnnotationArrowStyle)) config.AnnotationArrowStyle = AnnotationArrowStyle.Open;
        if (!Enum.IsDefined(config.OcrProvider)) config.OcrProvider = OcrProviderKind.WindowsLocal;
        if (!Enum.IsDefined(config.TrayIconStyle)) config.TrayIconStyle = TrayIconStyle.FollowTheme;
        if (!Enum.IsDefined(config.TrayLeftClickAction)) config.TrayLeftClickAction = TrayClickAction.OpenMainWindow;
        if (!Enum.IsDefined(config.TrayMiddleClickAction)) config.TrayMiddleClickAction = TrayClickAction.Capture;

        config.AccentColorHex = NormalizeColor(config.AccentColorHex, "#10B981");
        config.AnnotationColorHex = NormalizeColor(config.AnnotationColorHex, "#FF3B30");
        config.TrayIconLightColorHex = NormalizeColor(config.TrayIconLightColorHex, "#383C40");
        config.TrayIconDarkColorHex = NormalizeColor(config.TrayIconDarkColorHex, "#FFFFFF");

        config.ToolbarAutoHorizontalBias = double.IsFinite(config.ToolbarAutoHorizontalBias)
            ? Math.Clamp(config.ToolbarAutoHorizontalBias, 0d, 1d)
            : 0.78d;
        config.ToolbarAutoSampleCount = Math.Clamp(config.ToolbarAutoSampleCount, 0, 10_000);
        config.AnnotationFontSize = float.IsFinite(config.AnnotationFontSize)
            ? Math.Clamp(config.AnnotationFontSize, 8f, 72f)
            : 18f;
        config.AnnotationPenWidth = float.IsFinite(config.AnnotationPenWidth)
            ? Math.Clamp(config.AnnotationPenWidth, 1f, 48f)
            : 4f;
        config.AnnotationMosaicSize = float.IsFinite(config.AnnotationMosaicSize)
            ? Math.Clamp(config.AnnotationMosaicSize, 8f, 80f)
            : 24f;
        config.AnnotationMosaicPixelSize = Math.Clamp(config.AnnotationMosaicPixelSize, 4, 32);
        config.TrayIconScalePercent = Math.Clamp(config.TrayIconScalePercent, 80, 160);
        config.OcrApiTimeoutSeconds = Math.Clamp(config.OcrApiTimeoutSeconds, 10, 300);

        const FontStyle allowedFontStyles = FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout;
        if ((config.AnnotationFontStyle & ~(int)allowedFontStyles) != 0)
        {
            config.AnnotationFontStyle = (int)FontStyle.Regular;
        }

        config.AnnotationFontFamily = NormalizeText(config.AnnotationFontFamily, "Microsoft YaHei UI", 128);
        config.CustomSavePath = NormalizeText(config.CustomSavePath, string.Empty, 32_767);
        config.TrayIconSvgPath = NormalizeText(config.TrayIconSvgPath, string.Empty, 32_767);
        config.OcrApiEndpoint = NormalizeText(config.OcrApiEndpoint, string.Empty, 2_048);
        config.OcrApiModel = NormalizeText(config.OcrApiModel, string.Empty, 256);
        config.OcrCustomPrompt = NormalizeText(config.OcrCustomPrompt, string.Empty, 4_096);
        config.UpdateChannel = NormalizeUpdateChannel(config.UpdateChannel);
        config.UpdateCheckIntervalHours = config.UpdateCheckIntervalHours is 6 or 12 or 24 or 168
            ? config.UpdateCheckIntervalHours
            : 24;
        if (config.LastUpdateCheckAt is { } lastCheck && lastCheck > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            config.LastUpdateCheckAt = null;
        }

        NormalizeHotkeys(config);
        NormalizeToolbar(config);
        NormalizeTrayPalette(config);
    }

    private static void NormalizeHotkeys(AppConfig config)
    {
        bool captureValid = HotkeyGesture.TryParse(
            config.CaptureHotkey,
            out HotkeyGesture capture,
            config.CaptureHotkeyForceBinding);
        bool ocrValid = HotkeyGesture.TryParse(
            config.OcrHotkey,
            out HotkeyGesture ocr,
            config.OcrHotkeyForceBinding);
        if (!captureValid)
        {
            capture = DefaultCaptureHotkey;
            config.CaptureHotkeyForceBinding = false;
        }

        if (!ocrValid || ocr == capture)
        {
            ocr = capture == DefaultOcrHotkey ? DefaultCaptureHotkey : DefaultOcrHotkey;
            config.OcrHotkeyForceBinding = false;
        }

        config.CaptureHotkey = capture.ConfigText;
        config.OcrHotkey = ocr.ConfigText;

        HashSet<HotkeyGesture> used = [capture, ocr];
        foreach (HotkeyCommandDefinition definition in HotkeyCommandCatalog.Definitions)
        {
            HotkeyCommand command = definition.Command;
            if (command is HotkeyCommand.Capture or HotkeyCommand.Ocr)
            {
                continue;
            }

            string value = HotkeyCommandCatalog.GetConfigText(config, command);
            bool forceBinding = HotkeyCommandCatalog.GetForceBinding(config, command);
            if (string.IsNullOrWhiteSpace(value))
            {
                HotkeyCommandCatalog.SetConfig(config, command, string.Empty, forceBinding: false);
                continue;
            }

            if (!HotkeyGesture.TryParse(value, out HotkeyGesture gesture, forceBinding) || !used.Add(gesture))
            {
                HotkeyCommandCatalog.SetConfig(config, command, string.Empty, forceBinding: false);
                continue;
            }

            HotkeyCommandCatalog.SetConfig(config, command, gesture.ConfigText, forceBinding);
        }
    }

    private static void NormalizeToolbar(AppConfig config)
    {
        List<CaptureToolbarItem> defaults = CaptureToolbarDefaults.CreateItems();
        config.CaptureToolbarOrder = (config.CaptureToolbarOrder ?? [])
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();
        foreach (CaptureToolbarItem item in defaults)
        {
            if (!config.CaptureToolbarOrder.Contains(item)) config.CaptureToolbarOrder.Add(item);
        }

        // 1. 修复由于历史版本 enum 插入 Pin 导致的 13(Confirm) 与 14(ScrollCapture) 错位：
        // 历史版本中 ScrollCapture 是 13，位于 Cursor 与 Ocr 之间；Pin 插入后 Confirm 变为了 13，
        // 导致旧配置中将选区中间的 13 错误反序列化为 Confirm (√)，而 ScrollCapture (14) 被追加到末尾。
        int confirmOrderIdx = config.CaptureToolbarOrder.IndexOf(CaptureToolbarItem.Confirm);
        int scrollOrderIdx = config.CaptureToolbarOrder.IndexOf(CaptureToolbarItem.ScrollCapture);
        int ocrOrderIdx = config.CaptureToolbarOrder.IndexOf(CaptureToolbarItem.Ocr);
        if (confirmOrderIdx >= 0 && ocrOrderIdx >= 0 && confirmOrderIdx < ocrOrderIdx && scrollOrderIdx > ocrOrderIdx)
        {
            config.CaptureToolbarOrder[confirmOrderIdx] = CaptureToolbarItem.ScrollCapture;
            config.CaptureToolbarOrder[scrollOrderIdx] = CaptureToolbarItem.Confirm;

            if (config.CaptureToolbarItems != null)
            {
                int itemsConfirmIdx = config.CaptureToolbarItems.IndexOf(CaptureToolbarItem.Confirm);
                int itemsOcrIdx = config.CaptureToolbarItems.IndexOf(CaptureToolbarItem.Ocr);
                if (itemsConfirmIdx >= 0 && itemsOcrIdx >= 0 && itemsConfirmIdx < itemsOcrIdx)
                {
                    config.CaptureToolbarItems[itemsConfirmIdx] = CaptureToolbarItem.ScrollCapture;
                    if (!config.CaptureToolbarItems.Contains(CaptureToolbarItem.Confirm))
                    {
                        config.CaptureToolbarItems.Add(CaptureToolbarItem.Confirm);
                    }
                }
            }
        }

        // 2. 保证 Cancel (取消) 与 Confirm (完成) 始终固定停靠在工具栏最右侧末尾
        if (config.CaptureToolbarOrder.Contains(CaptureToolbarItem.Cancel))
        {
            config.CaptureToolbarOrder.Remove(CaptureToolbarItem.Cancel);
            config.CaptureToolbarOrder.Add(CaptureToolbarItem.Cancel);
        }
        if (config.CaptureToolbarOrder.Contains(CaptureToolbarItem.Confirm))
        {
            config.CaptureToolbarOrder.Remove(CaptureToolbarItem.Confirm);
            config.CaptureToolbarOrder.Add(CaptureToolbarItem.Confirm);
        }

        if (config.CaptureToolbarLayout != CaptureToolbarLayout.Custom)
        {
            config.CaptureToolbarItems = CaptureToolbarDefaults.CreateLayout(config.CaptureToolbarLayout);
            return;
        }

        HashSet<CaptureToolbarItem> selected = (config.CaptureToolbarItems ?? [])
            .Where(Enum.IsDefined)
            .ToHashSet();

        // 若因错位异常导致意外被标记为 Custom，但实际上选中的是全部默认项，则重置为标准的 Full 布局
        if (selected.SetEquals(defaults))
        {
            config.CaptureToolbarLayout = CaptureToolbarLayout.Full;
            config.CaptureToolbarItems = CaptureToolbarDefaults.CreateLayout(CaptureToolbarLayout.Full);
            return;
        }

        config.CaptureToolbarItems = config.CaptureToolbarOrder
            .Where(selected.Contains)
            .ToList();
        if (config.CaptureToolbarItems.Count == 0)
        {
            config.CaptureToolbarItems.Add(CaptureToolbarItem.Confirm);
        }
        else
        {
            // 若用户配置中选用了 Confirm，保证其停靠在活动项的最末尾
            if (config.CaptureToolbarItems.Contains(CaptureToolbarItem.Confirm))
            {
                config.CaptureToolbarItems.Remove(CaptureToolbarItem.Confirm);
                config.CaptureToolbarItems.Add(CaptureToolbarItem.Confirm);
            }
            // 若用户配置中同时包含 Cancel 与 Confirm，保证 Cancel 紧挨在 Confirm 之前
            if (config.CaptureToolbarItems.Contains(CaptureToolbarItem.Cancel) &&
                config.CaptureToolbarItems.Contains(CaptureToolbarItem.Confirm))
            {
                config.CaptureToolbarItems.Remove(CaptureToolbarItem.Cancel);
                int confirmPos = config.CaptureToolbarItems.IndexOf(CaptureToolbarItem.Confirm);
                config.CaptureToolbarItems.Insert(confirmPos, CaptureToolbarItem.Cancel);
            }
        }
    }

    private static void NormalizeTrayPalette(AppConfig config)
    {
        config.TrayIconCustomPalette = (config.TrayIconCustomPalette ?? [])
            .Select(value => NormalizeColor(value, string.Empty))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
        if (config.TrayIconCustomPalette.Count == 0)
        {
            config.TrayIconCustomPalette = new AppConfig().TrayIconCustomPalette;
        }
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string candidate = value.Trim();
        if (candidate.Length != 7 || candidate[0] != '#' ||
            !candidate.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            return fallback;
        }

        return candidate.ToUpperInvariant();
    }

    private static string NormalizeText(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : fallback;
    }

    private static string NormalizeUpdateChannel(string? value)
    {
        if (string.Equals(value, "Release", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Stable", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        return "Beta";
    }
}
