using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class OcrSettingsPanel : ModernCard
{
    private static readonly (int Seconds, string Label)[] TimeoutOptions =
    [
        (15, "15 秒"),
        (30, "30 秒"),
        (60, "60 秒 (默认)"),
        (90, "90 秒"),
        (120, "120 秒"),
        (180, "180 秒"),
        (300, "300 秒")
    ];

    private readonly ModernDropdown _providerDropdown;
    private readonly ModernDropdown _timeoutDropdown;
    private readonly ModernTextBox _endpointTextBox;
    private readonly ModernTextBox _modelTextBox;
    private readonly ModernTextBox _apiKeyTextBox;
    private readonly ModernTextBox _promptTextBox;
    private readonly ModernButton _saveButton;
    private readonly ModernButton _testButton;
    private readonly ModernButton _clearKeyButton;
    private readonly Label _statusLabel;
    private readonly Label _promptHintLabel;
    private readonly Label[] _fieldLabels;
    private readonly CancellationTokenSource _disposeCancellation = new();

    private bool ApiSelected => _providerDropdown.SelectedIndex == 1;

    public OcrSettingsPanel()
    {
        CornerRadius = 10;

        Label providerLabel = CreateLabel("识别引擎");
        Label timeoutLabel = CreateLabel("请求超时");
        Label endpointLabel = CreateLabel("API 地址");
        Label modelLabel = CreateLabel("视觉模型");
        Label apiKeyLabel = CreateLabel("API Key（可选，本机加密保存）");
        Label promptLabel = CreateLabel("前置提示词（对多模态模型生效，留空使用内置）");
        _fieldLabels = [providerLabel, timeoutLabel, endpointLabel, modelLabel, apiKeyLabel, promptLabel];

        _providerDropdown = new ModernDropdown
        {
            AccessibleName = "OCR 识别引擎",
            Size = new Size(200, 30)
        };
        _providerDropdown.SetItems(["Windows 本地离线", "OpenAI 兼容 API"]);
        _providerDropdown.SelectedIndex = ConfigService.Current.OcrProvider == OcrProviderKind.OpenAiCompatible ? 1 : 0;
        _providerDropdown.SelectedIndexChanged += (_, _) => UpdateApiControls();

        _timeoutDropdown = new ModernDropdown
        {
            AccessibleName = "OCR 请求超时",
            Size = new Size(130, 30)
        };
        _timeoutDropdown.SetItems(TimeoutOptions.Select(t => t.Label));
        int currentTimeout = ConfigService.Current.OcrApiTimeoutSeconds;
        int timeoutIdx = Array.FindIndex(TimeoutOptions, t => t.Seconds == currentTimeout);
        _timeoutDropdown.SelectedIndex = timeoutIdx >= 0 ? timeoutIdx : 2; // Default to 60s

        _endpointTextBox = new ModernTextBox
        {
            PlaceholderText = "例如 https://api.deepseek.com 或 http://127.0.0.1:11434/v1",
            AccessibleName = "API 地址",
            Height = 32
        };
        _endpointTextBox.Text = ConfigService.Current.OcrApiEndpoint;

        _modelTextBox = new ModernTextBox
        {
            PlaceholderText = "例如 deepseek-flash、qwen2.5-vl、gpt-4o",
            AccessibleName = "视觉模型",
            Height = 32
        };
        _modelTextBox.Text = ConfigService.Current.OcrApiModel;

        _apiKeyTextBox = new ModernTextBox
        {
            PlaceholderText = OcrCredentialStore.HasApiKey ? "已保存；留空则继续使用" : "本地免鉴权服务可留空",
            UseSystemPasswordChar = true,
            AccessibleName = "API Key",
            Height = 32
        };

        _clearKeyButton = new ModernButton
        {
            Text = "清除密钥",
            IsPrimary = false,
            CornerRadius = 7,
            Size = new Size(82, 32)
        };
        _clearKeyButton.Click += (_, _) => ClearApiKey();

        _promptTextBox = new ModernTextBox
        {
            PlaceholderText = "留空使用内置默认提示词（提取图中文字，保持原有排版与换行，不加解释）",
            AccessibleName = "OCR 前置提示词",
            Multiline = true,
            Height = 64
        };
        _promptTextBox.Text = ConfigService.Current.OcrCustomPrompt;

        _promptHintLabel = new Label
        {
            Text = "💡 前置提示词对多模态视觉模型生效。可指定如「提取为 Markdown 表格」或「提取文字并翻译」；留空则提取纯文字。",
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 8f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        _saveButton = new ModernButton
        {
            Text = "保存并应用",
            Icon = LucideIcon.Check,
            IconSize = 14,
            IconGap = 5,
            IsPrimary = true,
            CornerRadius = 7,
            Size = new Size(114, 34)
        };
        _saveButton.Click += (_, _) => SaveSettings();

        _testButton = new ModernButton
        {
            Text = "测试识别",
            Icon = LucideIcon.Sparkles,
            IconSize = 14,
            IconGap = 5,
            IsPrimary = false,
            CornerRadius = 7,
            Size = new Size(104, 34)
        };
        _testButton.Click += async (_, _) => await TestApiAsync();

        _statusLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 8.2f),
            Text = "本地模式不联网；API 模式会把所选截图发送到你配置的服务。",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        Controls.Add(providerLabel);
        Controls.Add(_providerDropdown);
        Controls.Add(timeoutLabel);
        Controls.Add(_timeoutDropdown);

        Controls.Add(endpointLabel);
        Controls.Add(_endpointTextBox);

        Controls.Add(modelLabel);
        Controls.Add(_modelTextBox);

        Controls.Add(apiKeyLabel);
        Controls.Add(_apiKeyTextBox);
        Controls.Add(_clearKeyButton);

        Controls.Add(promptLabel);
        Controls.Add(_promptTextBox);
        Controls.Add(_promptHintLabel);

        Controls.Add(_saveButton);
        Controls.Add(_testButton);
        Controls.Add(_statusLabel);

        ThemeManager.ThemeChanged += ApplyTheme;
        ApplyTheme();
        UpdateApiControls();
        Size = new Size(460, 484);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int left = 16;
        int availableWidth = Math.Max(120, ClientSize.Width - 32);

        // Row 0: Provider & Timeout
        _fieldLabels[0].SetBounds(left, 14, 160, 18);
        _providerDropdown.Location = new Point(left, 35);
        _fieldLabels[1].SetBounds(ClientSize.Width - 146, 14, 130, 18);
        _timeoutDropdown.SetBounds(ClientSize.Width - 146, 35, 130, 30);

        // Row 1: Endpoint
        _fieldLabels[2].SetBounds(left, 76, availableWidth, 18);
        _endpointTextBox.SetBounds(left, 97, availableWidth, 32);

        // Row 2: Model
        _fieldLabels[3].SetBounds(left, 138, availableWidth, 18);
        _modelTextBox.SetBounds(left, 159, availableWidth, 32);

        // Row 3: API Key & Clear Button
        _fieldLabels[4].SetBounds(left, 200, availableWidth, 18);
        int clearBtnWidth = 84;
        _apiKeyTextBox.SetBounds(left, 221, Math.Max(80, availableWidth - clearBtnWidth - 8), 32);
        _clearKeyButton.SetBounds(ClientSize.Width - 16 - clearBtnWidth, 221, clearBtnWidth, 32);

        // Row 4: Prompt & Hint
        _fieldLabels[5].SetBounds(left, 262, availableWidth, 18);
        _promptTextBox.SetBounds(left, 283, availableWidth, 64);
        _promptHintLabel.SetBounds(left, 351, availableWidth, 18);

        // Row 5: Action Buttons
        _saveButton.Location = new Point(left, 380);
        _testButton.Location = new Point(left + 122, 380);

        // Row 6: Status
        _statusLabel.SetBounds(left, 424, availableWidth, 44);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
            ThemeManager.ThemeChanged -= ApplyTheme;
        }

        base.Dispose(disposing);
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold)
    };

    private void UpdateApiControls()
    {
        bool enabled = ApiSelected;
        _endpointTextBox.Enabled = enabled;
        _modelTextBox.Enabled = enabled;
        _apiKeyTextBox.Enabled = enabled;
        _promptTextBox.Enabled = enabled;
        _timeoutDropdown.Enabled = enabled;
        _clearKeyButton.Enabled = enabled && OcrCredentialStore.HasApiKey;
        _testButton.Enabled = enabled;
        _statusLabel.Text = enabled
            ? "兼容 /v1 或完整 /chat/completions 地址；本地服务可不填 API Key。"
            : "Windows 本地离线识别，不上传图片，也不需要 API Key。";
        ApplyTheme();
    }

    private int GetSelectedTimeoutSeconds()
    {
        int idx = _timeoutDropdown.SelectedIndex;
        if (idx >= 0 && idx < TimeoutOptions.Length)
        {
            return TimeoutOptions[idx].Seconds;
        }
        return 60;
    }

    private void SaveSettings()
    {
        try
        {
            OcrProviderKind provider = ApiSelected
                ? OcrProviderKind.OpenAiCompatible
                : OcrProviderKind.WindowsLocal;
            if (provider == OcrProviderKind.OpenAiCompatible)
            {
                _ = BuildApiSettings();
                if (!string.IsNullOrWhiteSpace(_apiKeyTextBox.Text))
                {
                    OcrCredentialStore.SaveApiKey(_apiKeyTextBox.Text);
                    _apiKeyTextBox.Clear();
                    _apiKeyTextBox.PlaceholderText = "已保存；留空则继续使用";
                }
            }

            ConfigService.Current.OcrProvider = provider;
            ConfigService.Current.OcrApiEndpoint = _endpointTextBox.Text.Trim();
            ConfigService.Current.OcrApiModel = _modelTextBox.Text.Trim();
            ConfigService.Current.OcrApiTimeoutSeconds = GetSelectedTimeoutSeconds();
            ConfigService.Current.OcrCustomPrompt = _promptTextBox.Text.Trim();
            if (!ConfigService.Save())
            {
                throw new InvalidOperationException("配置文件写入失败，请查看本地日志。");
            }

            _clearKeyButton.Enabled = ApiSelected && OcrCredentialStore.HasApiKey;
            ShowStatus("设置已保存，下一次 OCR 立即使用此引擎。", success: true);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("OcrSettingsPanel.Save", exception);
            ShowStatus(exception.Message, success: false);
        }
    }

    private async Task TestApiAsync()
    {
        try
        {
            OcrApiSettings settings = BuildApiSettings();
            string? apiKey = string.IsNullOrWhiteSpace(_apiKeyTextBox.Text)
                ? OcrCredentialStore.TryGetApiKey()
                : _apiKeyTextBox.Text;

            SetBusy(true);
            ShowStatus("正在发送测试图片…", success: true);
            using Bitmap bitmap = CreateTestBitmap();
            string result = await OcrService.RecognizeWithApiAsync(
                bitmap,
                settings,
                apiKey,
                _disposeCancellation.Token);

            string preview = string.IsNullOrWhiteSpace(result)
                ? "连接成功，但模型没有返回文字。请确认它支持图片输入。"
                : "连接成功，识别结果：" + result.ReplaceLineEndings(" / ");
            ShowStatus(preview, success: !string.IsNullOrWhiteSpace(result));
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            // The settings page is closing.
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("OcrSettingsPanel.Test", exception);
            ShowStatus(exception.Message, success: false);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private OcrApiSettings BuildApiSettings()
    {
        string endpoint = _endpointTextBox.Text.Trim();
        _ = OpenAiCompatibleOcrClient.ResolveEndpoint(endpoint);
        string model = _modelTextBox.Text.Trim();
        if (model.Length == 0)
        {
            throw new OcrConfigurationException("请填写支持图片输入的模型名称。");
        }

        return new OcrApiSettings(endpoint, model, GetSelectedTimeoutSeconds(), _promptTextBox.Text.Trim());
    }

    private void ClearApiKey()
    {
        try
        {
            OcrCredentialStore.ClearApiKey();
            _apiKeyTextBox.Clear();
            _apiKeyTextBox.PlaceholderText = "本地免鉴权服务可留空";
            _clearKeyButton.Enabled = false;
            ShowStatus("已从 Windows 凭据管理器清除 OCR API Key。", success: true);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException("OcrSettingsPanel.ClearKey", exception);
            ShowStatus("无法清除 API Key：" + exception.Message, success: false);
        }
    }

    private void SetBusy(bool busy)
    {
        _testButton.Enabled = !busy && ApiSelected;
        _saveButton.Enabled = !busy;
        _providerDropdown.Enabled = !busy;
        _testButton.Text = busy ? "测试中…" : "测试识别";
    }

    private void ShowStatus(string text, bool success)
    {
        if (IsDisposed) return;
        _statusLabel.Text = text;
        _statusLabel.ForeColor = success
            ? ThemeManager.Palette.TextSecondary
            : Color.FromArgb(235, 75, 75);
    }

    private void ApplyTheme()
    {
        if (IsDisposed) return;
        ThemePalette palette = ThemeManager.Palette;
        foreach (Label label in _fieldLabels)
        {
            label.ForeColor = palette.TextPrimary;
        }

        _promptHintLabel.ForeColor = palette.TextMuted;
        _statusLabel.ForeColor = palette.TextSecondary;

        _endpointTextBox.ApplyTheme();
        _modelTextBox.ApplyTheme();
        _apiKeyTextBox.ApplyTheme();
        _promptTextBox.ApplyTheme();

        Invalidate(true);
    }

    private static Bitmap CreateTestBitmap()
    {
        Bitmap bitmap = new(520, 120);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using Font font = new("Segoe UI", 32f, FontStyle.Bold, GraphicsUnit.Pixel);
        using Brush brush = new SolidBrush(Color.Black);
        graphics.DrawString("ZSnaper OCR 123", font, brush, new PointF(24, 34));
        return bitmap;
    }
}
