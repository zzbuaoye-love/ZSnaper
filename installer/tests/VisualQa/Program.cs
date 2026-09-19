using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ZSnaper.FullInstaller;

namespace ZSnaper.Installer.VisualQa;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string outputDirectory = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "visual-qa"));
        Directory.CreateDirectory(outputDirectory);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        InstallerWindow window = new(null, "ZSnaper.Setup.exe", "0.0.5-beta")
        {
            ShowInTaskbar = false
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        for (int page = 0; page < 4; page++)
        {
            window.ShowPreviewPage(page);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(520);
            Dispatcher.UIThread.RunJobs();
            using var bitmap = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException($"Page {page + 1} did not render a frame.");
            string file = Path.Combine(outputDirectory, $"page-{page + 1}.png");
            using FileStream stream = File.Create(file);
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            Console.WriteLine(file);
        }

        window.Close();
        return 0;
    }
}
