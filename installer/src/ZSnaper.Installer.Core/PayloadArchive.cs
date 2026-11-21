using System.IO.Compression;
using System.Text;

namespace ZSnaper.Installer.Core;

public static class PayloadArchive
{
    private static readonly byte[] FooterMarker = Encoding.ASCII.GetBytes("ZSNAPER_PAYLOAD_V1");

    public static string ExtractEmbeddedPayload(string executablePath)
    {
        string sourcePath = Path.GetFullPath(executablePath);
        if (!TryReadPayloadRange(sourcePath, out long offset, out long length))
        {
            throw new InvalidDataException("安装器中没有找到有效的程序载荷");
        }

        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            InstallerPaths.ProductName,
            "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string zipPath = Path.Combine(tempRoot, "payload.zip");

        try
        {
            using (FileStream input = File.OpenRead(sourcePath))
            using (FileStream output = File.Create(zipPath))
            {
                input.Position = offset;
                CopyExactly(input, output, length);
            }

            ZipFile.ExtractToDirectory(zipPath, tempRoot, overwriteFiles: true);
            File.Delete(zipPath);
            return tempRoot;
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    public static bool TryReadPayloadRange(string executablePath, out long offset, out long length)
    {
        offset = 0;
        length = 0;

        using FileStream stream = File.OpenRead(executablePath);
        long footerLength = sizeof(long) + FooterMarker.Length;
        if (stream.Length < footerLength)
        {
            return false;
        }

        stream.Position = stream.Length - FooterMarker.Length;
        byte[] marker = new byte[FooterMarker.Length];
        if (stream.Read(marker, 0, marker.Length) != marker.Length ||
            !marker.AsSpan().SequenceEqual(FooterMarker))
        {
            return false;
        }

        stream.Position = stream.Length - footerLength;
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        length = reader.ReadInt64();
        offset = stream.Length - footerLength - length;
        return length > 0 && offset >= 0;
    }

    public static string CreateUpdatePayloadTempDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            InstallerPaths.ProductName,
            "update-" + Guid.NewGuid().ToString("N"));

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary cleanup must not hide the original installation error.
        }
    }

    private static void CopyExactly(Stream input, Stream output, long length)
    {
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new EndOfStreamException("安装器载荷不完整");
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
