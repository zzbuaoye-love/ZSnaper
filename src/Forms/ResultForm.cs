using System.Drawing.Drawing2D;
using ZSnaper.Controls;
using ZSnaper.Helpers;
using ZSnaper.Interop;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Forms;

public class ResultForm : Form
{
    private const int NormalHeight = 340;
    private const int ExpandedHeight = 450;

    private readonly ModernTextEditor _text;
    private readonly ModernButton _copyBtn;
    private readonly ModernButton _cleanBtn;
    private readonly ModernButton _optionsBtn;
    private readonly ModernButton _closeBtn;
    private readonly Label _titleLabel;
    private readonly ModelBadgeControl _modelBadge;
    private readonly Label _countLabel;
    private readonly ToolTip _toolTip;
    private readonly System.Windows.Forms.Timer _copyFeedbackTimer;
    private readonly CancellationTokenSource _disposeCancellation = new();

    // AI 文本工作室面板与控件
    private readonly Panel _aiPanel;
    private readonly ModernTextBox _promptBox;
    private readonly ModernButton _runAiBtn;
    private readonly ModernButton _closeAiBtn;
    private readonly ModernButton[] _presetButtons;

    public ResultForm()
    {
        SuspendLayout();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(500, NormalHeight);
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _toolTip = new ToolTip();

        _titleLabel = new Label
        {
            Text = "OCR 识别结果",
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Location = new Point(38, 12),
            AutoSize = true,
            Cursor = Cursors.SizeAll
        };
        _titleLabel.MouseDown += TitleBar_MouseDown;

        _modelBadge = new ModelBadgeControl();
        _modelBadge.MouseDown += TitleBar_MouseDown;

        _countLabel = new Label
        {
            Text = "0 字符",
            Font = new Font("Consolas", 8.5f),
            Location = new Point(150, 14),
            AutoSize = true,
            Cursor = Cursors.SizeAll
        };
        _countLabel.MouseDown += TitleBar_MouseDown;

        _closeBtn = new ModernButton
        {
            Text = string.Empty,
            Icon = LucideIcon.X,
            IconSize = 15,
            IsPrimary = false,
            CornerRadius = 6,
            Size = new Size(28, 26),
            Location = new Point(Width - 38, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _closeBtn.Click += (_, _) => Hide();

        _text = new ModernTextEditor
        {
            Location = new Point(14, 42),
            Size = new Size(Width - 28, Height - 95),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            PlaceholderText = "未识别到文字"
        };
        _text.TextChanged += (_, _) =>
        {
            _countLabel.Text = $"{_text.Text.Length} 字符";
            UpdateHeaderLayout();
        };

        // 底部操作栏
        var bottomBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            BackColor = Color.Transparent,
            Padding = new Padding(14, 6, 14, 6)
        };

        _copyBtn = new ModernButton
        {
            Text = "复制文本",
            Icon = LucideIcon.Copy,
            IsPrimary = true,
            CornerRadius = 8,
            Size = new Size(100, 32),
            Location = new Point(14, 6)
        };
        _copyFeedbackTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            if (!IsDisposed) _copyBtn.Text = "复制文本";
        };
        _copyBtn.Click += (_, _) =>
        {
            if (_text.TextLength > 0)
            {
                bool copied = CaptureService.TryCopyTextToClipboard(_text.Text);
                _copyBtn.Text = copied ? "已复制" : "复制失败";
                _copyFeedbackTimer.Stop();
                _copyFeedbackTimer.Start();
            }
        };

        // 点击「AI 整理」直接执行 AI 智能排版整理
        _cleanBtn = new ModernButton
        {
            Text = "AI 整理",
            Icon = LucideIcon.Sparkles,
            IsPrimary = false,
            CornerRadius = 8,
            Size = new Size(100, 32),
            Location = new Point(122, 6)
        };
        _cleanBtn.Click += (_, _) => _ = ExecuteAiPolishAsync("请智能优化这段文字的排版与分段，保持内容完整，独立标题、选项和菜单严格单独成行。");

        // 点击「更多指令 ▾」展开/收起快捷预设与自定义指令输入框
        _optionsBtn = new ModernButton
        {
            Text = "更多指令 ▾",
            IsPrimary = false,
            CornerRadius = 8,
            Size = new Size(95, 32),
            Location = new Point(230, 6)
        };
        _optionsBtn.Click += (_, _) => ToggleAiPanel();

        bottomBar.Controls.Add(_copyBtn);
        bottomBar.Controls.Add(_cleanBtn);
        bottomBar.Controls.Add(_optionsBtn);

        // AI 整理与分段面板 (抽屉式展开在文本与底栏之间)
        _aiPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 84,
            BackColor = Color.Transparent,
            Visible = false
        };

        _presetButtons =
        [
            CreatePresetButton("智能分段", "请对以下文字进行语义断句与规范分段排版，保持内容完整，独立标题和菜单严格单独成行。", 68),
            CreatePresetButton("转为表格", "将以下文本中的结构化数据或列表整理为规范的 Markdown 表格格式输出。", 68),
            CreatePresetButton("提炼要点", "提炼核心关键信息，整理为清晰的无序列表（Bullet Points）输出。", 68),
            CreatePresetButton("翻译中文", "将以下内容准确翻译为流畅自然的中文。", 68),
            CreatePresetButton("本地分段", "local", 68)
        ];

        _promptBox = new ModernTextBox
        {
            PlaceholderText = "输入自定义指令，例如：帮我删掉快捷键只保留功能名称",
            Height = 32,
            AccessibleName = "AI 整理指令"
        };
        _promptBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = ExecuteAiPolishAsync();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                ToggleAiPanel(false);
            }
        };

        _runAiBtn = new ModernButton
        {
            Text = "执行",
            IsPrimary = true,
            CornerRadius = 7,
            Size = new Size(68, 32),
            Font = new Font("Microsoft YaHei UI", 8.5f)
        };
        _runAiBtn.Click += (_, _) => _ = ExecuteAiPolishAsync();

        _closeAiBtn = new ModernButton
        {
            Text = string.Empty,
            Icon = LucideIcon.X,
            IconSize = 13,
            IsPrimary = false,
            CornerRadius = 7,
            Size = new Size(30, 32)
        };
        _closeAiBtn.Click += (_, _) => ToggleAiPanel(false);

        foreach (var btn in _presetButtons)
        {
            _aiPanel.Controls.Add(btn);
        }
        _aiPanel.Controls.Add(_promptBox);
        _aiPanel.Controls.Add(_runAiBtn);
        _aiPanel.Controls.Add(_closeAiBtn);
        _aiPanel.Layout += (_, _) => UpdateAiPanelLayout();

        Controls.Add(_titleLabel);
        Controls.Add(_modelBadge);
        Controls.Add(_countLabel);
        Controls.Add(_closeBtn);
        Controls.Add(_text);
        Controls.Add(_aiPanel);
        Controls.Add(bottomBar);

        ResumeLayout(false);

        ThemeManager.ThemeChanged += ApplyTheme;
        Disposed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= ApplyTheme;
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
        };
        ApplyTheme();

        Load += (_, _) => NativeMethods.EnableWindowDropShadowAndRoundCorners(Handle, ThemeManager.CurrentMode == ThemeMode.Dark);
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            NativeMethods.DragWindow(Handle);
        }
    }

    private ModernButton CreatePresetButton(string text, string prompt, int width)
    {
        var btn = new ModernButton
        {
            Text = text,
            Tag = prompt,
            IsPrimary = false,
            CornerRadius = 6,
            Font = new Font("Microsoft YaHei UI", 8f),
            Size = new Size(width, 26)
        };
        btn.Click += (_, _) => OnPresetClicked(prompt);
        return btn;
    }

    private void OnPresetClicked(string prompt)
    {
        if (prompt == "local")
        {
            if (_text.TextLength > 0)
            {
                _text.Text = LocalTextSegmenter.SmartSegment(_text.Text);
                _countLabel.Text = $"{_text.Text.Length} 字符";
                UpdateHeaderLayout();
            }
            return;
        }

        _promptBox.Text = prompt;
        _ = ExecuteAiPolishAsync(prompt);
    }

    private void ToggleAiPanel(bool? show = null)
    {
        bool targetState = show ?? !_aiPanel.Visible;
        if (_aiPanel.Visible == targetState) return;

        SuspendLayout();
        _aiPanel.Visible = targetState;
        Height = targetState ? ExpandedHeight : NormalHeight;
        _optionsBtn.Text = targetState ? "收起指令 ▴" : "更多指令 ▾";
        UpdateMainLayout();
        ResumeLayout(true);

        if (targetState)
        {
            UpdateAiPanelLayout();
            _promptBox.Focus();
        }
    }

    private void UpdateMainLayout()
    {
        if (_text == null) return;
        int bottomHeight = 46;
        int aiHeight = (_aiPanel != null && _aiPanel.Visible) ? _aiPanel.Height : 0;
        _text.Bounds = new Rectangle(14, 42, Math.Max(100, Width - 28), Math.Max(50, Height - 42 - bottomHeight - aiHeight - 6));
    }

    private void UpdateAiPanelLayout()
    {
        if (_aiPanel is null || _presetButtons is null || _promptBox is null || _runAiBtn is null || _closeAiBtn is null)
        {
            return;
        }

        int startX = 14;
        foreach (var btn in _presetButtons)
        {
            btn.Location = new Point(startX, 6);
            startX += btn.Width + 6;
        }

        int inputY = 38;
        int actionButtonsWidth = _runAiBtn.Width + 6 + _closeAiBtn.Width;
        int inputWidth = Math.Max(120, _aiPanel.Width - 28 - actionButtonsWidth - 8);

        _promptBox.Location = new Point(14, inputY);
        _promptBox.Size = new Size(inputWidth, 32);

        _runAiBtn.Location = new Point(_promptBox.Right + 8, inputY);
        _closeAiBtn.Location = new Point(_runAiBtn.Right + 6, inputY);
    }

    private async Task ExecuteAiPolishAsync(string? specificPrompt = null)
    {
        string instruction = (specificPrompt ?? _promptBox.Text).Trim();
        if (string.IsNullOrWhiteSpace(instruction))
        {
            _promptBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_text.Text))
        {
            return;
        }

        var config = ConfigService.Current;
        if (config.OcrProvider == OcrProviderKind.WindowsLocal && string.IsNullOrWhiteSpace(config.OcrApiModel))
        {
            MessageBox.Show(
                this,
                "AI 智能整理需要配置模型 API。\n请在【偏好设置 -> OCR】中配置 API 地址与模型，或点击「本地分段」使用纯离线功能。",
                "ZSnaper 提示",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _cleanBtn.Enabled = false;
        _cleanBtn.Text = "整理中...";
        _runAiBtn.Enabled = false;
        _runAiBtn.Text = "处理中...";
        _promptBox.Enabled = false;

        try
        {
            OcrRecognitionResult result = await OcrService.PolishTextAsync(_text.Text, instruction, _disposeCancellation.Token);
            _text.Text = result.Text;
            _countLabel.Text = $"{_text.Text.Length} 字符";

            string badgeTitle = (string.IsNullOrWhiteSpace(result.ModelName) ? "AI" : result.ModelName) + " · 整理";
            _modelBadge.SetModel(badgeTitle, result.TotalTokens, result.PromptTokens, result.CompletionTokens);
            _toolTip.SetToolTip(_modelBadge, _modelBadge.GetTooltipText());

            UpdateHeaderLayout();
            if (_aiPanel.Visible)
            {
                ToggleAiPanel(false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "AI 整理失败：" + ex.Message, "ZSnaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cleanBtn.Enabled = true;
            _cleanBtn.Text = "AI 整理";
            _runAiBtn.Enabled = true;
            _runAiBtn.Text = "执行";
            _promptBox.Enabled = true;
        }
    }

    private void UpdateHeaderLayout()
    {
        if (_modelBadge is null || _titleLabel is null || _countLabel is null)
        {
            return;
        }

        bool hasModel = !string.IsNullOrWhiteSpace(_modelBadge.ModelName);
        _modelBadge.Visible = hasModel;
        if (hasModel)
        {
            _modelBadge.Location = new Point(_titleLabel.Right + 8, 12);
            _countLabel.Location = new Point(_modelBadge.Right + 8, 14);
        }
        else
        {
            _countLabel.Location = new Point(_titleLabel.Right + 10, 14);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        UpdateHeaderLayout();
        UpdateMainLayout();
        UpdateAiPanelLayout();
    }

    private void ApplyTheme()
    {
        if (_titleLabel is null || _countLabel is null || _modelBadge is null || _text is null)
        {
            return;
        }

        var palette = ThemeManager.Palette;
        BackColor = Color.FromArgb(255, palette.CardBg);
        _titleLabel.ForeColor = palette.TextPrimary;
        _countLabel.ForeColor = palette.TextMuted;
        _modelBadge.Invalidate();
        _text.ApplyTheme();
        if (_promptBox is not null) _promptBox.ApplyTheme();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 40)
        {
            NativeMethods.DragWindow(Handle);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var palette = ThemeManager.Palette;

        // 绘制微边框
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = GraphicsHelper.GetRoundedRectangle(rect, 12);
        using var pen = new Pen(palette.CardBorder, 1f);
        g.DrawPath(pen, path);
        LucideRenderer.Draw(g, LucideIcon.FileText, 14, 12, 17, palette.TextSecondary, 1.8f);
    }

    public void ShowResult(
        string text,
        Point near,
        string? modelName = null,
        int? totalTokens = null,
        int? promptTokens = null,
        int? completionTokens = null)
    {
        var vs = SystemInformation.VirtualScreen;
        Location = new Point(
            Math.Clamp(near.X, vs.Left + 10, vs.Right - Width - 10),
            Math.Clamp(near.Y, vs.Top + 10, vs.Bottom - Height - 10));

        _modelBadge.SetModel(modelName, totalTokens, promptTokens, completionTokens);
        _toolTip.SetToolTip(_modelBadge, _modelBadge.GetTooltipText());

        _text.Text = text;
        UpdateHeaderLayout();
        UpdateMainLayout();
        Show();
        UpdateHeaderLayout();
        UpdateMainLayout();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer.Dispose();
            _toolTip.Dispose();
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class ModelBadgeControl : Control
    {
        private string _modelName = string.Empty;
        private string _displayText = string.Empty;
        private int? _totalTokens;
        private int? _promptTokens;
        private int? _completionTokens;

        public ModelBadgeControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            Font = new Font("Microsoft YaHei UI", 8f);
            Height = 20;
            Location = new Point(140, 12);
            Visible = false;
            Cursor = Cursors.SizeAll;
        }

        public string ModelName => _modelName;
        public int? TotalTokens => _totalTokens;

        public void SetModel(string? modelName, int? totalTokens = null, int? promptTokens = null, int? completionTokens = null)
        {
            _modelName = modelName ?? string.Empty;
            _totalTokens = totalTokens;
            _promptTokens = promptTokens;
            _completionTokens = completionTokens;

            if (string.IsNullOrWhiteSpace(_modelName))
            {
                _displayText = string.Empty;
                Width = 0;
                Visible = false;
                return;
            }

            if (_totalTokens.HasValue && _totalTokens.Value > 0)
            {
                _displayText = $"{_modelName} · {_totalTokens.Value}t";
            }
            else
            {
                _displayText = _modelName;
            }

            using var g = CreateGraphics();
            var sz = TextRenderer.MeasureText(g, _displayText, Font);
            Width = Math.Clamp(sz.Width + 16, 40, 260);
            Visible = true;
            Invalidate();
        }

        public string GetTooltipText()
        {
            if (string.IsNullOrWhiteSpace(_modelName)) return string.Empty;
            if (_totalTokens.HasValue && _totalTokens.Value > 0)
            {
                string prompt = _promptTokens.HasValue ? $", 输入: {_promptTokens.Value}" : string.Empty;
                string comp = _completionTokens.HasValue ? $", 输出: {_completionTokens.Value}" : string.Empty;
                return $"模型: {_modelName}\nToken 消耗: {_totalTokens.Value} (总计{prompt}{comp})";
            }
            if (_modelName.Contains("本地离线"))
            {
                return $"模型: {_modelName}\nWindows 本地硬件加速引擎 (0 消耗/离线)";
            }
            return $"模型: {_modelName}";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(_displayText)) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var palette = ThemeManager.Palette;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = GraphicsHelper.GetRoundedRectangle(rect, 4);
            int alphaBg = ThemeManager.CurrentMode == ThemeMode.Dark ? 40 : 25;
            using var brush = new SolidBrush(Color.FromArgb(alphaBg, palette.AccentColor));
            using var pen = new Pen(Color.FromArgb(80, palette.AccentColor), 1f);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);

            TextRenderer.DrawText(
                g,
                _displayText,
                Font,
                new Rectangle(2, 0, Width - 4, Height),
                palette.AccentColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
