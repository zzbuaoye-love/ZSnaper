using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;
using ZSnaper.Controls;
using ZSnaper.Helpers;
using ZSnaper.Interop;
using ZSnaper.Models;
using ZSnaper.Services;
using ZSnaper.Plugins;

namespace ZSnaper.Forms;

public partial class MainForm : Form
{
    private readonly SkiaRasterLayer _mainSurface = new();
    private bool _mainSurfaceDirty = true;
    private bool _mainResourcesDisposed;
    private int _currentTabIndex = 0;
    private bool _hasSystemBackdrop;
    private bool _hasShownOnce;

    // 控件
    private readonly WindowControls _windowControls;
    private readonly SidebarNavigationPanel _sidebarPanel;
    private readonly List<NavMenuButton> _navButtons = [];
    private readonly PageTransitionOverlay _pageTransitionOverlay = new();
    private readonly ToolTip _sidebarToolTip = new() { ShowAlways = true, InitialDelay = 250, ReshowDelay = 120 };
    private SettingsTabBar? _settingsTabBar;
    private PageTransitionOverlay? _settingsTransitionOverlay;

    // 页面容器
    private readonly Panel _pageContainer;
    private readonly Panel[] _pages = new Panel[6];
    private ModernTextEditor _ocrTextBox = null!;
    private HeroActionCard _captureHeroCard = null!;
    private HeroActionCard _ocrHeroCard = null!;
    private int _homeCaptureCount;
    private int _homeOcrCount;
    private Label _homeStatsLabel = null!;
    private Label _homeLatestResultLabel = null!;
    private ModernButton _homeOpenResultButton = null!;
    private ModernButton _updateButton = null!;
    private SettingItemRow _lastUpdateRow = null!;
    private string? _homeLatestFilePath;
    private HotkeyService? _hotkeyStatusService;
    private readonly Dictionary<HotkeyCommand, Label> _hotkeyStatusLabels = [];
    private string? _latestUpdateUrl;
    private readonly CancellationTokenSource _welcomeQuoteCancellation = new();
    private string _welcomeQuote = "保持好奇，保持创造";
    private string? _welcomeQuoteSource = "ZSnaper";

    // 事件
    public event Action<bool>? RequestCapture;
    public event Func<HotkeyCommand, HotkeyGesture, HotkeyBindingMode, HotkeyChangeResult>? RequestHotkeyChange;
    public event Func<HotkeyCommand, HotkeyChangeResult>? RequestHotkeyClear;
    public event Func<HotkeyCommand, HotkeyChangeResult>? RequestHotkeyRecordingStart;
    public event Func<HotkeyCommand, HotkeyChangeResult>? RequestHotkeyRecordingStop;
    public event Action? RequestUpdateCheck;
    public event Action<string>? RequestOpenUpdate;

    public MainForm()
    {
        Text = "ZSnaper";
#if DEBUG
        Text += " · DEV";
#endif
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 520);
        MinimumSize = new Size(780, 480);
        BackColor = ThemeManager.Palette.BackgroundColor;
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // 1. 窗口控制按钮
        _windowControls = new WindowControls
        {
            Location = new Point(Width - 86, 15),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        // 2. 左侧侧边栏导航面板
        _sidebarPanel = new SidebarNavigationPanel
        {
            Location = new Point(14, 66),
            Size = new Size(72, Height - 84),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
        };

        // 侧边栏按钮列表
        (LucideIcon icon, string label, bool isBottom)[] menuItems = [
            (LucideIcon.Camera, "截图", false),
            (LucideIcon.FileText, "文字", false),
            (LucideIcon.Keyboard, "快捷键", false),
            (LucideIcon.Grid3X3, "插件", false),
            (LucideIcon.Sliders, "设置", true),
            (LucideIcon.Info, "关于", true)
        ];

        for (int i = 0; i < menuItems.Length; i++)
        {
            int index = i;
            var item = menuItems[i];
            var btn = new NavMenuButton
            {
                Icon = item.icon,
                LabelText = item.label,
                Size = new Size(72, 54),
                AccessibleName = item.label,
                IsActive = i == 0
            };

            btn.Click += (_, _) => SwitchTab(index);
            _navButtons.Add(btn);
            _sidebarPanel.RegisterButton(btn);
        }

        // 3. 右侧主内容区域
        int initialContentLeft = GetSidebarContentLeft(ConfigService.Current.ShowSidebarLabels);
        _pageContainer = new BufferedPanel
        {
            BackColor = Color.Transparent,
            Location = new Point(initialContentLeft, 74),
            Size = new Size(Width - (initialContentLeft + 24), Height - 98),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(0)
        };
        _pageContainer.Controls.Add(_pageTransitionOverlay);

        // 初始化各个页面
        InitPages();
        ApplySidebarLayout(snapPill: true);
        _sidebarPanel.SetActiveIndex(0, animate: false);

        // 添加控件到窗体
        Controls.Add(_windowControls);
        Controls.Add(_sidebarPanel);
        Controls.Add(_pageContainer);

        // 注册全局主题变更监听
        ThemeManager.ThemeChanged += OnThemeChanged;
        Shown += (_, _) =>
        {
            _hasShownOnce = true;
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                foreach (Panel page in _pages)
                {
                    if (page is not null) EnsureHandlesCreated(page);
                }
            });
        };
        Disposed += (_, _) =>
        {
            if (_mainResourcesDisposed) return;
            _mainResourcesDisposed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            if (_hotkeyStatusService is not null) _hotkeyStatusService.StatusChanged -= RefreshHotkeyStatus;
            _welcomeQuoteCancellation.Cancel();
            _welcomeQuoteCancellation.Dispose();
            _mainSurface.Dispose();
            _recentToolTip.Dispose();
            _sidebarToolTip.Dispose();
        };

        // 加载时启用 Windows 11 原生圆角与 DWM 阴影
        Load += (_, _) =>
        {
            UpdateDwmEffect();
            _ = LoadReleaseQuoteAsync();
        };
    }

    public void AttachHotkeyService(HotkeyService service)
    {
        if (_hotkeyStatusService is not null) _hotkeyStatusService.StatusChanged -= RefreshHotkeyStatus;
        _hotkeyStatusService = service;
        service.StatusChanged += RefreshHotkeyStatus;
        RefreshHotkeyStatus();
    }

    private void RefreshHotkeyStatus()
    {
        if (IsDisposed || _hotkeyStatusService is null) return;
        if (InvokeRequired) { BeginInvoke(RefreshHotkeyStatus); return; }
        foreach (var definition in HotkeyCommandCatalog.Definitions)
            if (_hotkeyStatusLabels.TryGetValue(definition.Command, out Label? label))
                label.Text = $"{_hotkeyStatusService.GetStatusText(definition.Command)} · {definition.Description}";
    }

    private async Task LoadReleaseQuoteAsync()
    {
        if (!AppVersionInfo.IsReleaseBuild)
        {
            return;
        }

        try
        {
            HitokotoSentence? quote = await HitokotoService.FetchAsync(_welcomeQuoteCancellation.Token);
            if (quote is null || IsDisposed || Disposing)
            {
                return;
            }

            _welcomeQuote = quote.Text;
            _welcomeQuoteSource = quote.Source;
            _mainSurfaceDirty = true;
            Invalidate();
        }
        catch (OperationCanceledException)
        {
            // Closing the form cancels the optional network request.
        }
        catch (HttpRequestException)
        {
            // Keep the local sentence when the public service is unavailable.
        }
        catch (JsonException)
        {
            // Ignore malformed responses and keep the local sentence.
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("MainForm.LoadReleaseQuote", exception);
            // The optional welcome sentence must never affect the main window.
        }
    }

    private void UpdateDwmEffect()
    {
        NativeMethods.EnableWindowDropShadowAndRoundCorners(
            Handle,
            ThemeManager.CurrentMode == ThemeMode.Dark);
        bool enabled = false;
        if (_hasSystemBackdrop != enabled)
        {
            _hasSystemBackdrop = enabled;
            _mainSurfaceDirty = true;
        }
    }

    private void OnThemeChanged()
    {
        BackColor = ThemeManager.Palette.BackgroundColor;
        _mainSurfaceDirty = true;
        _pageTransitionOverlay.CompleteImmediately();
        _settingsTransitionOverlay?.CompleteImmediately();

        if (_ocrTextBox != null && !_ocrTextBox.IsDisposed)
        {
            _ocrTextBox.ApplyTheme();
        }

        UpdateDwmEffect();
        Invalidate(true);
    }

    public void CompleteTransitions()
    {
        _pageTransitionOverlay.CompleteImmediately();
        _settingsTransitionOverlay?.CompleteImmediately();
        _sidebarPanel.SnapPillToActive();
        _settingsTabBar?.CompleteAnimations();
    }

    private static int GetSidebarRailWidth(bool showLabels) => showLabels ? 80 : 62;
    private static int GetSidebarContentLeft(bool showLabels) => showLabels ? 114 : 96;

    private void ApplySidebarLayout(bool snapPill)
    {
        int D(int value) => GraphicsHelper.Dip(this, value);
        bool showLabels = ConfigService.Current.ShowSidebarLabels;
        int panelWidth = D(showLabels ? 72 : 54);
        int btnHeight = D(showLabels ? 54 : 46);
        int stepY = D(showLabels ? 58 : 50);
        int contentLeft = D(GetSidebarContentLeft(showLabels));

        _sidebarPanel.SuspendLayout();
        _sidebarPanel.SetBounds(D(14), D(66), panelWidth, Math.Max(1, ClientSize.Height - D(84)));
        int topIndex = 0;
        int bottomStartIndex = _navButtons.Count - 2;
        for (int i = 0; i < _navButtons.Count; i++)
        {
            NavMenuButton btn = _navButtons[i];
            bool isBottom = i >= bottomStartIndex;
            btn.ShowLabel = showLabels;
            btn.Size = new Size(panelWidth, btnHeight);
            btn.Anchor = isBottom ? AnchorStyles.Bottom | AnchorStyles.Left : AnchorStyles.Top | AnchorStyles.Left;
            int y = isBottom
                ? _sidebarPanel.Height - (_navButtons.Count - i) * stepY
                : topIndex++ * stepY;
            btn.Location = new Point(0, y);
            _sidebarToolTip.SetToolTip(btn, showLabels ? string.Empty : btn.LabelText);
        }
        _sidebarPanel.ResumeLayout(performLayout: true);

        _pageContainer.SetBounds(
            contentLeft,
            D(74),
            Math.Max(1, ClientSize.Width - D(GetSidebarContentLeft(showLabels) + 24)),
            Math.Max(1, ClientSize.Height - D(98)));
        if (_currentTabIndex >= 0 && _currentTabIndex < _pages.Length && _pages[_currentTabIndex] is { } currentPage)
        {
            currentPage.Bounds = _pageContainer.ClientRectangle;
        }
        _windowControls.Location = new Point(ClientSize.Width - D(86), D(15));

        if (snapPill)
        {
            _sidebarPanel.SnapPillToActive();
        }
        else
        {
            _sidebarPanel.SetActiveIndex(_currentTabIndex, animate: false);
        }

        _mainSurfaceDirty = true;
        Invalidate();
    }

    public void SwitchTab(int index)
    {
        if (index < 0 || index >= _pages.Length || _pages[index] is null)
        {
            return;
        }

        Panel selectedPage = _pages[index];
        if (_currentTabIndex == index &&
            selectedPage.Parent == _pageContainer &&
            selectedPage.Visible)
        {
            return;
        }

        // _pageTransitionOverlay stays shielded across rapid mid-flight tab switches.
        bool animate = _hasShownOnce && Visible && _pageContainer.IsHandleCreated;
        int direction = index >= _currentTabIndex ? 1 : -1;
        Panel? oldPage = _currentTabIndex >= 0 && _currentTabIndex < _pages.Length ? _pages[_currentTabIndex] : null;
        Bitmap? outgoing = animate && oldPage is { Visible: true }
            ? PageTransitionOverlay.TryCaptureSnapshot(oldPage)
            : null;
        bool usingTransition = animate && outgoing is not null;

        if (usingTransition)
        {
            _pageTransitionOverlay.FreezeOutgoing(outgoing, _pageContainer.ClientRectangle);
        }

        _pageContainer.SuspendLayout();
        for (int i = 0; i < _pages.Length; i++)
        {
            Panel? page = _pages[i];
            if (page is not null)
            {
                page.Visible = i == index;
            }
        }

        // Hidden docked pages retain their previous bounds while the window resizes.
        selectedPage.Bounds = _pageContainer.ClientRectangle;
        if (!usingTransition)
        {
            selectedPage.BringToFront();
        }
        _currentTabIndex = index;
        _pageContainer.ResumeLayout(performLayout: true);

        if (usingTransition)
        {
            _pageTransitionOverlay.BringToFront();
            selectedPage.PerformLayout();
            Bitmap? incoming = PageTransitionOverlay.TryCaptureSnapshot(selectedPage);
            _sidebarPanel.SetActiveIndex(index, animate: true);
            _pageTransitionOverlay.BeginReveal(
                incoming,
                PageTransitionAxis.Vertical,
                direction);
        }
        else
        {
            _pageTransitionOverlay.CompleteImmediately();
            outgoing?.Dispose();
            _sidebarPanel.SetActiveIndex(index, animate);
        }
    }

    private static void EnsureHandlesCreated(Control control)
    {
        if (control.IsDisposed) return;
        _ = control.Handle;
        foreach (Control child in control.Controls)
        {
            EnsureHandlesCreated(child);
        }
    }

    private Panel CreateBasePage()
    {
        return new BufferedPanel
        {
            BackColor = Color.Transparent,
            Location = new Point(0, 0),
            Size = _pageContainer.ClientSize,
            Dock = DockStyle.Fill
        };
    }

    private void InitPages()
    {
        _pages[0] = CreateCapturePage();
        _pages[1] = CreateOcrStudioPage();
        _pages[2] = CreateHotkeysPage();
        _pages[3] = CreatePluginsPage();
        _pages[4] = CreateSettingsPage();
        _pages[5] = CreateAboutPage();

        _pageContainer.SuspendLayout();
        for (int index = 0; index < _pages.Length; index++)
        {
            Panel page = _pages[index];
            page.Visible = index == _currentTabIndex;
            _pageContainer.Controls.Add(page);
        }
        _pages[_currentTabIndex].BringToFront();
        _pageContainer.ResumeLayout(performLayout: false);
    }

    // 页面 1：快捷截图主面板
    private Panel CreateCapturePage()
    {
        Panel panel = CreateBasePage();
        Label heading = new()
        {
            Text = "截图",
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Regular),
            AutoSize = true,
            Location = Point.Empty
        };
        Label subtitle = new()
        {
            Text = "截取画面、标注重点，或提取图片中的文字",
            Font = new Font("Microsoft YaHei UI", 9.5f),
            AutoSize = true,
            Location = new Point(1, 44)
        };
        _captureHeroCard = new HeroActionCard
        {
            Icon = LucideIcon.Camera, Title = "区域截图",
            Description = "自由框选 · 智能选区 · 随手标注",
            IsPrimary = true, AccessibleName = "区域截图"
        };
        _ocrHeroCard = new HeroActionCard
        {
            Icon = LucideIcon.FileText, Title = "提取文字",
            Description = "识别图片文字，复制或继续编辑",
            IsPrimary = false, AccessibleName = "截图并 OCR"
        };
        _captureHeroCard.Click += (_, _) => RequestCapture?.Invoke(false);
        _ocrHeroCard.Click += (_, _) => RequestCapture?.Invoke(true);

        Label recentTitle = new()
        {
            Text = "最近结果", AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
        };
        ModernButton folderButton = new()
        {
            Text = "打开截图文件夹", Icon = LucideIcon.Folder,
            IsPrimary = false, CornerRadius = 20, Size = new Size(138, 32),
            Font = new Font("Microsoft YaHei UI", 8.5f)
        };
        folderButton.Click += (_, _) =>
        {
            string directory = ConfigService.GetEffectiveSavePath();
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = $"\"{directory}\"", UseShellExecute = true
            });
        };
        ModernCard recentCard = new() { CornerRadius = 12 };
        Label resultIcon = new() { Size = new Size(24, 24), Location = new Point(18, 17) };
        resultIcon.Paint += (_, e) => LucideRenderer.Draw(e.Graphics, LucideIcon.FileText,
            2, 2, 20, ThemeManager.Palette.TextMuted, 1.5f);
        _homeLatestResultLabel = new Label
        {
            Text = "完成截图后，可在这里查看最近保存的图片",
            Font = new Font("Microsoft YaHei UI", 9f),
            Location = new Point(54, 17), AutoEllipsis = true,
            Size = new Size(280, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _homeOpenResultButton = new ModernButton
        {
            Text = "查看", IsPrimary = false, CornerRadius = 20,
            Size = new Size(64, 28), Visible = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _homeOpenResultButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_homeLatestFilePath) || !File.Exists(_homeLatestFilePath)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_homeLatestFilePath}\"",
                UseShellExecute = true
            });
        };
        recentCard.Controls.AddRange([resultIcon, _homeLatestResultLabel, _homeOpenResultButton]);
        InitializeRecentCaptures(recentCard);
        Label engineStatus = new()
        {
            AutoSize = false, AutoEllipsis = true, Height = 20,
            Font = new Font("Microsoft YaHei UI", 8.2f)
        };
        _homeStatsLabel = new Label
        {
            Text = "本次运行  ·  截图 0  ·  识别 0",
            AutoSize = false, Height = 20,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Microsoft YaHei UI", 8.2f)
        };
        void LayoutHome()
        {
            int D(int value) => GraphicsHelper.Dip(panel, value);
            int width = Math.Max(D(320), panel.ClientSize.Width - D(2));
            int gap = D(12);
            bool compact = panel.ClientSize.Height < D(400);
            int heroHeight = D(compact ? 96 : 112);
            int secondaryHeight = D(compact ? 68 : 76);
            _captureHeroCard.SetBounds(0, D(76), width, heroHeight);
            _ocrHeroCard.SetBounds(0, _captureHeroCard.Bottom + gap, width, secondaryHeight);
            int recentTop = _ocrHeroCard.Bottom + D(16);
            recentTitle.Location = new Point(1, recentTop + D(4));
            folderButton.Location = new Point(width - folderButton.Width, recentTop);
            recentCard.SetBounds(0, recentTop + D(36), width, D(compact ? 48 : 56));
            resultIcon.Top = (recentCard.Height - resultIcon.Height) / 2;
            _homeLatestResultLabel.Top = (recentCard.Height - _homeLatestResultLabel.Height) / 2;
            _homeLatestResultLabel.Width = Math.Max(D(80), width - D(152));
            _homeOpenResultButton.Location = new Point(width - D(80), (recentCard.Height - _homeOpenResultButton.Height) / 2);
            LayoutRecentCaptures();
            int footerTop = Math.Max(recentCard.Bottom + D(4), panel.Height - D(20));
            engineStatus.SetBounds(0, footerTop, width / 2, D(20));
            _homeStatsLabel.SetBounds(width / 2, footerTop, width - width / 2, D(20));
        }
        void UpdateHomeConfiguration()
        {
            if (panel.IsDisposed) return;
            if (panel.InvokeRequired)
            {
                panel.BeginInvoke((Action)UpdateHomeConfiguration);
                return;
            }
            _captureHeroCard.ShortcutText = ConfigService.Current.CaptureHotkey;
            _ocrHeroCard.ShortcutText = ConfigService.Current.OcrHotkey;
            bool api = ConfigService.Current.OcrProvider == OcrProviderKind.OpenAiCompatible;
            engineStatus.Text = api ? "文字识别  ·  视觉模型 API" : "文字识别  ·  Windows 本地";
        }
        void ApplyHomeTheme()
        {
            ThemePalette palette = ThemeManager.Palette;
            heading.ForeColor = palette.TextPrimary;
            subtitle.ForeColor = palette.TextSecondary;
            recentTitle.ForeColor = palette.TextPrimary;
            _homeLatestResultLabel.ForeColor = palette.TextSecondary;
            engineStatus.ForeColor = _homeStatsLabel.ForeColor = palette.TextMuted;
            panel.Invalidate(true);
        }
        panel.Controls.AddRange([heading, subtitle, _captureHeroCard, _ocrHeroCard,
            recentTitle, folderButton, recentCard, engineStatus, _homeStatsLabel]);
        panel.Resize += (_, _) => LayoutHome();
        ConfigService.ConfigChanged += UpdateHomeConfiguration;
        ThemeManager.ThemeChanged += ApplyHomeTheme;
        panel.Disposed += (_, _) =>
        {
            ConfigService.ConfigChanged -= UpdateHomeConfiguration;
            ThemeManager.ThemeChanged -= ApplyHomeTheme;
        };
        UpdateHomeConfiguration();
        ApplyHomeTheme();
        LayoutHome();
        return panel;
    }

    // 页面 2：OCR 工具与记录
    private Panel CreateOcrStudioPage()
    {
        var panel = CreateBasePage();
        int contentWidth = panel.Width - 16;
        var palette = ThemeManager.Palette;

        var titleLabel = new Label
        {
            Text = "文字工作台",
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Regular),
            AutoSize = true,
            Location = new Point(0, 0)
        };
        titleLabel.Paint += (s, e) => titleLabel.ForeColor = ThemeManager.Palette.TextPrimary;

        _ocrTextBox = new ModernTextEditor
        {
            Location = new Point(0, 76),
            Size = new Size(contentWidth, panel.Height - 124),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Microsoft YaHei UI", 10f),
            PlaceholderText = "此处显示最近一次 OCR 识别文本，可在此粘贴或整理..."
        };

        var copyBtn = new ModernButton
        {
            Text = "复制文本",
            IsPrimary = true,
            CornerRadius = 20,
            Size = new Size(100, 36),
            Location = new Point(0, panel.Height - 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        copyBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_ocrTextBox.Text))
            {
                CaptureService.TryCopyTextToClipboard(_ocrTextBox.Text);
                MessageBox.Show("已复制识别文本到剪贴板", "ZSnaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var aiCleanBtn = new ModernButton
        {
            Text = "AI 整理",
            Icon = LucideIcon.Sparkles,
            IsPrimary = false,
            CornerRadius = 20,
            Size = new Size(100, 36),
            Location = new Point(108, panel.Height - 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        aiCleanBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_ocrTextBox.Text)) return;
            aiCleanBtn.Enabled = false;
            aiCleanBtn.Text = "整理中...";
            try
            {
                var result = await OcrService.PolishTextAsync(_ocrTextBox.Text, "请智能优化这段文字的排版与分段，保持内容完整，独立标题、选项和菜单严格单独成行。");
                _ocrTextBox.Text = result.Text;
            }
            catch (Exception ex)
            {
                MessageBox.Show("AI 整理失败：" + ex.Message, "ZSnaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                aiCleanBtn.Enabled = true;
                aiCleanBtn.Text = "AI 整理";
            }
        };

        var cleanBtn = new ModernButton
        {
            Text = "本地分段",
            IsPrimary = false,
            CornerRadius = 20,
            Size = new Size(100, 36),
            Location = new Point(216, panel.Height - 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        cleanBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_ocrTextBox.Text))
            {
                _ocrTextBox.Text = LocalTextSegmenter.SmartSegment(_ocrTextBox.Text);
            }
        };

        var clearBtn = new ModernButton
        {
            Text = "清空",
            IsPrimary = false,
            CornerRadius = 20,
            Size = new Size(75, 36),
            Location = new Point(324, panel.Height - 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        clearBtn.Click += (_, _) => _ocrTextBox.Clear();

        var subtitleLabel = new Label
        {
            Text = "查看识别结果，整理并复制所需文字",
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = ThemeManager.Palette.TextMuted,
            AutoSize = true, Location = new Point(1, 44)
        };
        subtitleLabel.Paint += (_, _) => subtitleLabel.ForeColor = ThemeManager.Palette.TextMuted;
        panel.Controls.Add(subtitleLabel);
        panel.Controls.Add(titleLabel);
        panel.Controls.Add(_ocrTextBox);
        panel.Controls.Add(copyBtn);
        panel.Controls.Add(aiCleanBtn);
        panel.Controls.Add(cleanBtn);
        panel.Controls.Add(clearBtn);

        return panel;
    }

    public void UpdateLatestOcrText(string text)
    {
        if (_ocrTextBox != null && !IsDisposed)
        {
            _ocrTextBox.Text = text;
        }
    }

    public void UpdateHomeOverview(int captureCount, int ocrCount, string? savedFilePath, bool wasOcr)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateHomeOverview(captureCount, ocrCount, savedFilePath, wasOcr));
            return;
        }

        _homeCaptureCount = captureCount;
        _homeOcrCount = ocrCount;

        if (_homeStatsLabel != null && !_homeStatsLabel.IsDisposed)
        {
            _homeStatsLabel.Text = $"本次运行  ·  截图 {captureCount}  ·  识别 {ocrCount}";
        }

        if (!string.IsNullOrWhiteSpace(savedFilePath))
        {
            AddRecentCapture(savedFilePath);
            _homeLatestFilePath = savedFilePath;
            if (_homeLatestResultLabel != null && !_homeLatestResultLabel.IsDisposed)
            {
                _homeLatestResultLabel.Text = savedFilePath;
                _homeLatestResultLabel.ForeColor = ThemeManager.Palette.TextPrimary;
            }
            if (_homeOpenResultButton != null && !_homeOpenResultButton.IsDisposed)
            {
                _homeOpenResultButton.Visible = true;
            }
            if (_recentThumbnails.Count > 0 && _homeLatestResultLabel is not null) _homeLatestResultLabel.Visible = false;
            return;
        }

        _homeLatestFilePath = null;
        if (_homeLatestResultLabel != null && !_homeLatestResultLabel.IsDisposed)
        {
            _homeLatestResultLabel.Text = wasOcr
                ? "文字已发送到 OCR 工作台并复制到剪贴板"
                : "截图已复制到剪贴板";
            _homeLatestResultLabel.ForeColor = ThemeManager.Palette.TextMuted;
        }
        if (_homeOpenResultButton != null && !_homeOpenResultButton.IsDisposed)
        {
            _homeOpenResultButton.Visible = false;
        }
        LayoutRecentCaptures();
    }

    // 页面 3：快捷键配置
    private Panel CreateHotkeysPage()
    {
        Panel panel = CreateBasePage();
        int contentWidth = panel.Width - 16;
        Label titleLabel = new()
        {
            Text = "快捷键",
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Regular),
            AutoSize = true,
            Location = Point.Empty
        };
        Label descriptionLabel = new()
        {
            Text = "为每个常用动作设置全局快捷键；点击按键框后直接录制，Esc 取消",
            Font = new Font("Microsoft YaHei UI", 9f),
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(1, 44),
            Size = new Size(contentWidth - 98, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Label resultCountLabel = new()
        {
            Text = $"{HotkeyCommandCatalog.Definitions.Count} 个命令",
            Font = new Font("Microsoft YaHei UI", 8.1f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(contentWidth - 86, 44),
            Size = new Size(86, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        HotkeySearchBox searchBox = new()
        {
            Location = new Point(0, 76),
            Size = new Size(contentWidth, 38),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Label feedbackLabel = new()
        {
            Text = "新增动作默认未绑定，按需设置即可；右键快捷键框可清除",
            Font = new Font("Microsoft YaHei UI", 8.1f),
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(1, 124),
            Size = new Size(contentWidth, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        ModernButton confirmBindingButton = new()
        {
            Text = "仍然绑定",
            Icon = LucideIcon.Check,
            IsPrimary = true,
            Font = new Font("Microsoft YaHei UI", 8.1f),
            Size = new Size(104, 28),
            Location = new Point(contentWidth - 104, 118),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Visible = false,
            AccessibleRole = AccessibleRole.PushButton,
            AccessibleName = "仍然绑定这个快捷键",
            AccessibleDescription = "ZSnaper 将优先接收这个按键"
        };
        ModernScrollPanel scrollPanel = new()
        {
            Location = new Point(0, 152),
            Size = new Size(panel.Width, panel.Height - 152),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        int cardWidth = Math.Max(GraphicsHelper.Dip(panel, 280), scrollPanel.Content.ClientSize.Width - GraphicsHelper.Dip(panel, 4));
        bool feedbackIsError = false;
        bool feedbackIsWarning = false;
        HotkeyCommand? pendingCommand = null;
        HotkeyGesture pendingGesture = default;
        HotkeyRecorder? pendingRecorder = null;
        Action? pendingAccepted = null;
        Action applyFilter = null!;
        var entries = new List<(HotkeyCommandDefinition Definition, ModernCard Card, Label Name, Label Description, HotkeyRecorder Recorder)>();

        void ShowFeedback(HotkeyChangeResult result)
        {
            feedbackLabel.Text = result.Message;
            feedbackIsWarning = !result.Success && result.CanForce;
            feedbackIsError = !result.Success && result.Message != "已取消修改" && !feedbackIsWarning;
            feedbackLabel.ForeColor = feedbackIsError
                ? Color.FromArgb(239, 68, 68)
                : feedbackIsWarning ? Color.FromArgb(245, 158, 11) : ThemeManager.Palette.TextMuted;
        }

        void RefreshPendingBinding()
        {
            bool visible = pendingCommand is not null;
            confirmBindingButton.Visible = visible;
            feedbackLabel.Width = Math.Max(1, contentWidth - (visible ? 116 : 0));
            confirmBindingButton.Invalidate();
        }

        void ClearPendingBinding()
        {
            pendingCommand = null;
            pendingRecorder = null;
            pendingAccepted = null;
            RefreshPendingBinding();
        }

        void OfferDirectBinding(HotkeyCommand command, HotkeyGesture gesture, HotkeyRecorder recorder, Action accepted)
        {
            pendingCommand = command;
            pendingGesture = gesture;
            pendingRecorder = recorder;
            pendingAccepted = accepted;
            RefreshPendingBinding();
        }

        confirmBindingButton.Click += (_, _) =>
        {
            if (pendingCommand is not HotkeyCommand command || pendingRecorder is null) return;
            HotkeyChangeResult result = RequestHotkeyChange?.Invoke(command, pendingGesture, HotkeyBindingMode.Intercept)
                ?? new HotkeyChangeResult(false, "快捷键服务尚未就绪");
            if (result.Success)
            {
                pendingRecorder.Gesture = pendingGesture;
                pendingAccepted?.Invoke();
                result = new HotkeyChangeResult(true, $"已绑定 {pendingGesture.DisplayText}");
                ClearPendingBinding();
                applyFilter();
            }
            ShowFeedback(result);
        };

        foreach (HotkeyCommandDefinition definition in HotkeyCommandCatalog.Definitions)
        {
            string configured = HotkeyCommandCatalog.GetConfigText(ConfigService.Current, definition.Command);
            bool forceBinding = HotkeyCommandCatalog.GetForceBinding(ConfigService.Current, definition.Command);
            bool hasGesture = HotkeyGesture.TryParse(configured, out HotkeyGesture gesture, forceBinding);
            (ModernCard card, Label name, Label desc, HotkeyRecorder recorder) = CreateCommandCard(
                definition,
                gesture,
                hasGesture,
                forceBinding);
            recorder.Feedback += ShowFeedback;
            entries.Add((definition, card, name, desc, recorder));
            scrollPanel.Content.Controls.Add(card);
        }

        Label fixedTitle = new()
        {
            Text = "固定操作",
            Font = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Bold),
            AutoSize = true
        };
        ModernCard fixedCard = new()
        {
            CornerRadius = 9,
            Size = new Size(cardWidth, 60),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Label escapeKey = new()
        {
            Text = "Esc", Font = new Font("Segoe UI", 8.6f, FontStyle.Bold), AutoSize = true, Location = new Point(14, 10)
        };
        Label escapeDescription = new()
        {
            Text = "取消截图选区 / 取消快捷键录制", Font = new Font("Microsoft YaHei UI", 8.2f), AutoSize = true, Location = new Point(74, 10)
        };
        Label trayKey = new()
        {
            Text = "托盘图标", Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold), AutoSize = true, Location = new Point(14, 34)
        };
        Label trayDescription = new()
        {
            Text = "单击或双击快速触发（可在偏好设置中自定义）", Font = new Font("Microsoft YaHei UI", 8.2f), AutoSize = true, Location = new Point(94, 34)
        };
        fixedCard.Controls.Add(escapeKey);
        fixedCard.Controls.Add(escapeDescription);
        fixedCard.Controls.Add(trayKey);
        fixedCard.Controls.Add(trayDescription);

        Label emptyLabel = new()
        {
            Text = "没有找到匹配的快捷键",
            Font = new Font("Microsoft YaHei UI", 9f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(cardWidth, 72),
            Cursor = Cursors.Hand,
            Visible = false
        };
        emptyLabel.Click += (_, _) => searchBox.Clear();
        scrollPanel.Content.Controls.Add(fixedTitle);
        scrollPanel.Content.Controls.Add(fixedCard);
        scrollPanel.Content.Controls.Add(emptyLabel);

        applyFilter = () =>
        {
            string query = searchBox.Text;
            int top = 0;
            int matchCount = 0;
            int currentWidth = Math.Max(GraphicsHelper.Dip(panel, 280), scrollPanel.Content.ClientSize.Width - GraphicsHelper.Dip(panel, 4));
            foreach (var entry in entries)
            {
                string shortcut = entry.Recorder.HasGesture
                    ? $"{entry.Recorder.Gesture.DisplayText} {entry.Recorder.Gesture.ConfigText}"
                    : "未设置 未绑定 unassigned none";
                bool visible = HotkeyCommandCatalog.Matches(entry.Definition, query, shortcut);
                entry.Card.Visible = visible;
                if (!visible) continue;
                entry.Card.Location = new Point(0, top);
                entry.Card.Width = currentWidth;
                top += entry.Card.Height + GraphicsHelper.Dip(panel, 10);
                matchCount++;
            }

            bool fixedVisible = string.IsNullOrWhiteSpace(query) ||
                "固定操作 Esc 取消当前选区 取消快捷键录制 托盘图标 单击 双击 打开工作台".Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
            fixedTitle.Visible = fixedVisible;
            fixedCard.Visible = fixedVisible;
            if (fixedVisible)
            {
                fixedTitle.Location = new Point(0, top + GraphicsHelper.Dip(panel, 4));
                fixedCard.Location = new Point(0, top + GraphicsHelper.Dip(panel, 30));
                fixedCard.Width = currentWidth;
                top = fixedCard.Bottom + GraphicsHelper.Dip(panel, 2);
            }

            emptyLabel.Visible = matchCount == 0 && !fixedVisible;
            if (emptyLabel.Visible)
            {
                emptyLabel.Text = $"没有找到与 \"{query.Trim()}\" 匹配的快捷键\n点击此处或按 Esc 清空搜索";
                emptyLabel.Location = new Point(0, 18);
                emptyLabel.Width = currentWidth;
                top = emptyLabel.Bottom;
            }
            resultCountLabel.Text = string.IsNullOrWhiteSpace(query) ? $"{entries.Count} 个命令" : $"找到 {matchCount} 个";
            scrollPanel.ContentHeight = top + GraphicsHelper.Dip(panel, 14);
        };
        searchBox.TextChanged += (_, _) => applyFilter();
        scrollPanel.Content.Resize += (_, _) =>
        {
            int w = Math.Max(GraphicsHelper.Dip(panel, 280), scrollPanel.Content.ClientSize.Width - GraphicsHelper.Dip(panel, 4));
            foreach (var entry in entries)
            {
                entry.Card.Width = w;
            }
            fixedCard.Width = w;
            emptyLabel.Width = w;
        };
        applyFilter();

        Action applyHotkeyTheme = () =>
        {
            ThemePalette palette = ThemeManager.Palette;
            titleLabel.ForeColor = palette.TextPrimary;
            descriptionLabel.ForeColor = palette.TextMuted;
            resultCountLabel.ForeColor = palette.TextMuted;
            feedbackLabel.ForeColor = feedbackIsError
                ? Color.FromArgb(239, 68, 68)
                : feedbackIsWarning ? Color.FromArgb(245, 158, 11) : palette.TextMuted;
            foreach (var entry in entries)
            {
                entry.Name.ForeColor = palette.TextPrimary;
                entry.Description.ForeColor = palette.TextMuted;
                entry.Card.Invalidate();
                entry.Recorder.Invalidate();
            }
            fixedTitle.ForeColor = palette.TextSecondary;
            escapeKey.ForeColor = palette.AccentColor;
            trayKey.ForeColor = palette.AccentColor;
            escapeDescription.ForeColor = palette.TextMuted;
            trayDescription.ForeColor = palette.TextMuted;
            emptyLabel.ForeColor = palette.TextMuted;
            fixedCard.Invalidate();
        };
        applyHotkeyTheme();
        ThemeManager.ThemeChanged += applyHotkeyTheme;
        panel.Disposed += (_, _) => ThemeManager.ThemeChanged -= applyHotkeyTheme;

        panel.Controls.Add(titleLabel);
        panel.Controls.Add(descriptionLabel);
        panel.Controls.Add(resultCountLabel);
        panel.Controls.Add(searchBox);
        panel.Controls.Add(feedbackLabel);
        panel.Controls.Add(confirmBindingButton);
        panel.Controls.Add(scrollPanel);
        return panel;

        (ModernCard Card, Label Name, Label Description, HotkeyRecorder Recorder) CreateCommandCard(
            HotkeyCommandDefinition definition,
            HotkeyGesture gesture,
            bool hasGesture,
            bool forceBinding)
        {
            ModernCard card = new()
            {
                CornerRadius = 10,
                Size = new Size(cardWidth, 68),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Label nameLabel = new()
            {
                Text = definition.Name,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, 13)
            };
            Label descLabel = new()
            {
                Text = definition.Description,
                Font = new Font("Microsoft YaHei UI", 8f),
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(14, 38),
                Size = new Size(Math.Max(1, cardWidth - 184), 19),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            HotkeyRecorder recorder = new()
            {
                AccessibleName = definition.Name,
                Gesture = gesture,
                HasGesture = hasGesture,
                Location = new Point(cardWidth - 156, 17),
                Size = new Size(142, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            recorder.BeginRecordingRequest = () =>
            {
                HotkeyChangeResult result = RequestHotkeyRecordingStart?.Invoke(definition.Command)
                    ?? new HotkeyChangeResult(false, "快捷键服务尚未就绪");
                if (result.Success) ClearPendingBinding();
                return result;
            };
            recorder.EndRecordingRequest = () => RequestHotkeyRecordingStop?.Invoke(definition.Command)
                ?? new HotkeyChangeResult(true, string.Empty);
            recorder.TryCommit = proposed =>
            {
                if (forceBinding && recorder.HasGesture && proposed == recorder.Gesture)
                {
                    ClearPendingBinding();
                    return new HotkeyChangeResult(true, $"当前快捷键已是 {proposed.DisplayText}");
                }

                HotkeyChangeResult result = RequestHotkeyChange?.Invoke(definition.Command, proposed, HotkeyBindingMode.Standard)
                    ?? new HotkeyChangeResult(false, "快捷键服务尚未就绪");
                if (result.Success)
                {
                    forceBinding = false;
                    ClearPendingBinding();
                    BeginInvoke(applyFilter);
                }
                else if (result.CanForce)
                {
                    OfferDirectBinding(definition.Command, proposed, recorder, () => forceBinding = true);
                    string message = result.Failure == HotkeyChangeFailure.Occupied
                        ? $"{proposed.DisplayText} 正被其他程序使用；仍然绑定后，ZSnaper 会优先接收它"
                        : $"{proposed.DisplayText} 是单独按键；仍然绑定后，按下它会直接触发 ZSnaper";
                    result = new HotkeyChangeResult(false, message, result.Failure);
                }
                return result;
            };

            void ClearHotkey()
            {
                HotkeyChangeResult result = RequestHotkeyClear?.Invoke(definition.Command)
                    ?? new HotkeyChangeResult(false, "快捷键服务尚未就绪");
                if (result.Success)
                {
                    recorder.HasGesture = false;
                    forceBinding = false;
                    ClearPendingBinding();
                    ShowFeedback(new HotkeyChangeResult(true, $"已清除【{definition.Name}】快捷键"));
                    BeginInvoke(applyFilter);
                }
                else
                {
                    ShowFeedback(result);
                }
            }

            ModernTrayMenu recorderMenu = new();
            recorderMenu.AddAction("修改快捷键", LucideIcon.Keyboard, (_, _) => recorder.StartRecording());
            var clearItem = recorderMenu.AddAction("清除快捷键", LucideIcon.X, (_, _) => ClearHotkey(), kind: TrayMenuItemKind.Destructive);
            recorderMenu.Opening += (_, _) =>
            {
                clearItem.Visible = recorder.HasGesture;
                recorderMenu.ApplyTheme();
            };
            recorder.ContextMenuStrip = recorderMenu;

            ToolTip toolTip = new();
            toolTip.SetToolTip(recorder, "点击修改快捷键；右键可清除");
            toolTip.SetToolTip(descLabel, definition.Description);
            descLabel.TextChanged += (_, _) => toolTip.SetToolTip(descLabel, descLabel.Text);
            _hotkeyStatusLabels[definition.Command] = descLabel;
            card.Disposed += (_, _) => { toolTip.Dispose(); recorderMenu.Dispose(); };

            card.Controls.Add(nameLabel);
            card.Controls.Add(descLabel);
            card.Controls.Add(recorder);
            return (card, nameLabel, descLabel, recorder);
        }
    }

    // 页面 4：偏好设置
    private Panel CreateSettingsPage()
    {
        var panel = CreateBasePage();

        static void BindThemeColor(Label label, Func<ThemePalette, Color> resolve)
        {
            void Apply()
            {
                if (!label.IsDisposed) label.ForeColor = resolve(ThemeManager.Palette);
            }

            Apply();
            ThemeManager.ThemeChanged += Apply;
            label.Disposed += (_, _) => ThemeManager.ThemeChanged -= Apply;
        }

        var titleLabel = new Label
        {
            Text = "偏好设置",
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Regular),
            ForeColor = ThemeManager.Palette.TextPrimary,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        BindThemeColor(titleLabel, palette => palette.TextPrimary);

        var subtitleLabel = new Label
        {
            Text = "按类别管理外观、截图、OCR、工具栏与系统",
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular),
            ForeColor = ThemeManager.Palette.TextMuted,
            AutoSize = true,
            Location = new Point(1, 44)
        };
        BindThemeColor(subtitleLabel, palette => palette.TextMuted);

        var tabBar = new SettingsTabBar
        {
            Location = new Point(0, 70),
            Size = new Size(panel.Width, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _settingsTabBar = tabBar;

        var scrollPanel = new ModernScrollPanel
        {
            Location = new Point(0, 126),
            Size = new Size(panel.Width, panel.Height - 126),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        var settingsTransitionOverlay = new PageTransitionOverlay();
        _settingsTransitionOverlay = settingsTransitionOverlay;
        scrollPanel.Controls.Add(settingsTransitionOverlay);
        int contentWidth = Math.Max(320, scrollPanel.Content.ClientSize.Width - 4);

        ModernCard CreateSettingsCard(int height) => new()
        {
            CornerRadius = 12,
            Size = new Size(contentWidth, height),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        // 外观：只放影响窗口视觉的选项。
        var appearanceCard = CreateSettingsCard(572);
        var rowAnim = new SettingItemRow
        {
            Title = "交互动效",
            Description = "三档流畅度设置",
            ShowDivider = true,
            ActionControl = new AnimationSegmentedControl()
        };

        bool syncingSidebarToggle = false;
        var toggleSidebarLabels = new ModernToggleSwitch
        {
            Checked = ConfigService.Current.ShowSidebarLabels,
            AccessibleName = "显示侧栏图标文字"
        };
        toggleSidebarLabels.CheckedChanged += (_, _) =>
        {
            if (syncingSidebarToggle) return;
            if (ConfigService.Current.ShowSidebarLabels == toggleSidebarLabels.Checked) return;
            ConfigService.Current.ShowSidebarLabels = toggleSidebarLabels.Checked;
            ConfigService.Save();
            ApplySidebarLayout(snapPill: false);
        };
        var rowSidebarLabels = new SettingItemRow
        {
            Title = "显示侧栏图标文字",
            Description = "关闭后侧栏以紧凑纯图标模式显示",
            ShowDivider = true,
            ActionControl = toggleSidebarLabels
        };

        var toggleGlow = new ModernToggleSwitch { Checked = ConfigService.Current.EnableBackgroundGlow };
        toggleGlow.CheckedChanged += (_, _) => ThemeManager.EnableGlow = toggleGlow.Checked;
        var rowGlow = new SettingItemRow
        {
            Title = "背景氛围漫反射",
            Description = "微光跟随强调色",
            ShowDivider = true,
            ActionControl = toggleGlow
        };

        var rowAccent = new SettingItemRow
        {
            Title = "强调色",
            Description = "主题与交互控件的强调色",
            ShowDivider = true,
            ActionControl = new ModernAccentColorPicker()
        };

        var rowTheme = new SettingItemRow
        {
            Title = "主题外观模式",
            Description = "浅色与深色外观",
            ShowDivider = true,
            ActionControl = new ThemeSegmentedControl()
        };

        var trayStyleOptions = new[]
        {
            (Style: TrayIconStyle.FollowTheme, Label: "跟随主题"),
            (Style: TrayIconStyle.Light, Label: "浅色图标"),
            (Style: TrayIconStyle.Dark, Label: "深色图标"),
            (Style: TrayIconStyle.CustomSvg, Label: "自定义 SVG"),
            (Style: TrayIconStyle.LegacyBlack, Label: "黑底图标")
        };
        var trayStyleDropdown = new ModernDropdown
        {
            Font = new Font("Microsoft YaHei UI", 8f),
            Size = new Size(150, 28),
            AccessibleName = "托盘图标样式",
            AccessibleDescription = "选择托盘图标的主题和 SVG 来源"
        };
        trayStyleDropdown.SetItems(trayStyleOptions.Select(option => option.Label));
        int trayStyleIndex = 0;
        for (int index = 0; index < trayStyleOptions.Length; index++)
        {
            if (trayStyleOptions[index].Style != ConfigService.Current.TrayIconStyle) continue;
            trayStyleIndex = index;
            break;
        }
        trayStyleDropdown.SelectedIndex = trayStyleIndex;

        static Color ReadTrayIconColor(string value, Color fallback)
        {
            try
            {
                return Color.FromArgb(255, ColorTranslator.FromHtml(value));
            }
            catch
            {
                return fallback;
            }
        }

        static string WriteTrayIconColor(Color color) =>
            $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        Color[] configuredTrayIconColors = ConfigService.Current.TrayIconCustomPalette
            .Select(value => ReadTrayIconColor(value, Color.White))
            .ToArray();
        var lightTrayIconColor = new TrayIconColorButton
        {
            Color = ReadTrayIconColor(ConfigService.Current.TrayIconLightColorHex, Color.FromArgb(56, 60, 64)),
            CustomColors = configuredTrayIconColors
        };
        var darkTrayIconColor = new TrayIconColorButton
        {
            Color = ReadTrayIconColor(ConfigService.Current.TrayIconDarkColorHex, Color.White),
            CustomColors = configuredTrayIconColors
        };
        bool paletteTargetsDark = false;
        var trayIconPalette = new TrayIconPaletteControl();
        trayIconPalette.SetColors(configuredTrayIconColors);
        var trayIconScale = new TrayIconScaleControl
        {
            Value = ConfigService.Current.TrayIconScalePercent,
            Enabled = ConfigService.Current.TrayIconStyle != TrayIconStyle.LegacyBlack
        };

        void RefreshTrayIconCustomColors()
        {
            Color[] colors = trayIconPalette.Colors.ToArray();
            lightTrayIconColor.CustomColors = colors;
            darkTrayIconColor.CustomColors = colors;
        }

        void SaveTrayIconColor(bool dark, Color color)
        {
            if (dark)
            {
                ConfigService.Current.TrayIconDarkColorHex = WriteTrayIconColor(color);
            }
            else
            {
                ConfigService.Current.TrayIconLightColorHex = WriteTrayIconColor(color);
            }
            ConfigService.Save();
        }

        lightTrayIconColor.MouseDown += (_, _) => paletteTargetsDark = false;
        darkTrayIconColor.MouseDown += (_, _) => paletteTargetsDark = true;
        lightTrayIconColor.ColorChanged += (_, _) =>
            SaveTrayIconColor(false, lightTrayIconColor.Color);
        darkTrayIconColor.ColorChanged += (_, _) =>
            SaveTrayIconColor(true, darkTrayIconColor.Color);
        trayIconPalette.ColorSelected += color =>
        {
            if (paletteTargetsDark)
            {
                darkTrayIconColor.Color = color;
                SaveTrayIconColor(true, color);
            }
            else
            {
                lightTrayIconColor.Color = color;
                SaveTrayIconColor(false, color);
            }
        };
        trayIconPalette.PaletteChanged += colors =>
        {
            ConfigService.Current.TrayIconCustomPalette = colors
                .Select(WriteTrayIconColor)
                .ToList();
            RefreshTrayIconCustomColors();
            ConfigService.Save();
        };

        trayIconScale.ValueCommitted += (_, _) =>
        {
            ConfigService.Current.TrayIconScalePercent = trayIconScale.Value;
            ConfigService.Save();
        };

        trayStyleDropdown.SelectedIndexChanged += (_, _) =>
        {
            int selected = trayStyleDropdown.SelectedIndex;
            if (selected < 0 || selected >= trayStyleOptions.Length) return;
            ConfigService.Current.TrayIconStyle = trayStyleOptions[selected].Style;
            trayIconScale.Enabled = ConfigService.Current.TrayIconStyle != TrayIconStyle.LegacyBlack;
            ConfigService.Save();
        };

        var btnChooseTrayIconSvg = new ModernButton
        {
            Text = "选择 SVG",
            IsPrimary = false,
            CornerRadius = 20,
            Icon = LucideIcon.Folder,
            IconSize = 14,
            IconGap = 5,
            Size = new Size(104, 28)
        };
        var rowTrayIconSvg = new SettingItemRow
        {
            Title = "自定义 SVG 文件",
            Description = string.IsNullOrWhiteSpace(ConfigService.Current.TrayIconSvgPath)
                ? "未选择时使用内置 Logo SVG"
                : Path.GetFileName(ConfigService.Current.TrayIconSvgPath),
            ShowDivider = true,
            ActionControl = btnChooseTrayIconSvg
        };
        btnChooseTrayIconSvg.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择托盘图标 SVG",
                Filter = "SVG 文件 (*.svg)|*.svg|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(panel.FindForm()) != DialogResult.OK) return;

            ConfigService.Current.TrayIconSvgPath = dialog.FileName;
            rowTrayIconSvg.Description = Path.GetFileName(dialog.FileName);
            ConfigService.Current.TrayIconStyle = TrayIconStyle.CustomSvg;
            trayStyleDropdown.SelectedIndex = 3;
            ConfigService.Save();
        };

        var rowTrayIconStyle = new SettingItemRow
        {
            Title = "托盘图标样式",
            Description = "自动切换 Light / Dark SVG，也可固定或使用自定义文件",
            ShowDivider = true,
            ActionControl = trayStyleDropdown
        };
        var rowTrayIconLightColor = new SettingItemRow
        {
            Title = "浅色模式图标颜色",
            Description = "内置和自定义 SVG 的填充/描边颜色",
            ShowDivider = true,
            ActionControl = lightTrayIconColor
        };
        var rowTrayIconDarkColor = new SettingItemRow
        {
            Title = "深色模式图标颜色",
            Description = "内置和自定义 SVG 的填充/描边颜色",
            ShowDivider = true,
            ActionControl = darkTrayIconColor
        };
        var rowTrayIconPalette = new SettingItemRow
        {
            Title = "SVG 调色板",
            Description = "左键应用到最近选择的颜色，右键编辑色块",
            ShowDivider = true,
            ActionControl = trayIconPalette
        };
        var rowTrayIconScale = new SettingItemRow
        {
            Title = "SVG 托盘图标大小",
            Description = "可调 80% - 160%；黑底图标不支持缩放",
            ShowDivider = false,
            ActionControl = trayIconScale
        };

        rowTheme.SetBounds(0, 0, appearanceCard.Width, 52);
        rowAccent.SetBounds(0, 52, appearanceCard.Width, 52);
        rowGlow.SetBounds(0, 104, appearanceCard.Width, 52);
        rowAnim.SetBounds(0, 156, appearanceCard.Width, 52);
        rowSidebarLabels.SetBounds(0, 208, appearanceCard.Width, 52);
        rowTrayIconStyle.SetBounds(0, 260, appearanceCard.Width, 52);
        rowTrayIconSvg.SetBounds(0, 312, appearanceCard.Width, 52);
        rowTrayIconLightColor.SetBounds(0, 364, appearanceCard.Width, 52);
        rowTrayIconDarkColor.SetBounds(0, 416, appearanceCard.Width, 52);
        rowTrayIconPalette.SetBounds(0, 468, appearanceCard.Width, 52);
        rowTrayIconScale.SetBounds(0, 520, appearanceCard.Width, 52);
        rowTheme.Anchor = rowAccent.Anchor = rowGlow.Anchor = rowAnim.Anchor = rowSidebarLabels.Anchor =
            rowTrayIconStyle.Anchor = rowTrayIconSvg.Anchor = rowTrayIconLightColor.Anchor =
            rowTrayIconDarkColor.Anchor = rowTrayIconPalette.Anchor = rowTrayIconScale.Anchor =
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        appearanceCard.Controls.Add(rowTheme);
        appearanceCard.Controls.Add(rowAccent);
        appearanceCard.Controls.Add(rowGlow);
        appearanceCard.Controls.Add(rowAnim);
        appearanceCard.Controls.Add(rowSidebarLabels);
        appearanceCard.Controls.Add(rowTrayIconStyle);
        appearanceCard.Controls.Add(rowTrayIconSvg);
        appearanceCard.Controls.Add(rowTrayIconLightColor);
        appearanceCard.Controls.Add(rowTrayIconDarkColor);
        appearanceCard.Controls.Add(rowTrayIconPalette);
        appearanceCard.Controls.Add(rowTrayIconScale);

        // 工具栏与批注：编辑器单独成组，展开时不会把其他设置挤在同一张卡片里。
        var toolbarCard = CreateSettingsCard(104);
        var rowToolbarPlacement = new SettingItemRow
        {
            Title = "截图工具栏位置",
            Description = "左侧 / 居中 / 右侧 / Auto 习惯自适应",
            ShowDivider = true,
            ActionControl = new ToolbarPlacementSegmentedControl()
        };
        var btnCustomizeToolbar = new ModernButton
        {
            Text = "展开",
            IsPrimary = false,
            CornerRadius = 20,
            Icon = LucideIcon.Sliders,
            IconSize = 14,
            IconGap = 5,
            Size = new Size(82, 28)
        };
        var rowToolbarItems = new SettingItemRow
        {
            Title = "工具栏与批注样式",
            Description = "工具顺序、完成行为、颜色、字体与笔刷",
            ShowDivider = false,
            ActionControl = btnCustomizeToolbar
        };

        var toolbarEditor = new ToolbarCustomizationPanel
        {
            Visible = false,
            Location = new Point(0, 104),
            Size = new Size(toolbarCard.Width, 398),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        rowToolbarPlacement.SetBounds(0, 0, toolbarCard.Width, 52);
        rowToolbarItems.SetBounds(0, 52, toolbarCard.Width, 52);
        rowToolbarPlacement.Anchor = rowToolbarItems.Anchor =
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        toolbarCard.Controls.Add(rowToolbarPlacement);
        toolbarCard.Controls.Add(rowToolbarItems);
        toolbarCard.Controls.Add(toolbarEditor);

        // 截图行为：完成后的默认去向和保存位置。
        var workflowCard = CreateSettingsCard(156);
        var toggleCopy = new ModernToggleSwitch { Checked = ConfigService.Current.AutoCopyClipboard };
        toggleCopy.CheckedChanged += (_, _) => { ConfigService.Current.AutoCopyClipboard = toggleCopy.Checked; ConfigService.Save(); };
        var rowCopy = new SettingItemRow
        {
            Title = "自动写入剪贴板",
            Description = "截图或 OCR 识别完成后写入剪贴板",
            ShowDivider = true,
            ActionControl = toggleCopy
        };

        var toggleSave = new ModernToggleSwitch { Checked = ConfigService.Current.AutoSavePictures };
        toggleSave.CheckedChanged += (_, _) => { ConfigService.Current.AutoSavePictures = toggleSave.Checked; ConfigService.Save(); };
        var rowSave = new SettingItemRow
        {
            Title = "自动保存原图到本地",
            Description = "截图完成后自动归档为 PNG",
            ShowDivider = true,
            ActionControl = toggleSave
        };

        var btnOpenDir = new ModernButton
        {
            Text = "打开目录",
            IsPrimary = false,
            CornerRadius = 20,
            Size = new Size(76, 26)
        };
        btnOpenDir.Click += (_, _) =>
        {
            var dir = ConfigService.GetEffectiveSavePath();
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        };
        var rowPath = new SettingItemRow
        {
            Title = "本地保存目录",
            Description = @"系统图片目录 \ ZSnaper",
            ShowDivider = false,
            ActionControl = btnOpenDir
        };

        rowCopy.SetBounds(0, 0, workflowCard.Width, 52);
        rowSave.SetBounds(0, 52, workflowCard.Width, 52);
        rowPath.SetBounds(0, 104, workflowCard.Width, 52);
        rowCopy.Anchor = rowSave.Anchor = rowPath.Anchor =
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        workflowCard.Controls.Add(rowCopy);
        workflowCard.Controls.Add(rowSave);
        workflowCard.Controls.Add(rowPath);

        // OCR：本地 Windows 引擎和 OpenAI 兼容视觉 API 共用同一截图入口。
        var ocrSettingsCard = new OcrSettingsPanel
        {
            Size = new Size(contentWidth, 484),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        // 系统：托盘交互与更新设置集中管理。
        var systemCard = CreateSettingsCard(728);
        var trayActionOptions = new[]
        {
            (Action: TrayClickAction.OpenMainWindow, Label: "打开主界面"),
            (Action: TrayClickAction.Capture, Label: "区域截图"),
            (Action: TrayClickAction.CaptureAndPin, Label: "截图并贴图"),
            (Action: TrayClickAction.CaptureWithOcr, Label: "截图并 OCR"),
            (Action: TrayClickAction.CaptureCurrentScreen, Label: "当前屏幕截图"),
            (Action: TrayClickAction.PinClipboardImage, Label: "贴剪贴板图片"),
            (Action: TrayClickAction.OpenSaveFolder, Label: "打开截图目录"),
            (Action: TrayClickAction.ToggleTheme, Label: "切换深浅主题"),
            (Action: TrayClickAction.None, Label: "无操作")
        };

        ModernDropdown CreateTrayActionDropdown(
            string accessibleName,
            TrayClickAction currentAction,
            Action<TrayClickAction> saveAction)
        {
            var dropdown = new ModernDropdown
            {
                Font = new Font("Microsoft YaHei UI", 8f),
                Size = new Size(132, 28),
                AccessibleName = accessibleName,
                AccessibleDescription = "选择点击托盘图标时执行的操作"
            };
            dropdown.SetItems(trayActionOptions.Select(option => option.Label));
            int selectedIndex = Array.FindIndex(
                trayActionOptions,
                option => option.Action == currentAction);
            dropdown.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            dropdown.SelectedIndexChanged += (_, _) =>
            {
                int selected = dropdown.SelectedIndex;
                if (selected < 0 || selected >= trayActionOptions.Length) return;
                saveAction(trayActionOptions[selected].Action);
                ConfigService.Save();
            };
            return dropdown;
        }

        var leftClickActionDropdown = CreateTrayActionDropdown(
            "托盘左键点击行为",
            ConfigService.Current.TrayLeftClickAction,
            action => ConfigService.Current.TrayLeftClickAction = action);
        var rowTrayLeftClick = new SettingItemRow
        {
            Title = "托盘左键点击",
            Description = "单击或双击只执行一次，避免重复触发截图",
            ShowDivider = true,
            ActionControl = leftClickActionDropdown
        };
        rowTrayLeftClick.SetBounds(0, 0, systemCard.Width, 52);

        var middleClickActionDropdown = CreateTrayActionDropdown(
            "托盘中键点击行为",
            ConfigService.Current.TrayMiddleClickAction,
            action => ConfigService.Current.TrayMiddleClickAction = action);
        var rowTrayMiddleClick = new SettingItemRow
        {
            Title = "托盘中键点击",
            Description = "按下鼠标滚轮时快速执行指定操作",
            ShowDivider = true,
            ActionControl = middleClickActionDropdown
        };
        rowTrayMiddleClick.SetBounds(0, 52, systemCard.Width, 52);

        var rowChannel = new SettingItemRow
        {
            Title = "更新与发布通道",
            Description = "正式版 (稳定推荐) / 测试版 (预览体验)",
            ShowDivider = true,
            ActionControl = new ChannelSegmentedControl()
        };
        rowChannel.SetBounds(0, 104, systemCard.Width, 52);

        _updateButton = new ModernButton
        {
            Text = "检查更新",
            IsPrimary = false,
            CornerRadius = 20,
            Icon = LucideIcon.RotateCcw,
            IconSize = 14,
            IconGap = 5,
            Size = new Size(102, 28)
        };
        _updateButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_latestUpdateUrl))
            {
                RequestOpenUpdate?.Invoke(_latestUpdateUrl);
            }
            else
            {
                RequestUpdateCheck?.Invoke();
            }
        };

        var rowUpdate = new SettingItemRow
        {
            Title = "检查应用更新",
            Description = $"当前版本 {AppVersionInfo.DisplayVersion}",
            ShowDivider = true,
            ActionControl = _updateButton
        };
        rowUpdate.SetBounds(0, 156, systemCard.Width, 52);

        _lastUpdateRow = new SettingItemRow
        {
            Title = "上次检查",
            Description = FormatLastUpdateCheck(),
            ShowDivider = true
        };
        _lastUpdateRow.SetBounds(0, 208, systemCard.Width, 52);

        var toggleAutoUpdate = new ModernToggleSwitch
        {
            Checked = ConfigService.Current.AutoCheckUpdates
        };
        toggleAutoUpdate.CheckedChanged += (_, _) =>
        {
            ConfigService.Current.AutoCheckUpdates = toggleAutoUpdate.Checked;
            ConfigService.Save();
        };
        var rowAutoUpdate = new SettingItemRow
        {
            Title = "自动检查更新",
            Description = "应用启动后按设定频率自动检查",
            ShowDivider = true,
            ActionControl = toggleAutoUpdate
        };
        rowAutoUpdate.SetBounds(0, 260, systemCard.Width, 52);

        var updateIntervalOptions = new[]
        {
            (Hours: 6, Label: "每 6 小时"),
            (Hours: 12, Label: "每 12 小时"),
            (Hours: 24, Label: "每天"),
            (Hours: 168, Label: "每周")
        };
        var updateIntervalDropdown = new ModernDropdown
        {
            Font = new Font("Microsoft YaHei UI", 8f),
            Size = new Size(112, 28),
            AccessibleName = "自动检查更新间隔",
            AccessibleDescription = "设置自动检查更新的时间间隔"
        };
        updateIntervalDropdown.SetItems(updateIntervalOptions.Select(option => option.Label));
        int updateIntervalIndex = Array.FindIndex(
            updateIntervalOptions,
            option => option.Hours == ConfigService.Current.UpdateCheckIntervalHours);
        updateIntervalDropdown.SelectedIndex = updateIntervalIndex >= 0 ? updateIntervalIndex : 2;
        updateIntervalDropdown.SelectedIndexChanged += (_, _) =>
        {
            int selectedIndex = updateIntervalDropdown.SelectedIndex;
            if (selectedIndex < 0) return;
            ConfigService.Current.UpdateCheckIntervalHours = updateIntervalOptions[selectedIndex].Hours;
            ConfigService.Save();
        };

        var rowUpdateInterval = new SettingItemRow
        {
            Title = "检查频率",
            Description = "自动检查更新的时间间隔",
            ShowDivider = true,
            ActionControl = updateIntervalDropdown
        };
        rowUpdateInterval.SetBounds(0, 312, systemCard.Width, 52);

        var toggleAutoStart = new ModernToggleSwitch
        {
            Checked = ConfigService.IsAutoStartEnabled()
        };
        bool restoringAutoStart = false;
        toggleAutoStart.CheckedChanged += (_, _) =>
        {
            if (restoringAutoStart) return;

            bool enabled = toggleAutoStart.Checked;
            if (ConfigService.SetAutoStart(enabled)) return;

            restoringAutoStart = true;
            toggleAutoStart.Checked = ConfigService.IsAutoStartEnabled();
            restoringAutoStart = false;
            MessageBox.Show(
                "无法更新开机自启动设置，请检查当前用户的注册表权限",
                "ZSnaper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        };
        var rowAutoStart = new SettingItemRow
        {
            Title = "开机自启动",
            Description = "登录 Windows 后在后台启动并驻留托盘",
            ShowDivider = true,
            ActionControl = toggleAutoStart
        };
        rowAutoStart.SetBounds(0, 364, systemCard.Width, 52);

        var toggleNotify = new ModernToggleSwitch { Checked = ConfigService.Current.ShowNotification };
        toggleNotify.CheckedChanged += (_, _) => { ConfigService.Current.ShowNotification = toggleNotify.Checked; ConfigService.Save(); };
        var rowNotify = new SettingItemRow
        {
            Title = "操作完成状态气泡",
            Description = "截图和 OCR 完成后显示状态",
            ShowDivider = true,
            ActionControl = toggleNotify
        };
        rowNotify.SetBounds(0, 416, systemCard.Width, 52);

        var toggleWindowsNotifications = new ModernToggleSwitch
        {
            Checked = ConfigService.Current.ShowWindowsNotifications,
            AccessibleName = "Windows 弹窗通知"
        };
        toggleWindowsNotifications.CheckedChanged += (_, _) =>
        {
            ConfigService.Current.ShowWindowsNotifications = toggleWindowsNotifications.Checked;
            ConfigService.Save();
        };
        var rowWindowsNotifications = new SettingItemRow
        {
            Title = "Windows 弹窗通知",
            Description = "控制 ZSnaper 的状态、更新和错误通知",
            ShowDivider = false,
            ActionControl = toggleWindowsNotifications
        };
        rowWindowsNotifications.SetBounds(0, 468, systemCard.Width, 52);
        rowWindowsNotifications.ShowDivider = true;

        var configLocationDropdown = new ModernDropdown
        {
            Font = new Font("Microsoft YaHei UI", 8f),
            Size = new Size(126, 28),
            AccessibleName = "配置存放位置"
        };
        configLocationDropdown.SetItems(["用户数据", "程序目录"]);
        configLocationDropdown.SelectedIndex = ConfigService.StorageLocation == ConfigStorageLocation.ApplicationDirectory ? 1 : 0;
        bool restoringConfigLocation = false;
        configLocationDropdown.SelectedIndexChanged += (_, _) =>
        {
            if (restoringConfigLocation || configLocationDropdown.SelectedIndex < 0) return;
            ConfigStorageLocation selected = configLocationDropdown.SelectedIndex == 1
                ? ConfigStorageLocation.ApplicationDirectory
                : ConfigStorageLocation.UserData;
            if (ConfigService.TrySetStorageLocation(selected, out string error)) return;

            restoringConfigLocation = true;
            configLocationDropdown.SelectedIndex = ConfigService.StorageLocation == ConfigStorageLocation.ApplicationDirectory ? 1 : 0;
            restoringConfigLocation = false;
            MessageBox.Show(error, "ZSnaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        var rowConfigLocation = new SettingItemRow
        {
            Title = "配置存放位置",
            Description = "用户数据或程序安装目录下的 config",
            ActionControl = configLocationDropdown
        };
        rowConfigLocation.SetBounds(0, 520, systemCard.Width, 52);

        void OpenSettingsDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException("MainForm.OpenSettingsDirectory", exception);
                MessageBox.Show($"无法打开目录：{exception.Message}", "ZSnaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        ModernButton CreateOpenDirectoryButton(string directoryName, Func<string> getDirectory)
        {
            var button = new ModernButton
            {
                Text = "打开目录",
                IsPrimary = false,
                CornerRadius = 20,
                Size = new Size(76, 26),
                AccessibleName = directoryName
            };
            button.Click += (_, _) => OpenSettingsDirectory(getDirectory());
            return button;
        }

        var rowConfigDirectory = new SettingItemRow
        {
            Title = "配置目录",
            Description = "打开当前生效的配置文件所在目录",
            ActionControl = CreateOpenDirectoryButton("打开配置目录", () => ConfigService.ActiveConfigDirectory)
        };
        rowConfigDirectory.SetBounds(0, 572, systemCard.Width, 52);

        var logLevelOptions = new[]
        {
            (Level: AppLogLevel.Debug, Label: "调试"),
            (Level: AppLogLevel.Information, Label: "信息"),
            (Level: AppLogLevel.Warning, Label: "警告"),
            (Level: AppLogLevel.Error, Label: "错误")
        };
        var logLevelDropdown = new ModernDropdown
        {
            Font = new Font("Microsoft YaHei UI", 8f),
            Size = new Size(110, 28),
            AccessibleName = "日志级别"
        };
        logLevelDropdown.SetItems(logLevelOptions.Select(option => option.Label));
        logLevelDropdown.SelectedIndex = Array.FindIndex(logLevelOptions,
            option => option.Level == ConfigService.Current.LogLevel);
        bool restoringLogLevel = false;
        logLevelDropdown.SelectedIndexChanged += (_, _) =>
        {
            if (restoringLogLevel || logLevelDropdown.SelectedIndex < 0) return;
            AppLogLevel previous = ConfigService.Current.LogLevel;
            AppLogLevel selected = logLevelOptions[logLevelDropdown.SelectedIndex].Level;
            if (selected == previous) return;
            ConfigService.Current.LogLevel = selected;
            if (ConfigService.Save()) return;

            ConfigService.Current.LogLevel = previous;
            AppDiagnostics.SetMinimumLevel(previous);
            restoringLogLevel = true;
            logLevelDropdown.SelectedIndex = Array.FindIndex(logLevelOptions,
                option => option.Level == previous);
            restoringLogLevel = false;
            MessageBox.Show("日志级别保存失败，已恢复原设置", "ZSnaper",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        var rowLogLevel = new SettingItemRow
        {
            Title = "日志级别",
            Description = "只记录所选级别及更严重的事件",
            ActionControl = logLevelDropdown
        };
        rowLogLevel.SetBounds(0, 624, systemCard.Width, 52);

        var rowLogDirectory = new SettingItemRow
        {
            Title = "运行日志",
            Description = "打开按日期滚动保存的诊断日志",
            ShowDivider = false,
            ActionControl = CreateOpenDirectoryButton("打开日志目录", () => AppDiagnostics.LogDirectory)
        };
        rowLogDirectory.SetBounds(0, 676, systemCard.Width, 52);

        rowTrayLeftClick.Anchor = rowTrayMiddleClick.Anchor = rowChannel.Anchor = rowUpdate.Anchor = _lastUpdateRow.Anchor = rowAutoUpdate.Anchor =
            rowUpdateInterval.Anchor = rowNotify.Anchor = rowWindowsNotifications.Anchor =
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        rowAutoStart.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        rowConfigLocation.Anchor = rowConfigDirectory.Anchor = rowLogLevel.Anchor = rowLogDirectory.Anchor =
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        systemCard.Controls.Add(rowTrayLeftClick);
        systemCard.Controls.Add(rowTrayMiddleClick);
        systemCard.Controls.Add(rowChannel);
        systemCard.Controls.Add(rowUpdate);
        systemCard.Controls.Add(_lastUpdateRow);
        systemCard.Controls.Add(rowAutoUpdate);
        systemCard.Controls.Add(rowUpdateInterval);
        systemCard.Controls.Add(rowAutoStart);
        systemCard.Controls.Add(rowNotify);
        systemCard.Controls.Add(rowWindowsNotifications);
        systemCard.Controls.Add(rowConfigLocation);
        systemCard.Controls.Add(rowConfigDirectory);
        systemCard.Controls.Add(rowLogLevel);
        systemCard.Controls.Add(rowLogDirectory);

        var footerHint = new Label
        {
            Text = "设置会自动保存，并立即应用到当前窗口",
            Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular),
            ForeColor = ThemeManager.Palette.TextMuted,
            AutoSize = true,
            Location = new Point(4, 0)
        };
        BindThemeColor(footerHint, palette => palette.TextMuted);

        void LayoutSettings()
        {
            ModernCard[] sections =
            {
                appearanceCard,
                workflowCard,
                ocrSettingsCard,
                toolbarCard,
                systemCard
            };

            int selectedIndex = tabBar.SelectedIndex;
            for (int index = 0; index < sections.Length; index++)
            {
                bool visible = index == selectedIndex;
                sections[index].Visible = visible;
            }

            ModernCard selectedCard = sections[selectedIndex];
            selectedCard.Top = GraphicsHelper.Dip(panel, 8);

            footerHint.Visible = true;
            footerHint.Top = selectedCard.Bottom + GraphicsHelper.Dip(panel, 16);
            scrollPanel.FitContentHeight(bottomPadding: GraphicsHelper.Dip(panel, 18));
        }

        int previousSettingsCategory = tabBar.SelectedIndex;
        tabBar.SelectedIndexChanged += (_, _) =>
        {
            int nextSettingsCategory = tabBar.SelectedIndex;
            bool animateCategory = _hasShownOnce && Visible && panel.Visible && scrollPanel.IsHandleCreated;
            Bitmap? outgoing = animateCategory ? PageTransitionOverlay.TryCaptureSnapshot(scrollPanel) : null;
            bool usingTransition = animateCategory && outgoing is not null;

            if (usingTransition)
            {
                settingsTransitionOverlay.FreezeOutgoing(outgoing, scrollPanel.ClientRectangle);
            }

            LayoutSettings();
            scrollPanel.ScrollToTop();
            scrollPanel.Content.Invalidate(true);

            if (usingTransition)
            {
                settingsTransitionOverlay.BringToFront();
                scrollPanel.Content.PerformLayout();
                Bitmap? incoming = PageTransitionOverlay.TryCaptureSnapshot(scrollPanel);
                int direction = nextSettingsCategory >= previousSettingsCategory ? 1 : -1;
                settingsTransitionOverlay.BeginReveal(
                    incoming,
                    PageTransitionAxis.Horizontal,
                    direction);
            }
            else
            {
                settingsTransitionOverlay.CompleteImmediately();
                outgoing?.Dispose();
            }

            previousSettingsCategory = nextSettingsCategory;
        };

        btnCustomizeToolbar.Click += (_, _) =>
        {
            bool expanded = !toolbarEditor.Visible;

            toolbarEditor.Visible = expanded;
            btnCustomizeToolbar.Text = expanded ? "收起" : "展开";
            btnCustomizeToolbar.Icon = expanded ? LucideIcon.Minus : LucideIcon.Sliders;

            toolbarCard.Height = GraphicsHelper.Dip(panel, 104) + (expanded ? toolbarEditor.Height : 0);
            LayoutSettings();
            toolbarCard.Invalidate(true);
            scrollPanel.Content.Invalidate(true);
        };

        scrollPanel.Content.Controls.Add(appearanceCard);
        scrollPanel.Content.Controls.Add(toolbarCard);
        scrollPanel.Content.Controls.Add(workflowCard);
        scrollPanel.Content.Controls.Add(ocrSettingsCard);
        scrollPanel.Content.Controls.Add(systemCard);
        scrollPanel.Content.Controls.Add(footerHint);
        void HandleConfigChanged()
        {
            RefreshUpdateCheckInfo();
            if (IsDisposed || toggleSidebarLabels.IsDisposed) return;
            bool desired = ConfigService.Current.ShowSidebarLabels;
            if (toggleSidebarLabels.Checked != desired)
            {
                syncingSidebarToggle = true;
                toggleSidebarLabels.Checked = desired;
                syncingSidebarToggle = false;
                ApplySidebarLayout(snapPill: false);
            }
        }
        ConfigService.ConfigChanged += HandleConfigChanged;
        panel.Disposed += (_, _) => ConfigService.ConfigChanged -= HandleConfigChanged;
        LayoutSettings();

        panel.Controls.Add(titleLabel);
        panel.Controls.Add(subtitleLabel);
        panel.Controls.Add(tabBar);
        panel.Controls.Add(scrollPanel);

        return panel;
    }

    public void SetUpdateStatus(string text, bool isBusy, string? releaseUrl = null)
    {
        if (IsDisposed || _updateButton is null) return;

        if (InvokeRequired)
        {
            BeginInvoke(() => SetUpdateStatus(text, isBusy, releaseUrl));
            return;
        }

        _latestUpdateUrl = releaseUrl;
        _updateButton.Text = text;
        _updateButton.Enabled = !isBusy;
        _updateButton.Icon = !isBusy && !string.IsNullOrWhiteSpace(releaseUrl)
            ? LucideIcon.ArrowUpRight
            : LucideIcon.RotateCcw;
        _updateButton.IsPrimary = !isBusy && !string.IsNullOrWhiteSpace(releaseUrl);
    }

    public void RefreshUpdateCheckInfo()
    {
        if (IsDisposed || _lastUpdateRow is null) return;

        if (InvokeRequired)
        {
            BeginInvoke(RefreshUpdateCheckInfo);
            return;
        }

        _lastUpdateRow.Description = FormatLastUpdateCheck();
    }

    private static string FormatLastUpdateCheck()
    {
        return ConfigService.Current.LastUpdateCheckAt is { } lastCheck
            ? $"{lastCheck.LocalDateTime:yyyy-MM-dd HH:mm}"
            : "尚未检查更新";
    }

    // 页面 5：关于
    private Panel CreateAboutPage()
    {
        Panel panel = CreateBasePage();
        int width = panel.Width - 2;
        Label heading = new()
        {
            Text = "关于", Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Regular),
            AutoSize = true, Location = new Point(0, 2)
        };
        Panel brand = new()
        {
            BackColor = Color.Transparent, Location = new Point(0, 76),
            Size = new Size(width, 72),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        brand.Paint += (_, e) => LogoRenderer.DrawLogo(
            e.Graphics, 2, 10, 42, ThemeManager.Palette.AccentColor);
        Label name = new()
        {
            Text = "ZSnaper", Font = new Font("Segoe UI", 19f, FontStyle.Bold),
            AutoSize = true, Location = new Point(60, 0)
        };
        Label description = new()
        {
            Text = "Zip、Snip、Faster",
            Font = new Font("Microsoft YaHei UI", 9f),
            AutoSize = true, Location = new Point(62, 42)
        };
        brand.Controls.AddRange([name, description]);
        ModernCard details = new()
        {
            CornerRadius = 8, Location = new Point(0, 177),
            Size = new Size(width, 136),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        // Build metadata is immutable. Create these controls once instead of
        // clearing and recreating them on every configuration change.
        (string Caption, string Value)[] items =
        [
            ("版本", AppVersionInfo.DisplayVersion),
            ("发布通道", AppVersionInfo.BuildChannel == "Release" ? "正式版" : "测试版"),
            ("构建", $"{AppVersionInfo.BuildNumber}  ·  {AppVersionInfo.BuildDate}"),
            ("运行环境", "Windows  ·  .NET 8")
        ];
        List<Label> captions = [];
        List<Label> values = [];
        for (int i = 0; i < items.Length; i++)
        {
            Label caption = new()
            {
                Text = items[i].Caption, Font = new Font("Microsoft YaHei UI", 8.8f),
                Location = new Point(16, 12 + i * 29), Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Label value = new()
            {
                Text = items[i].Value, Font = new Font("Segoe UI", 9f),
                Location = new Point(102, 12 + i * 29), Size = new Size(width - 120, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight
            };
            captions.Add(caption);
            values.Add(value);
            details.Controls.AddRange([caption, value]);
        }
        Label author = new()
        {
            Text = "Powered by ZZBuAoYe",
            Font = new Font("Microsoft YaHei UI", 8.5f), AutoSize = true,
            Location = new Point(1, 334)
        };
        void ApplyAboutTheme()
        {
            ThemePalette palette = ThemeManager.Palette;
            heading.ForeColor = name.ForeColor = palette.TextPrimary;
            description.ForeColor = author.ForeColor = palette.TextMuted;
            foreach (Label caption in captions) caption.ForeColor = palette.TextSecondary;
            foreach (Label value in values) value.ForeColor = palette.TextPrimary;
            brand.Invalidate();
            details.Invalidate();
        }
        ApplyAboutTheme();
        ThemeManager.ThemeChanged += ApplyAboutTheme;
        panel.Disposed += (_, _) => ThemeManager.ThemeChanged -= ApplyAboutTheme;
        panel.Controls.AddRange([heading, brand, details, author]);
        return panel;
    }

    // 窗口自由拖拽
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < GraphicsHelper.Dip(this, 55))
        {
            NativeMethods.DragWindow(Handle);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            ApplySidebarLayout(snapPill: true);
        });
        _mainSurfaceDirty = true;
        Invalidate(true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _pageTransitionOverlay.CompleteImmediately();
        _settingsTransitionOverlay?.CompleteImmediately();
        _mainSurfaceDirty = true;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ThemePalette palette = ThemeManager.Palette;
        Size surfaceSize = ClientSize;
        if (_mainSurfaceDirty || _mainSurface.Size != surfaceSize)
        {
            _mainSurface.Render(surfaceSize, canvas =>
            {
                float scale = DeviceDpi / 96f;
                canvas.Scale(scale);
                DrawSkiaBackground(canvas, new Size((int)(surfaceSize.Width / scale), (int)(surfaceSize.Height / scale)), palette);
                DrawLogoArea(canvas, palette);
            });
            _mainSurfaceDirty = false;
        }

        // Skia renders the client in one cached layer. Native WinForms child
        // controls remain responsible for input, accessibility and composition.
        _mainSurface.Draw(e.Graphics, Point.Empty);
    }

    private static void DrawSkiaBackground(SKCanvas canvas, Size size, ThemePalette palette)
    {
        canvas.Clear(SkiaDrawing.ToSkColor(palette.BackgroundColor));
        if (ThemeManager.EnableGlow && palette.GlowColor1.A != 0)
        {
            DrawSoftGlowOrb(canvas, size.Width * 0.8f, 0, size.Width * 0.6f, palette.GlowColor1, 2);
        }

        bool isLight = palette.Mode == ThemeMode.Light;
        bool showLabels = ConfigService.Current.ShowSidebarLabels;
        float railRight = GetSidebarRailWidth(showLabels) + 10f;
        var railRect = new SKRect(10f, 10f, railRight, Math.Max(30f, size.Height - 10f));
        const float railCorner = 20f;

        using (var shadowFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, isLight ? 6f : 10f))
        using (var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Color = SkiaDrawing.ToSkColor(Color.FromArgb(isLight ? 14 : 68, 0, 0, 0)),
            Style = SKPaintStyle.Fill,
            MaskFilter = shadowFilter
        })
        {
            var shadowRect = new SKRect(railRect.Left, railRect.Top + 3f, railRect.Right, railRect.Bottom + 3f);
            canvas.DrawRoundRect(shadowRect, railCorner, railCorner, shadowPaint);
        }

        using (SKPaint navigation = SkiaDrawing.Fill(palette.SidebarBg))
        {
            canvas.DrawRoundRect(railRect, railCorner, railCorner, navigation);
        }

        var innerRimRect = new SKRect(railRect.Left + 1f, railRect.Top + 1f, railRect.Right - 1f, railRect.Bottom - 1f);
        using (SKPaint innerRim = SkiaDrawing.Stroke(Color.FromArgb(isLight ? 190 : 14, 255, 255, 255), 1f))
        {
            canvas.DrawRoundRect(innerRimRect, railCorner - 1f, railCorner - 1f, innerRim);
        }

        using (SKPaint railBorder = SkiaDrawing.Stroke(palette.SidebarBorder, 1f))
        {
            canvas.DrawRoundRect(railRect, railCorner, railCorner, railBorder);
        }
    }

    private static void DrawSoftGlowOrb(
        SKCanvas canvas,
        float centerX,
        float centerY,
        float radius,
        Color color,
        byte centerAlpha)
    {
        SKColor rgb = SkiaDrawing.ToSkColor(color);
        SKColor[] colors =
        [
            rgb.WithAlpha(centerAlpha),
            rgb.WithAlpha((byte)(centerAlpha * 0.92f)),
            rgb.WithAlpha((byte)(centerAlpha * 0.72f)),
            rgb.WithAlpha((byte)(centerAlpha * 0.40f)),
            rgb.WithAlpha(0)
        ];
        float[] stops = [0f, 0.15f, 0.35f, 0.65f, 1f];
        using SKShader shader = SKShader.CreateRadialGradient(
            new SKPoint(centerX, centerY),
            Math.Max(1f, radius),
            colors,
            stops,
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = shader
        };
        canvas.DrawCircle(centerX, centerY, Math.Max(1f, radius), paint);
    }

    private void DrawLogoArea(SKCanvas canvas, ThemePalette palette)
    {
        bool isLight = palette.Mode == ThemeMode.Light;
        bool showLabels = ConfigService.Current.ShowSidebarLabels;
        float railRight = GetSidebarRailWidth(showLabels) + 10f;
        float railCenterX = (10f + railRight) / 2f;

        const float logoCenterY = 36f;
        // Visual bounds of DrawLogo at size S span roughly 99/118 x 117/118.
        const float logoSize = 24f;
        float logoX = railCenterX - (99f / 2f) * (logoSize / 118f);
        float logoY = logoCenterY - (117f / 2f) * (logoSize / 118f);
        SkiaDrawing.DrawLogo(canvas, logoX, logoY, logoSize, palette.AccentColor);

        using (SKPaint divider = SkiaDrawing.Stroke(palette.SeparatorColor, 1f))
        {
            canvas.DrawLine(railCenterX - 11f, 60f, railCenterX + 11f, 60f, divider);
        }

        float titleX = GetSidebarContentLeft(showLabels);
        float titleWidth = MeasureSkiaTextWidth("ZSnaper", "Segoe UI", 19f, SKFontStyleWeight.SemiBold);
        SkiaDrawing.DrawText(canvas, "ZSnaper", "Segoe UI", 19f, palette.TextPrimary,
            titleX, 36f, SKFontStyleWeight.SemiBold);

        string? channelLabel = AppVersionInfo.WelcomeChannelLabel;
#if DEBUG
        channelLabel = "DEV";
#endif
        if (!string.IsNullOrEmpty(channelLabel))
        {
            string badgeText = channelLabel.ToUpperInvariant();
            float badgeTextWidth = MeasureSkiaTextWidth(badgeText, "Segoe UI", 9.5f, SKFontStyleWeight.SemiBold);
            float badgeWidth = badgeTextWidth + 14f;
            float badgeX = titleX + titleWidth + 10f;
            var badgeRect = new SKRect(badgeX, 27f, badgeX + badgeWidth, 45f);
            bool monoAccent = palette.AccentColor.R == palette.AccentColor.G && palette.AccentColor.G == palette.AccentColor.B;
            Color badgeFill = isLight
                ? GraphicsHelper.BlendColor(Color.White, palette.AccentColor, monoAccent ? 0.06f : 0.11f)
                : GraphicsHelper.BlendColor(palette.SurfaceContainerHigh, palette.AccentColor, monoAccent ? 0.16f : 0.24f);
            Color badgeBorder = Color.FromArgb(isLight ? 42 : 72, palette.AccentColor);
            float lum = 0.299f * palette.AccentColor.R + 0.587f * palette.AccentColor.G + 0.114f * palette.AccentColor.B;
            Color badgeForeground = !monoAccent && (isLight ? lum < 150f : lum > 120f)
                ? palette.AccentColor
                : palette.TextPrimary;

            using (SKPaint badgeBg = SkiaDrawing.Fill(badgeFill))
            {
                canvas.DrawRoundRect(badgeRect, 9f, 9f, badgeBg);
            }
            using (SKPaint badgeStroke = SkiaDrawing.Stroke(badgeBorder, 1f))
            {
                canvas.DrawRoundRect(badgeRect, 9f, 9f, badgeStroke);
            }
            SkiaDrawing.DrawText(canvas, badgeText, "Segoe UI", 9.5f, badgeForeground,
                badgeRect.MidX, badgeRect.MidY, SKFontStyleWeight.SemiBold, align: SKTextAlign.Center);
        }
        else if (AppVersionInfo.IsReleaseBuild)
        {
            float quoteX = titleX + titleWidth + 20f;
            SkiaDrawing.DrawText(canvas, LimitHeaderText($"“{_welcomeQuote}”", 30),
                "Microsoft YaHei UI", 9f, palette.TextMuted, quoteX, 33f);
            if (!string.IsNullOrWhiteSpace(_welcomeQuoteSource))
                SkiaDrawing.DrawText(canvas, LimitHeaderText($"— {_welcomeQuoteSource}", 18),
                    "Microsoft YaHei UI", 8f, palette.TextMuted, quoteX, 49f);
        }
    }

    private static float MeasureSkiaTextWidth(string text, string family, float textSize, SKFontStyleWeight weight)
    {
        using SKTypeface typeface = SKTypeface.FromFamilyName(
            family,
            new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        using var paint = new SKPaint
        {
            IsAntialias = true,
            TextSize = textSize,
            Typeface = typeface
        };
        return paint.MeasureText(text);
    }

    private static string LimitHeaderText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, Math.Max(1, maxLength - 1)), "…");
    }
}
