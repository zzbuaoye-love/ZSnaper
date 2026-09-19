using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZSnaper.Models;

namespace ZSnaper.Services;

/// <summary>
/// Coordinates OCR recognition through the configured local or API provider.
/// </summary>
public static class OcrService
{
    private const string MissingLanguagePackMessage =
        "未找到可用的 OCR 语言包，请在 Windows「设置 > 时间和语言 > 语言和区域」中安装语言包。";

    private static readonly Lazy<OcrEngine?> Engine =
        new(CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);

    // OcrEngine is a shared WinRT object. Serialize access instead of assuming
    // that overlapping hotkey and toolbar requests are safe.
    private static readonly SemaphoreSlim RecognitionLock = new(1, 1);

    public static Task<string> RecognizeAsync(Bitmap bitmap) =>
        RecognizeAsync(bitmap, CancellationToken.None);

    public static async Task<string> RecognizeAsync(
        Bitmap bitmap,
        CancellationToken cancellationToken)
    {
        OcrRecognitionResult result = await RecognizeDetailedAsync(bitmap, cancellationToken);
        return result.Text;
    }

    public static Task<OcrRecognitionResult> RecognizeDetailedAsync(Bitmap bitmap) =>
        RecognizeDetailedAsync(bitmap, CancellationToken.None);

    public static async Task<OcrRecognitionResult> RecognizeDetailedAsync(
        Bitmap bitmap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        AppConfig config = ConfigService.Current;
        if (config.OcrProvider == OcrProviderKind.OpenAiCompatible)
        {
            var settings = new OcrApiSettings(
                config.OcrApiEndpoint,
                config.OcrApiModel,
                config.OcrApiTimeoutSeconds,
                config.OcrCustomPrompt);
            return await OpenAiCompatibleOcrClient.RecognizeDetailedAsync(
                bitmap,
                settings,
                OcrCredentialStore.TryGetApiKey(),
                cancellationToken);
        }

        string text = await RecognizeWithWindowsAsync(bitmap, cancellationToken);
        return new OcrRecognitionResult(text, "Windows 本地离线");
    }

    public static string GetActiveModelName()
    {
        AppConfig config = ConfigService.Current;
        if (config.OcrProvider == OcrProviderKind.OpenAiCompatible)
        {
            return string.IsNullOrWhiteSpace(config.OcrApiModel) ? "API 模型" : config.OcrApiModel.Trim();
        }

        return "Windows 本地离线";
    }

    public static async Task<OcrRecognitionResult> PolishTextAsync(
        string text,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        AppConfig config = ConfigService.Current;
        var settings = new OcrApiSettings(
            config.OcrApiEndpoint,
            config.OcrApiModel,
            config.OcrApiTimeoutSeconds);
        return await OpenAiCompatibleOcrClient.PolishTextAsync(
            text,
            instruction,
            settings,
            OcrCredentialStore.TryGetApiKey(),
            cancellationToken);
    }

    internal static Task<string> RecognizeWithApiAsync(
        Bitmap bitmap,
        OcrApiSettings settings,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        OpenAiCompatibleOcrClient.RecognizeAsync(bitmap, settings, apiKey, cancellationToken);

    internal static Task<OcrRecognitionResult> RecognizeDetailedWithApiAsync(
        Bitmap bitmap,
        OcrApiSettings settings,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        OpenAiCompatibleOcrClient.RecognizeDetailedAsync(bitmap, settings, apiKey, cancellationToken);

    private static async Task<string> RecognizeWithWindowsAsync(
        Bitmap bitmap,
        CancellationToken cancellationToken)
    {

        OcrEngine engine = Engine.Value
            ?? throw new OcrUnavailableException(MissingLanguagePackMessage);

        await RecognitionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using OcrPreparedImage preparedImage = OcrImagePreprocessor.Prepare(bitmap);
            using SoftwareBitmap softwareBitmap = SoftwareBitmapFactory.Create(preparedImage.Bitmap);

            OcrResult result = await engine.RecognizeAsync(softwareBitmap);
            cancellationToken.ThrowIfCancellationRequested();
            return FormatResult(result);
        }
        finally
        {
            RecognitionLock.Release();
        }
    }

    private static OcrEngine? CreateEngine()
    {
        // Windows selects the first OCR-capable language in the user's profile.
        // This is both more predictable and cheaper than running every installed
        // language until one happens to emit non-empty (possibly incorrect) text.
        OcrEngine? profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (profileEngine is not null)
        {
            return profileEngine;
        }

        foreach (Windows.Globalization.Language language in OcrEngine.AvailableRecognizerLanguages)
        {
            OcrEngine? fallbackEngine = OcrEngine.TryCreateFromLanguage(language);
            if (fallbackEngine is not null)
            {
                return fallbackEngine;
            }
        }

        return null;
    }

    private static string FormatResult(OcrResult result)
    {
        IEnumerable<string> lines = result.Lines
            .Select(line => OcrTextFormatter.JoinRecognizedWords(
                line.Words.Select(word => word.Text)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim());

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class OcrUnavailableException : InvalidOperationException
{
    public OcrUnavailableException(string message)
        : base(message)
    {
    }
}
