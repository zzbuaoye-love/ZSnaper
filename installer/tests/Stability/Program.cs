using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using ZSnaper.Controls;
using ZSnaper.Forms;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;
using ZSnaper.Update;

namespace ZSnaper.Stability;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--live-update-download", StringComparer.OrdinalIgnoreCase))
        {
            TestLiveUpdateDownload();
            return 0;
        }

        TestConfigurationNormalization();
        TestUpdateChannelAndControl();
        TestUpdateAssetSelectionAndChecksums();
        TestInstalledLanguagePackResolution();
        TestToolbarItemOrderAndRepair();
        TestForceHotkeyValidation();
        TestExpandedHotkeyCatalog();
        TestHotkeyClearingAndRestoration();
        TestHotkeySearchBox();
        TestHotkeyPageUi();
        TestHotkeyRecordingLifecycle();
        TestSingleInstanceActivation();
        TestAtomicConfigurationRecovery();
        TestCaptureResourceGuards();
        TestSmartSelectionDragInvalidation();
        TestPinnedImageRendering();
        Console.WriteLine("Application stability tests passed.");
        return 0;
    }

    private static void TestForceHotkeyValidation()
    {
        var delete = new HotkeyGesture(Keys.Delete, Keys.None);
        Assert(!delete.IsValid, "Standalone Delete unexpectedly became a normal hotkey.");
        Assert(delete.IsValidForForceBinding, "Standalone Delete was rejected for force binding.");
        Assert(
            !HotkeyGesture.TryParse("Delete", out _),
            "Standalone Delete unexpectedly parsed as a normal hotkey.");
        Assert(
            HotkeyGesture.TryParse("Delete", out HotkeyGesture parsedDelete, forceBinding: true) &&
            parsedDelete == delete,
            "Standalone Delete did not parse as a force-bound hotkey.");
        Assert(
            !new HotkeyGesture(Keys.Escape, Keys.None).IsValidForForceBinding,
            "Escape must remain reserved for cancelling recording.");
        Assert(
            !HotkeyGesture.TryParse("Q+X", out _, forceBinding: true),
            "A hotkey with multiple trigger keys was accepted.");

        var config = new AppConfig
        {
            CaptureHotkey = "Delete",
            CaptureHotkeyForceBinding = true,
            OcrHotkey = "Alt+X"
        };
        AppConfigSanitizer.Normalize(config);
        Assert(
            config.CaptureHotkey == "Delete" && config.CaptureHotkeyForceBinding,
            "Configuration normalization discarded a force-bound standalone Delete key.");
    }

    private static void TestExpandedHotkeyCatalog()
    {
        Assert(HotkeyCommandCatalog.Definitions.Count == 8, "The configurable hotkey command list is incomplete.");
        Assert(
            HotkeyCommandCatalog.Matches(
                HotkeyCommandCatalog.GetDefinition(HotkeyCommand.CaptureAndPin),
                "贴图"),
            "Hotkey search did not match a command keyword.");
        Assert(
            HotkeyCommandCatalog.Matches(
                HotkeyCommandCatalog.GetDefinition(HotkeyCommand.OpenMainWindow),
                "Ctrl + Alt + Z",
                "Ctrl + Alt + Z"),
            "Hotkey search did not match the configured gesture text.");

        Assert(
            HotkeyCommandCatalog.Matches(
                HotkeyCommandCatalog.GetDefinition(HotkeyCommand.CaptureAndPin),
                "截图 贴图"),
            "Hotkey search did not match multi-token query.");
        Assert(
            HotkeyCommandCatalog.Matches(
                HotkeyCommandCatalog.GetDefinition(HotkeyCommand.ToggleTheme),
                "未设置",
                "未设置 未绑定 unassigned none"),
            "Hotkey search did not match unassigned shortcut text.");

        var config = new AppConfig
        {
            CaptureHotkey = "Alt+Q",
            OcrHotkey = "Alt+X",
            CaptureAndPinHotkey = "Ctrl+Alt+P",
            CaptureCurrentScreenHotkey = "Alt+Q",
            PinClipboardImageHotkey = "not-a-key",
            OpenMainWindowHotkey = string.Empty,
            OpenMainWindowHotkeyForceBinding = true
        };
        AppConfigSanitizer.Normalize(config);
        Assert(config.CaptureAndPinHotkey == "Ctrl+Alt+P", "A valid expanded hotkey was discarded.");
        Assert(config.CaptureCurrentScreenHotkey.Length == 0, "A duplicate expanded hotkey was retained.");
        Assert(config.PinClipboardImageHotkey.Length == 0, "An invalid expanded hotkey was retained.");
        Assert(
            config.OpenMainWindowHotkey.Length == 0 && !config.OpenMainWindowHotkeyForceBinding,
            "An unassigned expanded hotkey retained force-binding state.");
    }

    private static void TestHotkeyClearingAndRestoration()
    {
        using var service = new HotkeyService();
        service.RegisterConfiguredHotkeys(out _, out _);
        var gesture = new HotkeyGesture(Keys.F11, Keys.Control | Keys.Alt);
        var updateResult = service.TryUpdateHotkey(HotkeyCommand.ToggleTheme, gesture);
        Assert(updateResult.Success, "Failed to assign hotkey for ToggleTheme in test: " + updateResult.Message);
        Assert(service.GetGesture(HotkeyCommand.ToggleTheme) == gesture, "Gesture was not set.");

        var clearResult = service.TryClearHotkey(HotkeyCommand.ToggleTheme);
        Assert(clearResult.Success, "Failed to clear hotkey: " + clearResult.Message);
        Assert(service.GetGesture(HotkeyCommand.ToggleTheme) is null, "Gesture was not cleared.");
        Assert(string.IsNullOrEmpty(ConfigService.Current.ToggleThemeHotkey), "Config was not cleared.");
    }

    private static void TestHotkeySearchBox()
    {
        using var searchBox = new HotkeySearchBox();
        searchBox.Text = "贴图";
        Assert(searchBox.Text == "贴图", "SearchBox text was not assigned.");
        searchBox.Clear();
        Assert(searchBox.Text.Length == 0, "SearchBox text was not cleared.");
    }

    private static void TestHotkeyPageUi()
    {
        using var form = new MainForm();
        form.Show();
        form.SwitchTab(2);
        Application.DoEvents();

        HotkeySearchBox? searchBox = null;
        ModernScrollPanel? scrollPanel = null;

        void FindControls(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is HotkeySearchBox sb) searchBox = sb;
                if (child is ModernScrollPanel sp && child.Parent is not ModernScrollPanel) scrollPanel = sp;
                FindControls(child);
            }
        }

        FindControls(form);
        Assert(searchBox is not null, "HotkeySearchBox was not found in MainForm.");
        scrollPanel = searchBox!.Parent?.Controls.OfType<ModernScrollPanel>().FirstOrDefault();
        Assert(scrollPanel is not null, "ModernScrollPanel was not found on Hotkeys page.");

        // Check initial state: 8 command cards + 1 fixedCard = 9 cards
        int initialVisibleCards = scrollPanel!.Content.Controls.OfType<ModernCard>().Count(c => c.Visible);
        Assert(initialVisibleCards == 9, "Expected 8 command cards + 1 fixedCard initially, found " + initialVisibleCards);

        // Search for "贴图" -> CaptureAndPin and PinClipboardImage
        searchBox.Text = "贴图";
        Application.DoEvents();
        int pinVisibleCards = scrollPanel.Content.Controls.OfType<ModernCard>().Count(c => c.Visible);
        Assert(pinVisibleCards == 2, "Expected 2 visible cards for '贴图' (CaptureAndPin and PinClipboardImage), found " + pinVisibleCards);

        // Clear search
        searchBox.Clear();
        Application.DoEvents();
        int restoredCards = scrollPanel.Content.Controls.OfType<ModernCard>().Count(c => c.Visible);
        Assert(restoredCards == 9, "Expected 9 visible cards after clearing search, found " + restoredCards);
        form.Hide();
    }

    private static void TestSingleInstanceActivation()
    {
        string scope = "ZSnaper.Stability." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(scope);
        Assert(primary.IsPrimary, "The first coordinator did not own the instance scope.");

        bool activated = false;
        primary.ActivationRequested += () => activated = true;
        primary.StartListening();

        using var secondary = new SingleInstanceCoordinator(scope);
        Assert(!secondary.IsPrimary, "A second coordinator incorrectly became primary.");
        Assert(secondary.NotifyPrimaryInstance(), "The second coordinator could not notify the primary instance.");

        var timeout = Stopwatch.StartNew();
        while (!activated && timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert(activated, "The primary instance did not receive the activation request.");
        primary.Dispose();
        Assert(secondary.TryBecomePrimary(), "A secondary instance could not take over after primary shutdown.");
    }

    private static void TestHotkeyRecordingLifecycle()
    {
        using var recorder = new HotkeyRecorder();
        int beginCount = 0;
        int endCount = 0;
        recorder.BeginRecordingRequest = () =>
        {
            beginCount++;
            return new HotkeyChangeResult(true, string.Empty);
        };
        recorder.EndRecordingRequest = () =>
        {
            endCount++;
            return new HotkeyChangeResult(true, string.Empty);
        };

        recorder.StartRecording();
        Assert(recorder.IsRecording, "Hotkey recording did not start.");

        recorder.StartRecording();
        Assert(beginCount == 1 && endCount == 0, "Starting an active recorder created a duplicate recording session.");

        recorder.CancelExternalRecording();
        Assert(!recorder.IsRecording && endCount == 1, "Cancelling recording did not close the active session.");

        var occupied = new HotkeyChangeResult(false, "occupied", HotkeyChangeFailure.Occupied);
        var registration = new HotkeyChangeResult(false, "failed", HotkeyChangeFailure.Registration);
        Assert(occupied.CanForce && !registration.CanForce, "Force fallback was offered for the wrong failure type.");
    }

    private static void TestConfigurationNormalization()
    {
        var config = new AppConfig
        {
            Theme = (ThemeMode)999,
            AnimationMode = (AnimationLevel)999,
            ToolbarPlacement = (ToolbarPlacementMode)999,
            CaptureToolbarLayout = CaptureToolbarLayout.Custom,
            CaptureToolbarItems = [CaptureToolbarItem.Copy, CaptureToolbarItem.Copy, (CaptureToolbarItem)999],
            CaptureToolbarOrder = [CaptureToolbarItem.Copy, CaptureToolbarItem.Copy, (CaptureToolbarItem)999],
            ConfirmButtonBehavior = (ConfirmButtonBehavior)999,
            AnnotationToolBehavior = (AnnotationToolBehavior)999,
            AnnotationArrowStyle = (AnnotationArrowStyle)999,
            AccentColorHex = "not-a-color",
            AnnotationColorHex = "#123",
            ToolbarAutoHorizontalBias = double.NaN,
            ToolbarAutoSampleCount = int.MaxValue,
            AnnotationFontSize = float.PositiveInfinity,
            AnnotationPenWidth = -10,
            AnnotationMosaicSize = 999,
            AnnotationMosaicPixelSize = -1,
            AnnotationFontStyle = int.MaxValue,
            CaptureHotkey = "invalid",
            OcrHotkey = "Alt+Q",
            TrayLeftClickAction = (TrayClickAction)999,
            TrayMiddleClickAction = (TrayClickAction)999,
            UpdateCheckIntervalHours = 1,
            LastUpdateCheckAt = DateTimeOffset.UtcNow.AddDays(3),
            TrayIconCustomPalette = ["invalid", "#ffffff", "#FFFFFF"]
        };

        AppConfigSanitizer.Normalize(config);

        Assert(config.Theme == ThemeMode.Light, "Invalid theme was not repaired.");
        Assert(config.AnimationMode == AnimationLevel.Balanced, "Invalid animation mode was not repaired.");
        Assert(config.ToolbarPlacement == ToolbarPlacementMode.Auto, "Invalid toolbar placement was not repaired.");
        Assert(config.ConfirmButtonBehavior == ConfirmButtonBehavior.Copy, "Invalid confirm behavior was not repaired.");
        Assert(config.AccentColorHex == "#10B981", "Invalid accent color was not repaired.");
        Assert(double.IsFinite(config.ToolbarAutoHorizontalBias), "Non-finite toolbar bias survived normalization.");
        Assert(config.ToolbarAutoSampleCount == 10_000, "Toolbar sample count was not bounded.");
        Assert(config.AnnotationFontSize == 18f, "Non-finite annotation font size was not repaired.");
        Assert(config.AnnotationPenWidth == 1f && config.AnnotationMosaicSize == 80f, "Annotation dimensions were not bounded.");
        Assert(config.AnnotationFontStyle == (int)FontStyle.Regular, "Invalid font style survived normalization.");
        Assert(config.CaptureHotkey == "Alt+Q" && config.OcrHotkey == "Alt+X", "Invalid or duplicate hotkeys were not repaired.");
        Assert(
            config.TrayLeftClickAction == TrayClickAction.OpenMainWindow &&
            config.TrayMiddleClickAction == TrayClickAction.Capture,
            "Invalid tray click actions were not repaired.");
        Assert(config.UpdateCheckIntervalHours == 24 && config.LastUpdateCheckAt is null, "Invalid update schedule survived normalization.");
        Assert(config.CaptureToolbarOrder.Distinct().Count() == CaptureToolbarDefaults.CreateItems().Count, "Toolbar order was not repaired.");
        Assert(config.CaptureToolbarItems.SequenceEqual([CaptureToolbarItem.Copy]), "Custom toolbar selection was not preserved safely.");
        Assert(config.TrayIconCustomPalette.SequenceEqual(["#FFFFFF"]), "Tray palette was not normalized and deduplicated.");
    }

    private static void TestUpdateChannelAndControl()
    {
        // 1. AppConfigSanitizer normalization
        var configAlpha = new AppConfig { UpdateChannel = "Alpha" };
        AppConfigSanitizer.Normalize(configAlpha);
        Assert(configAlpha.UpdateChannel == "Beta", "Channel Alpha was not migrated to Beta.");

        var configBeta = new AppConfig { UpdateChannel = "beta" };
        AppConfigSanitizer.Normalize(configBeta);
        Assert(configBeta.UpdateChannel == "Beta", "Channel beta was not normalized to Beta.");

        var configRelease = new AppConfig { UpdateChannel = "release" };
        AppConfigSanitizer.Normalize(configRelease);
        Assert(configRelease.UpdateChannel == "Release", "Channel release was not normalized to Release.");

        var configStable = new AppConfig { UpdateChannel = "Stable" };
        AppConfigSanitizer.Normalize(configStable);
        Assert(configStable.UpdateChannel == "Release", "Channel Stable was not normalized to Release.");

        var configInvalid = new AppConfig { UpdateChannel = "invalid_channel_xyz" };
        AppConfigSanitizer.Normalize(configInvalid);
        Assert(configInvalid.UpdateChannel == "Beta", "Invalid channel was not defaulted to Beta.");

        // 2. ChannelSegmentedControl mapping
        Assert(ChannelSegmentedControl.ChannelToIndex("Release") == 0, "Release did not map to index 0.");
        Assert(ChannelSegmentedControl.ChannelToIndex("stable") == 0, "stable did not map to index 0.");
        Assert(ChannelSegmentedControl.ChannelToIndex("Beta") == 1, "Beta did not map to index 1.");
        Assert(ChannelSegmentedControl.ChannelToIndex("Alpha") == 1, "Alpha did not map to index 1.");
        Assert(ChannelSegmentedControl.ChannelToIndex(null) == 1, "null channel did not map to index 1.");

        Assert(ChannelSegmentedControl.IndexToChannel(0) == "Release", "Index 0 did not map to Release.");
        Assert(ChannelSegmentedControl.IndexToChannel(1) == "Beta", "Index 1 did not map to Beta.");

        // 3. ChannelSegmentedControl UI properties
        using var control = new ChannelSegmentedControl();
        Assert(control.Width == 136 && control.Height == 28, "ChannelSegmentedControl size is incorrect.");

        // 4. AppVersionInfo build channel and version
        Assert(AppVersionInfo.Version == "0.0.5", "AppVersionInfo.Version should be 0.0.5.");
        Assert(AppVersionInfo.DisplayVersion == "0.0.5-beta", $"AppVersionInfo.DisplayVersion expected 0.0.5-beta, got {AppVersionInfo.DisplayVersion}.");
        Assert(AppVersionInfo.BuildChannel == "Beta", "AppVersionInfo.BuildChannel should be Beta for prerelease build.");
        Assert(AppVersionInfo.WelcomeChannelLabel == "BETA", "AppVersionInfo.WelcomeChannelLabel should be BETA.");
        Assert(!AppVersionInfo.IsReleaseBuild, "Prerelease build was identified as Release.");
    }

    private static void TestToolbarItemOrderAndRepair()
    {
        // 1. Simulate the exact legacy config where enum shifted:
        // Confirm (13) was saved between Cursor (6) and Ocr (7), and ScrollCapture (14) at the end.
        var legacyConfig = new AppConfig
        {
            CaptureToolbarLayout = CaptureToolbarLayout.Custom,
            CaptureToolbarOrder =
            [
                (CaptureToolbarItem)0, // Pen
                (CaptureToolbarItem)1, // Arrow
                (CaptureToolbarItem)2, // Text
                (CaptureToolbarItem)3, // Mosaic
                (CaptureToolbarItem)4, // Style
                (CaptureToolbarItem)5, // Undo
                (CaptureToolbarItem)6, // Cursor
                (CaptureToolbarItem)13, // was ScrollCapture in old enum, now Confirm
                (CaptureToolbarItem)7, // Ocr
                (CaptureToolbarItem)8, // Copy
                (CaptureToolbarItem)9, // Save
                (CaptureToolbarItem)10, // Pin
                (CaptureToolbarItem)11, // Reset
                (CaptureToolbarItem)12, // Cancel
                (CaptureToolbarItem)14  // ScrollCapture appended by defaults
            ],
            CaptureToolbarItems =
            [
                (CaptureToolbarItem)0,
                (CaptureToolbarItem)1,
                (CaptureToolbarItem)2,
                (CaptureToolbarItem)3,
                (CaptureToolbarItem)4,
                (CaptureToolbarItem)5,
                (CaptureToolbarItem)6,
                (CaptureToolbarItem)13,
                (CaptureToolbarItem)7,
                (CaptureToolbarItem)8,
                (CaptureToolbarItem)9,
                (CaptureToolbarItem)10,
                (CaptureToolbarItem)11,
                (CaptureToolbarItem)12
            ]
        };

        AppConfigSanitizer.Normalize(legacyConfig);

        // Confirm must be at the very end (index 14)
        Assert(
            legacyConfig.CaptureToolbarOrder[^1] == CaptureToolbarItem.Confirm,
            "Confirm was not moved to the end of CaptureToolbarOrder.");
        Assert(
            legacyConfig.CaptureToolbarOrder[^2] == CaptureToolbarItem.Cancel,
            "Cancel was not placed immediately before Confirm in CaptureToolbarOrder.");

        // ScrollCapture must be restored to its rightful place between Cursor and Ocr (index 7)
        Assert(
            legacyConfig.CaptureToolbarOrder[7] == CaptureToolbarItem.ScrollCapture,
            "ScrollCapture was not restored to index 7 in CaptureToolbarOrder.");
        Assert(
            legacyConfig.CaptureToolbarOrder[6] == CaptureToolbarItem.Cursor &&
            legacyConfig.CaptureToolbarOrder[8] == CaptureToolbarItem.Ocr,
            "Cursor/Ocr neighborhood is incorrect in CaptureToolbarOrder.");

        // CaptureToolbarItems must also have ScrollCapture at index 7 and Confirm at the end
        Assert(
            legacyConfig.CaptureToolbarItems[^1] == CaptureToolbarItem.Confirm,
            "Confirm was not at the end of CaptureToolbarItems.");
        Assert(
            legacyConfig.CaptureToolbarItems[^2] == CaptureToolbarItem.Cancel,
            "Cancel was not immediately before Confirm in CaptureToolbarItems.");
        Assert(
            legacyConfig.CaptureToolbarItems[7] == CaptureToolbarItem.ScrollCapture,
            "ScrollCapture was not restored to index 7 in CaptureToolbarItems.");

        // Layout was restored to Full because all 15 defaults are now present
        Assert(
            legacyConfig.CaptureToolbarLayout == CaptureToolbarLayout.Full,
            "CaptureToolbarLayout was not restored to Full.");

        // 2. Custom partial layout where Confirm was placed in the middle
        var customConfig = new AppConfig
        {
            CaptureToolbarLayout = CaptureToolbarLayout.Custom,
            CaptureToolbarItems = [CaptureToolbarItem.Confirm, CaptureToolbarItem.Pen, CaptureToolbarItem.Cancel],
            CaptureToolbarOrder = CaptureToolbarDefaults.CreateItems()
        };
        AppConfigSanitizer.Normalize(customConfig);
        Assert(
            customConfig.CaptureToolbarItems.SequenceEqual([CaptureToolbarItem.Pen, CaptureToolbarItem.Cancel, CaptureToolbarItem.Confirm]),
            "Confirm and Cancel were not positioned at the end of custom items.");
    }

    private static void TestAtomicConfigurationRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ZSnaper-Stability-" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(directory, "config.json");
        string backupPath = configPath + ".bak";
        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            string first = JsonSerializer.Serialize(new AppConfig { Theme = ThemeMode.Light }, options);
            ConfigFileStore.WriteAtomic(configPath, first, backupPath, backupExisting: false);
            string second = JsonSerializer.Serialize(new AppConfig { Theme = ThemeMode.Dark }, options);
            ConfigFileStore.WriteAtomic(configPath, second, backupPath, backupExisting: true);

            Assert(ConfigFileStore.TryRead(configPath, options, out AppConfig current) && current.Theme == ThemeMode.Dark,
                "Atomic write did not publish the new configuration.");
            Assert(ConfigFileStore.TryRead(backupPath, options, out AppConfig backup) && backup.Theme == ThemeMode.Light,
                "Atomic write did not retain the previous configuration.");

            File.WriteAllText(configPath, "{ broken json");
            Assert(!ConfigFileStore.TryRead(configPath, options, out _), "Corrupted configuration was accepted.");
            Assert(ConfigFileStore.TryRead(backupPath, options, out backup) && backup.Theme == ThemeMode.Light,
                "Backup configuration was not recoverable.");
            Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic writer left a temporary file behind.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestCaptureResourceGuards()
    {
        bool rejected = false;
        try
        {
            using Bitmap _ = CaptureService.CaptureScreen(Rectangle.Empty);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        Assert(rejected, "An empty screen capture was not rejected before allocating GDI resources.");

        string directory = Path.Combine(Path.GetTempPath(), "ZSnaper-Capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var bitmap = new Bitmap(2, 2);
            string first = CaptureService.SaveToDirectory(bitmap, directory);
            string second = CaptureService.SaveToDirectory(bitmap, directory);
            Assert(first != second && File.Exists(first) && File.Exists(second), "Rapid saves reused a file name or lost output.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestPinnedImageRendering()
    {
        using var source = new Bitmap(320, 180);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.FromArgb(24, 160, 220));
        }

        using var pinned = new PinnedImageForm(source, new Point(120, 120));
        Assert(pinned.ClientSize.Width > 0 && pinned.ClientSize.Height > 0, "Pinned image window has invalid bounds.");
        using var rendered = new Bitmap(pinned.ClientSize.Width, pinned.ClientSize.Height);
        pinned.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
        Color center = rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
        Assert(center.G > center.R && center.B > center.R, "Pinned image window did not render its image content.");
    }

    private static void TestUpdateAssetSelectionAndChecksums()
    {
        GitHubRelease release = new()
        {
            TagName = "v1.2.3",
            Assets =
            [
                new GitHubReleaseAsset { Name = "ZSnaper-v1.2.3-win-x64-Update.zup", DownloadUrl = "https://example.test/app.zup" },
                new GitHubReleaseAsset { Name = "ZSnaper-v1.2.3-win-x64-Update.exe", DownloadUrl = "https://example.test/update.exe" },
                new GitHubReleaseAsset { Name = "SHA256SUMS.txt", DownloadUrl = "https://example.test/hashes" }
            ]
        };
        Assert(AppUpdateService.SelectPackageAsset(release).Name.EndsWith(".zup"), "The app did not select the .zup update package.");
        Assert(AppUpdateService.SelectUpdaterAsset(release).Name.EndsWith("Update.exe"), "The app did not select the updater executable.");
        Assert(AppUpdateService.SelectChecksumAsset(release).Name == "SHA256SUMS.txt", "The app did not select the checksum list.");

        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        IReadOnlyDictionary<string, string> parsed = AppUpdateService.ParseChecksums($"{hash}  package.zup\r\n");
        Assert(parsed.TryGetValue("package.zup", out string? value) && value == hash, "SHA256SUMS parsing failed.");
    }

    private static void TestInstalledLanguagePackResolution()
    {
        string applicationDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string installDirectory = Directory.Exists(Path.Combine(applicationDirectory, "langs"))
            ? applicationDirectory
            : Directory.GetParent(applicationDirectory)?.FullName ?? applicationDirectory;
        string languageAssembly = Path.Combine(installDirectory, "langs", "zh-Hans", "System.Windows.Forms.resources.dll");
        if (!File.Exists(languageAssembly))
        {
            return;
        }

        Type resolver = typeof(AppVersionInfo).Assembly.GetType("ZSnaper.Helpers.SatelliteAssemblyResolver", throwOnError: true)!;
        MethodInfo method = resolver.GetMethod("ResolveSatelliteAssembly", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(resolver.FullName, "ResolveSatelliteAssembly");
        AssemblyName name = new("System.Windows.Forms.resources") { CultureName = "zh-Hans" };
        var context = new System.Runtime.Loader.AssemblyLoadContext("LanguagePackTest", isCollectible: true);
        try
        {
            Assembly? loaded = method.Invoke(null, [context, name]) as Assembly;
            Assert(
                loaded is not null && string.Equals(loaded.Location, languageAssembly, StringComparison.OrdinalIgnoreCase),
                $"The installed language pack was not loaded from the langs directory. Expected={languageAssembly}; Actual={loaded?.Location ?? "<null>"}.");
        }
        finally
        {
            context.Unload();
        }
    }

    private static void TestLiveUpdateDownload()
    {
        GitHubRelease release = VersionGet.GetLatestReleaseAsync(includePrerelease: true).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("No current GitHub release was found.");
        PreparedAppUpdate update = new AppUpdateService().PrepareAsync(release).GetAwaiter().GetResult();
        Assert(File.Exists(update.PackagePath), "The live .zup package was not downloaded.");
        Assert(File.Exists(update.UpdaterPath), "The live updater was not downloaded.");
        Console.WriteLine($"Live update download passed for {release.TagName}.");
    }

    private static void TestSmartSelectionDragInvalidation()
    {
        using var overlay = new OverlayFormProbe { ClientSize = new Size(240, 180) };
        _ = overlay.Handle;

        var smartTarget = new SmartSelectionTarget(
            nint.Zero,
            new Rectangle(10, 10, 220, 160),
            "Target");
        SetOverlayField(overlay, "_pendingSmartClick", true);
        SetOverlayField(overlay, "_start", new Point(20, 20));
        SetOverlayField(overlay, "_smartTarget", smartTarget);
        SetOverlayField(overlay, "_smartFastTarget", smartTarget);

        var invalidatedBounds = new List<Rectangle>();
        overlay.Invalidated += (_, args) => invalidatedBounds.Add(args.InvalidRect);
        overlay.RaiseMouseMove(new MouseEventArgs(MouseButtons.Left, 0, 120, 100, 0));

        Assert(
            invalidatedBounds.Any(bounds => bounds == overlay.ClientRectangle),
            "Dragging away from a smart target did not invalidate the full overlay, leaving stale undimmed pixels around the manual selection.");
    }

    private static void SetOverlayField(OverlayForm overlay, string name, object value)
    {
        FieldInfo field = typeof(OverlayForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OverlayForm).FullName, name);
        field.SetValue(overlay, value);
    }

    private sealed class OverlayFormProbe : OverlayForm
    {
        public void RaiseMouseMove(MouseEventArgs args) => base.OnMouseMove(args);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
