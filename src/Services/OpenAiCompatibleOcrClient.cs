using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ZSnaper.Services;

internal sealed record OcrApiSettings(
    string Endpoint,
    string Model,
    int TimeoutSeconds,
    string? CustomPrompt = null);

public sealed record OcrRecognitionResult(
    string Text,
    string ModelName,
    int? TotalTokens = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);

internal static class OpenAiCompatibleOcrClient
{
    private const string DefaultOcrPrompt =
        "现在你是一个高精度的专业 OCR 文字识别引擎,你现在正工作在 ZSnaper 截图 app 中，你需要严格识别并提取图片中所有可见的文字内容，保持原有换行、段落和阅读顺序。" +
        "严禁任何解释、分析、对话或客套话；严格保留画面的排版结构：独立标题、菜单项、选项、列表和段落必须单独换行，严禁融合成一团；不要使用外部 Markdown 代码块；若图片中没有文字，直接返回空字符串。";

    private static readonly HttpClient Client = CreateClient();

    private static string BuildEffectivePrompt(string? customPrompt)
    {
        if (string.IsNullOrWhiteSpace(customPrompt))
        {
            return DefaultOcrPrompt;
        }

        return
            "【任务：图片文字提取与排版 (OCR)】\n" +
            "你是一个正在ZSnaper工作的高精度的专业 OCR 文字识别引擎,你的唯一任务是识别并提取图片中的所有文字\n\n" +
            "【用户排版与格式要求】\n" +
            $"严格按照以下要求对提取出的文字进行整理和排版输出：\n" +
            $"「{customPrompt.Trim()}」\n\n" +
            "【绝对执行约束】\n" +
            "1. 仅输出从图片中提取和排版后的文字内容本身，严禁任何额外输出。\n" +
            "2. 严禁对图片、指令或提示词进行任何分析、评论、建议、评价、解释或客套问候（绝对不要输出类似「从截图来看」、「好的」、「以下是识别结果」等内容）。\n" +
            "3. 严格保留画面的结构与排版：独立标题、菜单项、选项、列表和段落必须单独换行输出，严禁将多行文字合并为一段长文本。\n" +
            "4. 不要添加多余的外部 Markdown 解释，若图片中无文字，直接返回空字符串。";
    }

    public static async Task<string> RecognizeAsync(
        Bitmap bitmap,
        OcrApiSettings settings,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        OcrRecognitionResult result = await RecognizeDetailedAsync(
            bitmap,
            settings,
            apiKey,
            cancellationToken);
        return result.Text;
    }

    public static async Task<OcrRecognitionResult> RecognizeDetailedAsync(
        Bitmap bitmap,
        OcrApiSettings settings,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Uri endpoint = ResolveEndpoint(settings.Endpoint);
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new OcrConfigurationException("请先填写支持图片输入的模型名称。");
        }

        string effectivePrompt = BuildEffectivePrompt(settings.CustomPrompt);

        string imageDataUrl = CreateImageDataUrl(bitmap);
        string detailLevel = (bitmap.Width <= 512 && bitmap.Height <= 512) ? "low" : "auto";
        var payload = new
        {
            model = settings.Model.Trim(),
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = effectivePrompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = imageDataUrl, detail = detailLevel }
                        }
                    }
                }
            },
            temperature = 0,
            max_tokens = 4096
        };

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 300)));

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OcrProviderException($"OCR API 请求超时（{settings.TimeoutSeconds} 秒）。");
        }
        catch (HttpRequestException exception)
        {
            string fullError = GetFullExceptionMessage(exception);
            throw new OcrProviderException("无法连接 OCR API：" + fullError, exception);
        }

        using (response)
        {
            string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                string detail = ReadErrorMessage(responseBody);
                string status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new OcrProviderException($"OCR API 鉴权失败（{status}）。请检查 API Key。{detail}");
                }

                if (IsMultimodalUnsupportedError(detail, settings.Model))
                {
                    throw new OcrProviderException(
                        $"当前模型「{settings.Model}」不支持多模态/图片输入。\n" +
                        "建议在设置中更换为支持视觉的模型（例如 DeepSeek 的 deepseek-flash，OpenAI 的 gpt-4o，通义千问 qwen2.5-vl 等）。" +
                        (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"\n(接口错误: {detail.Trim()})"));
                }

                throw new OcrProviderException($"OCR API 返回错误（{status}）。{detail}");
            }

            return ReadRecognizedResult(responseBody, settings.Model.Trim());
        }
    }

    public static async Task<OcrRecognitionResult> PolishTextAsync(
        string text,
        string instruction,
        OcrApiSettings settings,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new OcrRecognitionResult(string.Empty, settings.Model);
        }

        Uri endpoint = ResolveEndpoint(settings.Endpoint);
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new OcrConfigurationException("请先填写所使用的模型名称。");
        }

        var payload = new
        {
            model = settings.Model.Trim(),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是一个高效率、极度专注的专业文本排版与处理专家。请严格按照用户的指令对文本进行整理、排版或修改。绝对只输出最终处理后的文本正文，严禁输出任何解释、分析、说明、客套问候或外部 Markdown 代码包裹。"
                },
                new
                {
                    role = "user",
                    content = $"【用户指令】\n{instruction.Trim()}\n\n【待处理文本】\n{text}"
                }
            },
            temperature = 0.2,
            max_tokens = 4096
        };

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 300)));

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OcrProviderException($"AI 整理请求超时（{settings.TimeoutSeconds} 秒）。");
        }
        catch (HttpRequestException exception)
        {
            string fullError = GetFullExceptionMessage(exception);
            throw new OcrProviderException("无法连接 AI 服务：" + fullError, exception);
        }

        using (response)
        {
            string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                string detail = ReadErrorMessage(responseBody);
                string status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                throw new OcrProviderException($"AI 服务返回错误（{status}）。{detail}");
            }

            return ReadRecognizedResult(responseBody, settings.Model.Trim());
        }
    }

    internal static Uri ResolveEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new OcrConfigurationException("API 地址必须是有效的 http:// 或 https:// 地址。");
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        string suffix = path.Length <= 1 ? "/v1/chat/completions" : "/chat/completions";
        var builder = new UriBuilder(uri)
        {
            Path = path.TrimEnd('/') + suffix,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static string CreateImageDataUrl(Bitmap bitmap)
    {
        using MemoryStream stream = new();

        const int maxDimension = 1440;
        int width = bitmap.Width;
        int height = bitmap.Height;

        if (width > maxDimension || height > maxDimension)
        {
            double scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
            int newWidth = Math.Max(1, (int)Math.Round(width * scale));
            int newHeight = Math.Max(1, (int)Math.Round(height * scale));

            using var resized = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(bitmap, 0, 0, newWidth, newHeight);
            }

            SaveOptimizedJpeg(resized, stream, 88);
            return "data:image/jpeg;base64," + Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
        }

        SaveOptimizedJpeg(bitmap, stream, 92);
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void SaveOptimizedJpeg(Bitmap bmp, Stream stream, long quality)
    {
        ImageCodecInfo? jpegCodec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        if (jpegCodec is null)
        {
            bmp.Save(stream, ImageFormat.Png);
            return;
        }

        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        bmp.Save(stream, jpegCodec, encoderParams);
    }

    private static OcrRecognitionResult ReadRecognizedResult(string json, string requestedModel)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out JsonElement message) ||
                !message.TryGetProperty("content", out JsonElement content))
            {
                throw new OcrProviderException("OCR API 响应中没有 choices[0].message.content。");
            }

            string text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(
                    Environment.NewLine,
                    content.EnumerateArray()
                        .Where(item => item.TryGetProperty("text", out JsonElement textNode) &&
                                       textNode.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetProperty("text").GetString())
                        .Where(value => !string.IsNullOrWhiteSpace(value))),
                _ => string.Empty
            };

            string resolvedModel = requestedModel;
            if (root.TryGetProperty("model", out JsonElement modelElem) &&
                modelElem.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(modelElem.GetString()))
            {
                resolvedModel = modelElem.GetString()!;
            }

            int? totalTokens = null;
            int? promptTokens = null;
            int? completionTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usageElem) && usageElem.ValueKind == JsonValueKind.Object)
            {
                if (usageElem.TryGetProperty("total_tokens", out JsonElement totalElem) && totalElem.TryGetInt32(out int tt))
                    totalTokens = tt;
                if (usageElem.TryGetProperty("prompt_tokens", out JsonElement promptElem) && promptElem.TryGetInt32(out int pt))
                    promptTokens = pt;
                if (usageElem.TryGetProperty("completion_tokens", out JsonElement compElem) && compElem.TryGetInt32(out int ct))
                    completionTokens = ct;
            }

            text = StripChatPrefixes(text.Trim());
            text = RemoveMarkdownFence(text.Trim());
            text = StripChatPrefixes(text.Trim());
            text = CheckMultimodalRefusal(text, resolvedModel);

            return new OcrRecognitionResult(text, resolvedModel, totalTokens, promptTokens, completionTokens);
        }
        catch (JsonException exception)
        {
            throw new OcrProviderException("OCR API 返回了无法解析的 JSON。", exception);
        }
    }

    private static string StripChatPrefixes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        string trimmed = text.Trim();
        string[] lines = trimmed.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (lines.Length > 1)
        {
            string firstLine = lines[0].Trim();
            if (IsConversationalPrefix(firstLine))
            {
                return string.Join(Environment.NewLine, lines.Skip(1)).Trim();
            }
        }
        else if (lines.Length == 1 && IsConversationalPrefix(lines[0].Trim()))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static bool IsConversationalPrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 60) return false;
        string l = line.Trim().Trim('：', ':', '。', '！', '!', '、', ' ');
        string lower = l.ToLowerInvariant();

        if (lower is "好的" or "好的，以下是识别结果" or "以下是识别结果" or "识别结果如下" or "识别结果"
            or "以下是图片中的文字" or "以下是图片中的文字内容" or "图片文字提取如下" or "以下是提取出的文字"
            or "好的，以下是提取的内容" or "根据图片识别出的文字如下" or "从图片中提取的内容如下"
            or "图片中的文字如下" or "提取文字如下"
            or "here is the text" or "here is the extracted text" or "here is the recognized text"
            or "sure, here is the text" or "certainly, here is the text" or "sure! here is the text")
        {
            return true;
        }

        if ((lower.StartsWith("好的") || lower.StartsWith("以下是") || lower.StartsWith("根据图片")) &&
            (lower.EndsWith("识别结果") || lower.EndsWith("文字内容") || lower.EndsWith("提取的内容") || lower.EndsWith("如下") || lower.EndsWith("文字")))
        {
            return true;
        }

        return false;
    }

    private static bool IsMultimodalUnsupportedError(string detail, string model)
    {
        string lowerModel = model.Trim().ToLowerInvariant();
        if (lowerModel is "deepseek-chat" or "deepseek-reasoner" or "deepseek-v3" or "deepseek-r1"
            or "gpt-3.5-turbo" or "gpt-3.5" or "text-davinci-003")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(detail)) return false;

        string lowerDetail = detail.ToLowerInvariant();
        return lowerDetail.Contains("image_url") ||
               lowerDetail.Contains("vision") ||
               lowerDetail.Contains("multimodal") ||
               lowerDetail.Contains("multi-modal") ||
               lowerDetail.Contains("not support image") ||
               lowerDetail.Contains("does not support image") ||
               lowerDetail.Contains("doesn't support image") ||
               lowerDetail.Contains("unsupported image") ||
               lowerDetail.Contains("invalid content type") ||
               lowerDetail.Contains("image input") ||
               lowerDetail.Contains("不支持图片") ||
               lowerDetail.Contains("不支持图像") ||
               lowerDetail.Contains("多模态");
    }

    private static string CheckMultimodalRefusal(string text, string model)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string lower = text.ToLowerInvariant();
        bool isRefusal =
            (lower.Contains("无法") && (lower.Contains("查看图片") || lower.Contains("识别图片") || lower.Contains("读取图片") || lower.Contains("处理图片") || lower.Contains("看图"))) ||
            (lower.Contains("文本模型") && (lower.Contains("无法") || lower.Contains("不能"))) ||
            (lower.Contains("纯文本") && (lower.Contains("无法") || lower.Contains("没有视觉"))) ||
            (lower.Contains("cannot") && (lower.Contains("see image") || lower.Contains("view image") || lower.Contains("process image"))) ||
            ((lower.Contains("text-only") || lower.Contains("text-based")) && lower.Contains("cannot"));

        if (isRefusal)
        {
            return text +
                $"\n\n[💡 提示: 模型回复表明其可能为纯文本模型，无法读取图片。请在设置中更换为支持多模态视觉的模型（如 deepseek-flash、gpt-4o 等）。]";
        }

        return text;
    }

    private static string ReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return " " + message.GetString();
                }

                if (error.ValueKind == JsonValueKind.String) return " " + error.GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through to a bounded plain-text error.
        }

        string compact = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length == 0 ? string.Empty : " " + compact[..Math.Min(compact.Length, 400)];
    }

    private static string RemoveMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal) ||
            !text.EndsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        int firstLineEnd = text.IndexOf('\n');
        return firstLineEnd < 0
            ? string.Empty
            : text[(firstLineEnd + 1)..^3].Trim();
    }

    private static string GetFullExceptionMessage(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            string msg = cur.Message.Trim();
            if (msg.StartsWith("The SSL connection could not be established, see inner exception", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(msg) && !parts.Contains(msg))
            {
                parts.Add(msg);
            }
        }
        return parts.Count > 0 ? string.Join(" -> ", parts) : ex.Message;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = HttpClient.DefaultProxy,
            UseProxy = true,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13,
                // Allow corporate proxy MITM / self-signed local gateway certificates
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            }
        };

        HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZSnaper-OCR/1.0");
        return client;
    }
}

public sealed class OcrConfigurationException : InvalidOperationException
{
    public OcrConfigurationException(string message) : base(message)
    {
    }
}

public sealed class OcrProviderException : InvalidOperationException
{
    public OcrProviderException(string message) : base(message)
    {
    }

    public OcrProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
