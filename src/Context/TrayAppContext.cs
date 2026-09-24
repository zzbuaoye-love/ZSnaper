using ZSnaper.Forms;
using ZSnaper.Helpers;
using ZSnaper.Interop;
using ZSnaper.Models;
using ZSnaper.Services;
using ZSnaper.Controls;
using ZSnaper.Update;
using ZSnaper.Plugins;

namespace ZSnaper.Context;

public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Icon _lightAppIcon;
    private readonly Icon _darkAppIcon;
    private Icon _lightTrayIcon;
    private Icon _darkTrayIcon;
    private string _appliedTrayIconSettingsKey;
    private readonly OverlayForm _overlay;
    private readonly HotkeyService _hotkeyService;
    private readonly PluginRuntimeManager _pluginManager;
    private readonly MainForm _mainForm;
    private readonly ModernTrayMenu _trayMenu;
    private readonly ToolStripMenuItem _captureMenuItem;
    private readonly ToolStripMenuItem _ocrMenuItem;
    private readonly ToolStripMenuItem _captureAndPinMenuItem;
    private readonly ToolStripMenuItem _pinClipboardMenuItem;
    private readonly ToolStripMenuItem _captureScreenMenuItem;
    private readonly ToolStripMenuItem _openFolderMenuItem;
    private readonly ToolStripMenuItem _themeMenuItem;
    private readonly System.Windows.Forms.Timer _trayPrimaryClickTimer = new();
    private readonly List<PinnedImageForm> _pinnedImages = [];
    private ResultForm? _result;
    private bool _ocrMode;
    private CaptureCompletionAction _defaultCaptureAction = CaptureCompletionAction.Default;
    private int _captureCount;
    private int _ocrCount;
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly System.Windows.Forms.Timer _updateTimer;
    private bool _updateCheckInProgress;
    private bool _updateApplyInProgress;
    private GitHubRelease? _pendingRelease;
    private DateTimeOffset? _lastUpdateAttemptAt;
    private bool _exitRequested;
    private bool _disposed;

    public TrayAppContext(bool startMinimizedToTray = false)
    {
        _lightAppIcon = AppIconProvider.CreateApplicationIcon(ThemeMode.Light);
        _darkAppIcon = AppIconProvider.CreateApplicationIcon(ThemeMode.Dark);
        _lightTrayIcon = AppIconProvider.CreateTrayIcon(ThemeMode.Light);
        _darkTrayIcon = AppIconProvider.CreateTrayIcon(ThemeMode.Dark);
        _appliedTrayIconSettingsKey = AppIconProvider.GetTrayIconSettingsKey();
        Icon currentWindowIcon = CurrentWindowIcon;
        Icon currentTrayIcon = CurrentTrayIcon;

        _overlay = new OverlayForm { Icon = currentWindowIcon };
        _overlay.Captured += OnCaptured;

        _hotkeyService = new HotkeyService();
        _overlay.AttachHotkeyService(_hotkeyService);
        _hotkeyService.CommandTriggered += ExecuteHotkeyCommand;
        _pluginManager = new PluginRuntimeManager();

        _mainForm = new MainForm
        {
            Icon = currentWindowIcon,
            ShowInTaskbar = !startMinimizedToTray
        };
        _mainForm.RequestCapture += StartCapture;
        _mainForm.RequestHotkeyChange += (command, gesture, forceBinding) =>
            _hotkeyService.TryUpdateHotkey(command, gesture, forceBinding);
        _mainForm.RequestHotkeyClear += _hotkeyService.TryClearHotkey;
        _mainForm.RequestHotkeyRecordingStart += _hotkeyService.BeginRecording;
        _mainForm.RequestHotkeyRecordingStop += _ => _hotkeyService.EndRecording();
        _mainForm.RequestUpdateCheck += () => _ = CheckForUpdatesAsync(manual: true);
        _mainForm.SetPluginManager(_pluginManager);
        _mainForm.RequestOpenUpdate += unused => _ = ApplyPendingUpdateAsync();
        _mainForm.Shown += (_, _) => CheckForUpdatesIfDue();

        _updateTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _updateTimer.Tick += (_, _) => CheckForUpdatesIfDue();
        ConfigService.ConfigChanged += OnUpdateConfigChanged;
        ConfigService.ConfigChanged += ApplyConfiguredTrayIcon;
        ConfigService.ConfigChanged += ApplyHotkeyMenuShortcuts;
        ConfigureUpdateTimer();

        _tray = new NotifyIcon
        {
            Icon = currentTrayIcon,
            Text = "ZSnaper · 极简截图 & 本地 OCR",
            Visible = true
        };
        _overlay.CaptureFailed += message =>
            ShowWindowsNotification(1800, "ZSnaper", message, ToolTipIcon.Warning);
        ThemeManager.ThemeChanged += ApplyThemeIcon;

        _trayMenu = new ModernTrayMenu();
        _trayMenu.AddBrandAction("打开 ZSnaper", (_, _) => ShowMainForm());
        _trayMenu.AddSectionSeparator();
        _captureMenuItem = _trayMenu.AddAction(
            "截图",
            LucideIcon.Camera,
            (_, _) => StartCapture(ocr: false),
            _hotkeyService.GetGesture(HotkeyCommand.Capture)?.DisplayText ?? string.Empty);
        _ocrMenuItem = _trayMenu.AddAction(
            "截图并 OCR",
            LucideIcon.FileText,
            (_, _) => StartCapture(ocr: true),
            _hotkeyService.GetGesture(HotkeyCommand.Ocr)?.DisplayText ?? string.Empty);
        _captureAndPinMenuItem = _trayMenu.AddAction(
            "截图并贴图",
            LucideIcon.Pin,
            (_, _) => StartCaptureAndPin(),
            _hotkeyService.GetGesture(HotkeyCommand.CaptureAndPin)?.DisplayText ?? string.Empty);
        _pinClipboardMenuItem = _trayMenu.AddAction(
            "贴剪贴板图片",
            LucideIcon.Copy,
            (_, _) => PinClipboardImage(),
            _hotkeyService.GetGesture(HotkeyCommand.PinClipboardImage)?.DisplayText ?? string.Empty);
        _captureScreenMenuItem = _trayMenu.AddAction(
            "当前屏幕截图",
            LucideIcon.Monitor,
            (_, _) => CaptureCurrentScreen(),
            _hotkeyService.GetGesture(HotkeyCommand.CaptureCurrentScreen)?.DisplayText ?? string.Empty);
        _trayMenu.AddSectionSeparator();
        _openFolderMenuItem = _trayMenu.AddAction(
            "打开截图目录",
            LucideIcon.Folder,
            (_, _) => OpenSaveFolder(),
            _hotkeyService.GetGesture(HotkeyCommand.OpenSaveFolder)?.DisplayText ?? string.Empty);
        _trayMenu.AddAction(
            "检查更新",
            LucideIcon.RotateCcw,
            (_, _) => _ = CheckForUpdatesAsync(manual: true));
        _themeMenuItem = _trayMenu.AddAction(
            "深色模式",
            LucideIcon.Moon,
            (_, _) => ThemeManager.ToggleTheme(),
            kind: TrayMenuItemKind.ThemeToggle);
        _trayMenu.AddSectionSeparator();
        _trayMenu.AddAction(
            "退出 ZSnaper",
            LucideIcon.Power,
            (_, _) => ExitApp(),
            kind: TrayMenuItemKind.Destructive);
        _trayMenu.Opening += (_, _) =>
        {
            ApplyHotkeyMenuShortcuts();
            ApplyTrayMenuTheme();
        };
        _tray.ContextMenuStrip = _trayMenu;
        _trayPrimaryClickTimer.Interval = Math.Max(100, SystemInformation.DoubleClickTime + 20);
        _trayPrimaryClickTimer.Tick += (_, _) =>
        {
            _trayPrimaryClickTimer.Stop();
            ExecuteTrayClickAction(ConfigService.Current.TrayLeftClickAction);
        };
        _tray.MouseClick += HandleTrayMouseClick;
        _tray.MouseDoubleClick += HandleTrayMouseDoubleClick;

        _hotkeyService.RegisterConfiguredHotkeys(out bool captureOk, out bool ocrOk);
        if (!captureOk)
        {
            ShowWindowsNotification(
                2000,
                "ZSnaper",
                $"{_hotkeyService.CaptureGesture.DisplayText} 截图快捷键启用失败，{(_hotkeyService.IsCaptureForceBinding ? "按键拦截未能启动" : "可能已被占用")}",
                ToolTipIcon.Warning);
        }
        if (!ocrOk)
        {
            ShowWindowsNotification(
                2000,
                "ZSnaper",
                $"{_hotkeyService.OcrGesture.DisplayText} OCR 快捷键启用失败，{(_hotkeyService.IsOcrForceBinding ? "按键拦截未能启动" : "可能已被占用")}",
                ToolTipIcon.Warning);
        }

        HotkeyCommand[] otherFailures = _hotkeyService.GetInactiveConfiguredCommands()
            .Where(command => command is not HotkeyCommand.Capture and not HotkeyCommand.Ocr)
            .ToArray();
        if (otherFailures.Length > 0)
        {
            string names = string.Join("、", otherFailures.Select(command => HotkeyCommandCatalog.GetDefinition(command).Name));
            ShowWindowsNotification(2200, "ZSnaper", $"这些快捷键启用失败：{names}", ToolTipIcon.Warning);
        }

        if (!startMinimizedToTray)
        {
            ShowMainForm();
        }
        _ = LoadPluginsAsync();
    }

    private async Task LoadPluginsAsync()
    {
        try { await _pluginManager.StartEnabledAsync(); }
        catch (Exception exception) { AppDiagnostics.LogException("TrayAppContext.LoadPlugins", exception); }
    }

    private void ShowMainForm()
    {
        if (_mainForm.IsDisposed) return;
        _mainForm.ShowInTaskbar = true;
        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        NativeMethods.SetForegroundWindow(_mainForm.Handle);
    }

    public void ActivateMainWindow() => ShowMainForm();

    private void ExecuteHotkeyCommand(HotkeyCommand command)
    {
        switch (command)
        {
            case HotkeyCommand.Capture:
                StartCapture(ocr: false);
                break;
            case HotkeyCommand.Ocr:
                StartCapture(ocr: true);
                break;
            case HotkeyCommand.CaptureAndPin:
                StartCaptureAndPin();
                break;
            case HotkeyCommand.CaptureCurrentScreen:
                CaptureCurrentScreen();
                break;
            case HotkeyCommand.PinClipboardImage:
                PinClipboardImage();
                break;
            case HotkeyCommand.OpenMainWindow:
                ShowMainForm();
                break;
            case HotkeyCommand.OpenSaveFolder:
                OpenSaveFolder();
                break;
            case HotkeyCommand.ToggleTheme:
                ThemeManager.ToggleTheme();
                break;
        }
    }

    private void HandleTrayMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (_disposed || _exitRequested) return;

        switch (eventArgs.Button)
        {
            case MouseButtons.Left:
                // Wait until the system double-click window closes so a double-click runs only once.
                _trayPrimaryClickTimer.Stop();
                _trayPrimaryClickTimer.Start();
                break;
            case MouseButtons.Middle:
                ExecuteTrayClickAction(ConfigService.Current.TrayMiddleClickAction);
                break;
        }
    }

    private void HandleTrayMouseDoubleClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left || _disposed || _exitRequested) return;

        _trayPrimaryClickTimer.Stop();
        ExecuteTrayClickAction(ConfigService.Current.TrayLeftClickAction);
    }

    private void ExecuteTrayClickAction(TrayClickAction action)
    {
        switch (action)
        {
            case TrayClickAction.None:
                return;
            case TrayClickAction.OpenMainWindow:
                ShowMainForm();
                return;
            case TrayClickAction.Capture:
                StartCapture(ocr: false);
                return;
            case TrayClickAction.CaptureWithOcr:
                StartCapture(ocr: true);
                return;
            case TrayClickAction.CaptureAndPin:
                StartCaptureAndPin();
                return;
            case TrayClickAction.CaptureCurrentScreen:
                CaptureCurrentScreen();
                return;
            case TrayClickAction.PinClipboardImage:
                PinClipboardImage();
                return;
            case TrayClickAction.OpenSaveFolder:
                OpenSaveFolder();
                return;
            case TrayClickAction.ToggleTheme:
                ThemeManager.ToggleTheme();
                return;
        }
    }

    private void OpenSaveFolder()
    {
        try
        {
            string directory = ConfigService.GetEffectiveSavePath();
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("TrayAppContext.OpenSaveFolder", exception);
            ShowWindowsNotification(
                2500,
                "ZSnaper",
                "无法打开截图目录：" + ShortenError(exception.Message),
                ToolTipIcon.Warning);
        }
    }

    private void OnUpdateConfigChanged()
    {
        ConfigureUpdateTimer();
        CheckForUpdatesIfDue();
    }

    private void ApplyHotkeyMenuShortcuts()
    {
        _captureMenuItem.ShortcutKeyDisplayString = _hotkeyService.GetGesture(HotkeyCommand.Capture)?.DisplayText ?? string.Empty;
        _ocrMenuItem.ShortcutKeyDisplayString = _hotkeyService.GetGesture(HotkeyCommand.Ocr)?.DisplayText ?? string.Empty;
        _captureAndPinMenuItem.ShortcutKeyDisplayString = _hotkeyService.GetGesture(HotkeyCommand.CaptureAndPin)?.DisplayText ?? string.Empty;
        _pinClipboardMenuItem.ShortcutKeyDisplayString = _hotkeyService.GetGesture(HotkeyCommand.PinClipboardImage)?.DisplayText ?? string.Empty;
        _captureScreenMenuItem.ShortcutKeyDisplayString = _hotkeyService.GetGesture(HotkeyCommand.CaptureCurrentScreen)?.DisplayText ?? string.Empty;
        _openFolderMenuItem.ShortcutKeyDisplayString = _hotkeyService.GetGesture(HotkeyCommand.OpenSaveFolder)?.DisplayText ?? string.Empty;
        _trayMenu.PerformLayout();
    }

    private void ConfigureUpdateTimer()
    {
        if (ConfigService.Current.AutoCheckUpdates)
        {
            _updateTimer.Start();
        }
        else
        {
            _updateTimer.Stop();
        }
    }

    private void CheckForUpdatesIfDue()
    {
        if (!ConfigService.Current.AutoCheckUpdates ||
            _updateCheckInProgress ||
            _updateCancellation.IsCancellationRequested ||
            !IsUpdateCheckDue())
        {
            return;
        }

        _ = CheckForUpdatesAsync(manual: false);
    }

    private bool IsUpdateCheckDue()
    {
        if (_lastUpdateAttemptAt is { } lastAttempt &&
            DateTimeOffset.UtcNow - lastAttempt < TimeSpan.FromMinutes(15))
        {
            return false;
        }

        DateTimeOffset? lastCheck = ConfigService.Current.LastUpdateCheckAt;
        if (lastCheck is null) return true;

        int intervalHours = Math.Clamp(ConfigService.Current.UpdateCheckIntervalHours, 1, 24 * 365);
        return DateTimeOffset.UtcNow - lastCheck.Value >= TimeSpan.FromHours(intervalHours);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInProgress || _updateCancellation.IsCancellationRequested) return;

        _updateCheckInProgress = true;
        _lastUpdateAttemptAt = DateTimeOffset.UtcNow;
        _mainForm.SetUpdateStatus("检查中…", isBusy: true);

        try
        {
            UpdateCheckResult result = await VersionGet.CheckForUpdateAsync(_updateCancellation.Token);
            if (_updateCancellation.IsCancellationRequested || _mainForm.IsDisposed) return;

            if (result.IsSuccess)
            {
                ConfigService.Current.LastUpdateCheckAt = DateTimeOffset.UtcNow;
                ConfigService.Save();
                _mainForm.RefreshUpdateCheckInfo();
            }

            if (result.IsSuccess && result.HasUpdate && result.LatestRelease is { } release)
            {
                _pendingRelease = release;
                string version = release.CleanVersion;
                _mainForm.SetUpdateStatus(
                    "立即更新",
                    isBusy: false,
                    release.TagName);

                ShowWindowsNotification(
                    4000,
                    "ZSnaper 有新版本",
                    $"发现 v{version}，可在设置页打开下载页",
                    ToolTipIcon.Info);
            }
            else if (result.IsSuccess)
            {
                _pendingRelease = null;
                _mainForm.SetUpdateStatus("已是最新", isBusy: false);
                if (manual)
                {
                    ShowWindowsNotification(2200, "ZSnaper", "当前已是最新版本", ToolTipIcon.Info);
                }
            }
            else
            {
                _mainForm.SetUpdateStatus("检查失败", isBusy: false);
                if (manual)
                {
                    ShowWindowsNotification(
                        3000,
                        "ZSnaper",
                        result.ErrorMessage ?? "检查更新失败，请稍后重试",
                        ToolTipIcon.Warning);
                }
            }
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task ApplyPendingUpdateAsync()
    {
        if (_updateApplyInProgress || _pendingRelease is null || _updateCancellation.IsCancellationRequested) return;
        _updateApplyInProgress = true;
        GitHubRelease release = _pendingRelease;
        _mainForm.SetUpdateStatus("下载更新 0%", isBusy: true);
        try
        {
            Progress<int> progress = new(percent =>
                _mainForm.SetUpdateStatus($"下载更新 {percent}%", isBusy: true));
            PreparedAppUpdate update = await new AppUpdateService().PrepareAsync(
                release,
                progress,
                _updateCancellation.Token);
            if (_updateCancellation.IsCancellationRequested) return;

            _mainForm.SetUpdateStatus("正在安装…", isBusy: true);
            AppUpdateService.Launch(update);
            ExitApp();
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            // Application shutdown cancelled the download.
        }
        catch (Exception exception)
        {
            _mainForm.SetUpdateStatus("重试更新", isBusy: false, release.TagName);
            ShowWindowsNotification(3500, "ZSnaper", "更新失败：" + exception.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _updateApplyInProgress = false;
        }
    }

    private void OpenUpdatePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            ShowWindowsNotification(2500, "ZSnaper", "更新链接无效", ToolTipIcon.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowWindowsNotification(3000, "ZSnaper", "无法打开更新页面：" + ex.Message, ToolTipIcon.Warning);
        }
    }

    private void StartCapture(bool ocr) =>
        StartCapture(ocr, CaptureCompletionAction.Default);

    private void StartCaptureAndPin() =>
        StartCapture(ocr: false, defaultAction: CaptureCompletionAction.Pin);

    private void StartCapture(bool ocr, CaptureCompletionAction defaultAction)
    {
        if (_disposed || _exitRequested || _overlay.IsDisposed) return;
        if (_overlay.Visible)
        {
            if (!_overlay.PreservesForeground) _overlay.Activate();
            return;
        }

        _ocrMode = ocr;
        _defaultCaptureAction = defaultAction;
        try
        {
            _overlay.BeginCapture(ShouldPreserveForegroundForCapture());
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("TrayAppContext.StartCapture", exception);
            ShowWindowsNotification(
                3000,
                "ZSnaper",
                "无法开始截图：" + ShortenError(exception.Message),
                ToolTipIcon.Warning);
        }
    }

    private static bool ShouldPreserveForegroundForCapture()
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero || !NativeMethods.IsWindowVisible(foreground) ||
            NativeMethods.IsIconic(foreground) || !NativeMethods.GetWindowRect(foreground, out NativeMethods.RECT rect))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == (uint)Environment.ProcessId) return false;
        try
        {
            if (string.Equals(System.Diagnostics.Process.GetProcessById((int)processId).ProcessName,
                    "explorer", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        Rectangle window = rect.ToRectangle();
        Rectangle monitor = Screen.FromHandle(foreground).Bounds;
        const int tolerance = 16;
        return window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 &&
               window.Right >= monitor.Right - 2 && window.Bottom >= monitor.Bottom - 2 &&
               window.Left >= monitor.Left - tolerance && window.Top >= monitor.Top - tolerance &&
               window.Right <= monitor.Right + tolerance && window.Bottom <= monitor.Bottom + tolerance;
    }

    private async void OnCaptured(
        Bitmap bitmap,
        Point screenPoint,
        CaptureCompletionAction action)
    {
        try
        {
            using (bitmap)
            {
                CaptureCompletionAction effectiveAction = action == CaptureCompletionAction.Default
                    ? _defaultCaptureAction
                    : action;
                _defaultCaptureAction = CaptureCompletionAction.Default;
                bool performOcr = effectiveAction == CaptureCompletionAction.Ocr ||
                                  effectiveAction == CaptureCompletionAction.Default && _ocrMode;
                (bool copyImage, bool saveImage) = ResolveCaptureDestinations(effectiveAction, _ocrMode);

                bool copied = copyImage && CaptureService.TryCopyToClipboard(bitmap);
                string? savedFilePath = null;
                string? saveError = null;
                if (saveImage)
                {
                    CaptureService.TrySaveToPictures(bitmap, out savedFilePath, out saveError);
                }

                try { _pluginManager.PublishCapture(bitmap, effectiveAction.ToString()); }
                catch (Exception exception) { AppDiagnostics.LogException("TrayAppContext.PluginCapture", exception); }

                _captureCount++;

                if (effectiveAction == CaptureCompletionAction.Pin)
                {
                    ShowPinnedImage(bitmap, screenPoint);
                    _mainForm.UpdateHomeOverview(_captureCount, _ocrCount, null, wasOcr: false);
                    return;
                }

                if (!performOcr)
                {
                    _mainForm.UpdateHomeOverview(_captureCount, _ocrCount, savedFilePath, wasOcr: false);
                    ShowCaptureNotification(effectiveAction, copied, savedFilePath, saveError);
                    return;
                }

                string text;
                string modelName;
                int? totalTokens = null;
                int? promptTokens = null;
                int? completionTokens = null;
                bool ocrFailed = false;
                try
                {
                    OcrRecognitionResult result = await OcrService.RecognizeDetailedAsync(bitmap);
                    text = result.Text;
                    modelName = result.ModelName;
                    totalTokens = result.TotalTokens;
                    promptTokens = result.PromptTokens;
                    completionTokens = result.CompletionTokens;
                }
                catch (Exception ex)
                {
                    ocrFailed = true;
                    AppDiagnostics.LogException("TrayAppContext.OnCaptured.Ocr", ex);
                    ShowWindowsNotification(3000, "ZSnaper", "OCR 识别失败：" + ShortenError(ex.Message), ToolTipIcon.Warning);
                    modelName = OcrService.GetActiveModelName();
                    text = "(OCR 失败: " + ex.Message + ")";
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = "(未识别到文字)";
                }
                else if (ConfigService.Current.AutoCleanOcrParagraphs)
                {
                    text = LocalTextSegmenter.SmartSegment(text);
                }

                _ocrCount++;
                _mainForm.UpdateHomeOverview(_captureCount, _ocrCount, savedFilePath, wasOcr: true);
                bool textCopied = CaptureService.TryCopyTextToClipboard(text);

                if (!ocrFailed && ConfigService.Current.ShowNotification)
                {
                    string message = textCopied
                        ? "OCR 识别完成，文字已复制到剪贴板"
                        : "OCR 识别完成，但剪贴板暂时不可用";
                    ShowWindowsNotification(1000, "ZSnaper", message, textCopied ? ToolTipIcon.Info : ToolTipIcon.Warning);
                }
                if (saveError is not null)
                {
                    ShowWindowsNotification(2500, "ZSnaper", "保存截图失败：" + ShortenError(saveError), ToolTipIcon.Warning);
                }

                _mainForm.UpdateLatestOcrText(text);
                if (_result is null || _result.IsDisposed)
                {
                    _result = new ResultForm { Icon = CurrentWindowIcon };
                }

                _result.ShowResult(text, screenPoint, modelName, totalTokens, promptTokens, completionTokens);
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("TrayAppContext.OnCaptured", exception);
            if (!_disposed)
            {
                ShowWindowsNotification(
                    3000,
                    "ZSnaper",
                    "处理截图失败：" + ShortenError(exception.Message),
                    ToolTipIcon.Error);
            }
        }
    }

    private void CaptureCurrentScreen()
    {
        if (_disposed || _exitRequested) return;

        Rectangle bounds = Screen.FromPoint(Cursor.Position).Bounds;
        try
        {
            _ocrMode = false;
            _defaultCaptureAction = CaptureCompletionAction.Default;
            Bitmap bitmap = CaptureService.CaptureScreen(bounds);
            OnCaptured(bitmap, bounds.Location, CaptureCompletionAction.Default);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("TrayAppContext.CaptureCurrentScreen", exception);
            ShowWindowsNotification(
                2500,
                "ZSnaper",
                "当前屏幕截图失败：" + ShortenError(exception.Message),
                ToolTipIcon.Warning);
        }
    }

    private void PinClipboardImage()
    {
        if (_disposed || _exitRequested) return;
        if (!CaptureService.TryGetImageFromClipboard(out Bitmap? bitmap) || bitmap is null)
        {
            ShowWindowsNotification(2200, "ZSnaper", "剪贴板中没有可贴出的图片", ToolTipIcon.Info);
            return;
        }

        using (bitmap)
        {
            ShowPinnedImage(bitmap, Cursor.Position);
        }
    }

    private void ShowPinnedImage(Bitmap bitmap, Point near)
    {
        var pinned = new PinnedImageForm(bitmap, near)
        {
            Icon = CurrentWindowIcon
        };
        _pinnedImages.Add(pinned);
        pinned.FormClosed += (_, _) => _pinnedImages.Remove(pinned);
        pinned.Show();
        pinned.Activate();
    }

    private static (bool Copy, bool Save) ResolveCaptureDestinations(
        CaptureCompletionAction action,
        bool ocrMode)
    {
        if (action == CaptureCompletionAction.Copy) return (true, false);
        if (action == CaptureCompletionAction.Save) return (false, true);

        if (action == CaptureCompletionAction.ScrollCapture)
        {
            return ConfigService.Current.ConfirmButtonBehavior switch
            {
                ConfirmButtonBehavior.Copy => (true, false),
                ConfirmButtonBehavior.Save => (false, true),
                ConfirmButtonBehavior.CopyAndSave => (true, true),
                ConfirmButtonBehavior.FinishOnly => (false, false),
                _ => (ConfigService.Current.AutoCopyClipboard, ConfigService.Current.AutoSavePictures)
            };
        }

        if (action == CaptureCompletionAction.Default && !ocrMode)
        {
            return ConfigService.Current.ConfirmButtonBehavior switch
            {
                ConfirmButtonBehavior.Copy => (true, false),
                ConfirmButtonBehavior.Save => (false, true),
                ConfirmButtonBehavior.CopyAndSave => (true, true),
                ConfirmButtonBehavior.FinishOnly => (false, false),
                _ => (ConfigService.Current.AutoCopyClipboard, ConfigService.Current.AutoSavePictures)
            };
        }

        return action is CaptureCompletionAction.Default or CaptureCompletionAction.Ocr
            ? (ConfigService.Current.AutoCopyClipboard, ConfigService.Current.AutoSavePictures)
            : (false, false);
    }

    private void ShowWindowsNotification(int timeout, string title, string message, ToolTipIcon icon)
    {
        if (!ConfigService.Current.ShowWindowsNotifications || _disposed) return;
        _tray.ShowBalloonTip(timeout, title, message, icon);
    }

    private void ShowCaptureNotification(
        CaptureCompletionAction action,
        bool copied,
        string? savedFilePath,
        string? saveError)
    {
        if (saveError is not null)
        {
            string failure = copied
                ? "截图已复制，但保存失败："
                : "保存截图失败：";
            ShowWindowsNotification(2800, "ZSnaper", failure + ShortenError(saveError), ToolTipIcon.Warning);
            return;
        }

        if (!ConfigService.Current.ShowNotification) return;

        string message = action switch
        {
            CaptureCompletionAction.Copy when copied => "截图已复制到剪贴板",
            CaptureCompletionAction.Copy => "无法复制截图，请稍后重试",
            CaptureCompletionAction.Save when savedFilePath is not null => $"截图已保存到 {savedFilePath}",
            CaptureCompletionAction.ScrollCapture when copied && savedFilePath is not null => "长截图已复制并保存",
            CaptureCompletionAction.ScrollCapture when copied => "长截图已复制到剪贴板",
            CaptureCompletionAction.ScrollCapture when savedFilePath is not null => $"长截图已保存到 {savedFilePath}",
            CaptureCompletionAction.ScrollCapture => "长截图已完成",
            CaptureCompletionAction.Default when copied && savedFilePath is not null => "截图已复制并保存",
            CaptureCompletionAction.Default when copied => "截图已复制到剪贴板",
            CaptureCompletionAction.Default when savedFilePath is not null => $"截图已保存到 {savedFilePath}",
            _ => "截图已完成"
        };
        ShowWindowsNotification(800, "ZSnaper", message, ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _updateCancellation.Cancel();
        _updateTimer.Stop();
        _tray.Visible = false;
        ExitThread();
    }

    private Icon CurrentWindowIcon => ThemeManager.CurrentMode == ThemeMode.Dark
        ? _darkAppIcon
        : _lightAppIcon;

    private Icon CurrentTrayIcon => ThemeManager.CurrentMode == ThemeMode.Dark
        ? _darkTrayIcon
        : _lightTrayIcon;

    private void ApplyThemeIcon()
    {
        Icon windowIcon = CurrentWindowIcon;
        _mainForm.Icon = windowIcon;
        _overlay.Icon = windowIcon;
        if (_result is not null)
        {
            _result.Icon = windowIcon;
        }
        foreach (PinnedImageForm pinned in _pinnedImages.ToArray())
        {
            if (!pinned.IsDisposed) pinned.Icon = windowIcon;
        }
        _tray.Icon = CurrentTrayIcon;
        ApplyTrayMenuTheme();
    }

    private void ApplyTrayMenuTheme()
    {
        _trayMenu.ApplyTheme();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ThemeManager.ThemeChanged -= ApplyThemeIcon;
            ConfigService.ConfigChanged -= OnUpdateConfigChanged;
            ConfigService.ConfigChanged -= ApplyConfiguredTrayIcon;
            ConfigService.ConfigChanged -= ApplyHotkeyMenuShortcuts;
            _updateCancellation.Cancel();
            _tray.MouseClick -= HandleTrayMouseClick;
            _tray.MouseDoubleClick -= HandleTrayMouseDoubleClick;
            _trayPrimaryClickTimer.Stop();
            _trayPrimaryClickTimer.Dispose();
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _trayMenu.Dispose();
            _tray.Dispose();
            _overlay.Dispose();
            _result?.Dispose();
            foreach (PinnedImageForm pinned in _pinnedImages.ToArray()) pinned.Dispose();
            _pinnedImages.Clear();
            _mainForm.Dispose();
            _pluginManager.Dispose();
            _hotkeyService.Dispose();
            _updateCancellation.Dispose();
            _lightTrayIcon.Dispose();
            _darkTrayIcon.Dispose();
            _lightAppIcon.Dispose();
            _darkAppIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string ShortenError(string? message)
    {
        const int maxLength = 140;
        string normalized = string.IsNullOrWhiteSpace(message)
            ? "未知错误"
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    private void ApplyConfiguredTrayIcon()
    {
        string settingsKey = AppIconProvider.GetTrayIconSettingsKey();
        if (string.Equals(settingsKey, _appliedTrayIconSettingsKey, StringComparison.Ordinal))
        {
            return;
        }

        Icon? nextLight = null;
        Icon? nextDark = null;
        try
        {
            nextLight = AppIconProvider.CreateTrayIcon(ThemeMode.Light);
            nextDark = AppIconProvider.CreateTrayIcon(ThemeMode.Dark);
        }
        catch
        {
            nextLight?.Dispose();
            nextDark?.Dispose();
            return;
        }

        Icon previousLight = _lightTrayIcon;
        Icon previousDark = _darkTrayIcon;
        _lightTrayIcon = nextLight;
        _darkTrayIcon = nextDark;
        _appliedTrayIconSettingsKey = settingsKey;
        _tray.Icon = CurrentTrayIcon;
        previousLight.Dispose();
        previousDark.Dispose();
    }
}
