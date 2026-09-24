using System.Diagnostics;
using ZSnaper.Controls;
using ZSnaper.Models;
using ZSnaper.Plugins;
using ZSnaper.Services;

namespace ZSnaper.Forms;

public partial class MainForm
{
    private PluginRuntimeManager? _pluginManager;
    private FlowLayoutPanel _pluginList = null!;
    private FlowLayoutPanel _pluginActions = null!;
    private Label _pluginStatus = null!;
    private Label _pluginActionHeading = null!;
    private Panel _pluginPage = null!;

    public void SetPluginManager(PluginRuntimeManager manager)
    {
        if (_pluginManager is not null) _pluginManager.Changed -= RefreshPluginPage;
        _pluginManager = manager;
        manager.Changed += RefreshPluginPage;
        Disposed += (_, _) => manager.Changed -= RefreshPluginPage;
        RefreshPluginPage();
    }

    private Panel CreatePluginsPage()
    {
        _pluginPage = CreateBasePage();
        Label heading = new()
        {
            Text = "插件管理", Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            Location = new Point(0, 2), Size = new Size(250, 34)
        };
        Label description = new()
        {
            Text = "安装 .zsp 插件，并管理启用状态与截图动作。",
            Font = new Font("Microsoft YaHei UI", 9f), Location = new Point(1, 40),
            Size = new Size(500, 23), AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        ModernButton install = new()
        {
            Text = "安装插件", Icon = LucideIcon.Download,
            Size = new Size(125, 32), Location = new Point(0, 72)
        };
        install.Click += async (_, _) =>
        {
            using OpenFileDialog dialog = new()
            {
                Title = "选择 ZSnaper 插件包",
                Filter = "ZSnaper 插件 (*.zsp)|*.zsp",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK || _pluginManager is null) return;
            install.Enabled = false;
            try
            {
                PluginOperationResult result = await Task.Run(() => _pluginManager.Install(dialog.FileName));
                ShowPluginStatus(result);
            }
            finally { install.Enabled = true; }
        };
        ModernButton folder = new()
        {
            Text = "打开插件目录", Icon = LucideIcon.Folder, IsPrimary = false,
            Size = new Size(146, 32), Location = new Point(135, 72)
        };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(PluginStorage.InstalledDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = $"\"{PluginStorage.InstalledDirectory}\"",
                UseShellExecute = true
            });
        };
        _pluginStatus = new Label
        {
            Text = "插件在启用后运行，请只启用可信来源的插件。",
            Font = new Font("Microsoft YaHei UI", 8.5f),
            Location = new Point(0, 111), Size = new Size(500, 23),
            AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _pluginActionHeading = new Label
        {
            Text = "上次截图的插件动作", Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Location = new Point(0, 139), Size = new Size(300, 22), Visible = false
        };
        _pluginActions = new FlowLayoutPanel
        {
            Location = new Point(0, 163), Height = 38, AutoScroll = true,
            WrapContents = false, BackColor = Color.Transparent, Visible = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _pluginList = new FlowLayoutPanel
        {
            Location = new Point(0, 144), AutoScroll = true, WrapContents = false,
            FlowDirection = FlowDirection.TopDown, BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _pluginPage.Controls.AddRange([heading, description, install, folder,
            _pluginStatus, _pluginActionHeading, _pluginActions, _pluginList]);

        void Layout()
        {
            int width = Math.Max(260, _pluginPage.ClientSize.Width - 2);
            description.Width = _pluginStatus.Width = width;
            bool hasActions = _pluginActions.Visible;
            _pluginActions.Width = width;
            _pluginList.SetBounds(0, hasActions ? 208 : 144, width,
                Math.Max(80, _pluginPage.ClientSize.Height - (hasActions ? 208 : 144)));
            foreach (Control row in _pluginList.Controls) row.Width = Math.Max(240, width - 20);
        }

        _pluginPage.Resize += (_, _) => Layout();
        ThemeManager.ThemeChanged += ApplyTheme;
        _pluginPage.Disposed += (_, _) => ThemeManager.ThemeChanged -= ApplyTheme;
        void ApplyTheme()
        {
            ThemePalette palette = ThemeManager.Palette;
            heading.ForeColor = _pluginActionHeading.ForeColor = palette.TextPrimary;
            description.ForeColor = _pluginStatus.ForeColor = palette.TextSecondary;
            _pluginPage.Invalidate(true);
        }
        ApplyTheme();
        Layout();
        return _pluginPage;
    }

    private void RefreshPluginPage()
    {
        if (_pluginManager is null || _pluginPage.IsDisposed) return;
        if (_pluginPage.InvokeRequired)
        {
            if (_pluginPage.IsHandleCreated) _pluginPage.BeginInvoke((Action)RefreshPluginPage);
            return;
        }

        _pluginList.SuspendLayout();
        foreach (Control old in _pluginList.Controls.Cast<Control>().ToArray()) old.Dispose();
        _pluginList.Controls.Clear();
        IReadOnlyList<InstalledPlugin> plugins;
        try { plugins = _pluginManager.List(); }
        catch (Exception exception)
        {
            ShowPluginStatus(new(false, "无法读取插件目录：" + exception.Message));
            plugins = [];
        }
        if (plugins.Count == 0)
        {
            _pluginList.Controls.Add(new Label
            {
                Text = "尚未安装插件。可导入 .zsp 插件包。",
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = ThemeManager.Palette.TextSecondary,
                Width = Math.Max(240, _pluginList.ClientSize.Width - 20), Height = 42,
                TextAlign = ContentAlignment.MiddleLeft
            });
        }
        foreach (InstalledPlugin plugin in plugins) AddPluginRow(plugin);
        _pluginList.ResumeLayout();

        foreach (Control old in _pluginActions.Controls.Cast<Control>().ToArray()) old.Dispose();
        _pluginActions.Controls.Clear();
        if (_pluginManager.HasCapture)
        {
            foreach (PluginActionInfo action in _pluginManager.GetActions())
            {
                ModernButton button = new()
                {
                    Text = action.Item.Label, IsPrimary = false,
                    Size = new Size(145, 30), Margin = new Padding(0, 0, 7, 0)
                };
                button.Click += async (_, _) =>
                    ShowPluginStatus(await _pluginManager.RunActionAsync(action.PluginId, action.Item.Id));
                _pluginActions.Controls.Add(button);
            }
        }
        bool visible = _pluginActions.Controls.Count > 0;
        _pluginActions.Visible = _pluginActionHeading.Visible = visible;
        _pluginPage.PerformLayout();
        int top = visible ? 208 : 144;
        int width = Math.Max(260, _pluginPage.ClientSize.Width - 2);
        _pluginActions.Width = width;
        _pluginList.SetBounds(0, top, width, Math.Max(80, _pluginPage.ClientSize.Height - top));
    }

    private void AddPluginRow(InstalledPlugin plugin)
    {
        PluginRuntimeManager manager = _pluginManager!;
        int width = Math.Max(240, _pluginList.ClientSize.Width - 20);
        ModernCard card = new()
        {
            Size = new Size(width, 92), Margin = new Padding(0, 0, 0, 9)
        };
        Label title = new()
        {
            Text = $"{plugin.Manifest.Name}  v{plugin.Manifest.Version}",
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = ThemeManager.Palette.TextPrimary,
            Location = new Point(12, 8), Size = new Size(Math.Max(90, width - 185), 23),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AutoEllipsis = true
        };
        Label description = new()
        {
            Text = string.IsNullOrWhiteSpace(plugin.Manifest.Description) ? plugin.Manifest.Id : plugin.Manifest.Description,
            Font = new Font("Microsoft YaHei UI", 8f), ForeColor = ThemeManager.Palette.TextSecondary,
            Location = new Point(12, 34), Size = new Size(Math.Max(90, width - 185), 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AutoEllipsis = true
        };
        Label status = new()
        {
            Text = !plugin.Compatible ? "与当前版本不兼容"
                : manager.IsRunning(plugin.Manifest.Id) ? "运行中"
                : manager.GetError(plugin.Manifest.Id) is not null ? "启用失败：" + manager.GetError(plugin.Manifest.Id)
                : "已停用",
            Font = new Font("Microsoft YaHei UI", 8f), ForeColor = ThemeManager.Palette.TextMuted,
            Location = new Point(12, 61), Size = new Size(width - 24, 20), AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        ModernButton toggle = new()
        {
            Text = manager.IsRunning(plugin.Manifest.Id) ? "停用" : "启用",
            IsPrimary = !manager.IsRunning(plugin.Manifest.Id),
            Enabled = plugin.Compatible || manager.IsRunning(plugin.Manifest.Id),
            Size = new Size(70, 29), Location = new Point(width - 160, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        toggle.Click += async (_, _) =>
        {
            if (_pluginManager is null) return;
            if (!_pluginManager.IsRunning(plugin.Manifest.Id) &&
                MessageBox.Show(this,
                    "插件代码会在 ZSnaper 进程内运行，拥有与应用相同的系统权限。仅启用可信来源的插件。\n\n继续启用？",
                    "启用插件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            toggle.Enabled = false;
            PluginOperationResult result = _pluginManager.IsRunning(plugin.Manifest.Id)
                ? await _pluginManager.DisableAsync(plugin.Manifest.Id)
                : await _pluginManager.EnableAsync(plugin.Manifest.Id);
            ShowPluginStatus(result);
        };
        ModernButton remove = new()
        {
            Text = "卸载", IsPrimary = false, Size = new Size(70, 29),
            Location = new Point(width - 82, 12), Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        remove.Click += async (_, _) =>
        {
            if (_pluginManager is null ||
                MessageBox.Show(this, $"卸载 {plugin.Manifest.Name}？", "卸载插件",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            remove.Enabled = false;
            ShowPluginStatus(await _pluginManager.RemoveAsync(plugin.Manifest.Id));
        };
        card.Controls.AddRange([title, description, status, toggle, remove]);
        _pluginList.Controls.Add(card);
    }

    private void ShowPluginStatus(PluginOperationResult result)
    {
        if (_pluginStatus.IsDisposed) return;
        _pluginStatus.Text = result.Message;
        _pluginStatus.ForeColor = result.Success
            ? ThemeManager.Palette.TextSecondary
            : Color.IndianRed;
    }
}
