using System.Runtime.InteropServices;

namespace ZSnaper.Installer.Core;

public static class ShortcutService
{
    public static void Create(
        string shortcutPath,
        string targetPath,
        string arguments = "",
        string? workingDirectory = null,
        string? description = null)
    {
        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Shell 快捷方式组件不可用");
        object? shell = Activator.CreateInstance(shellType);
        if (shell is null)
        {
            throw new InvalidOperationException("无法创建 Windows Shell 快捷方式对象");
        }

        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            if (shortcut is null)
            {
                throw new InvalidOperationException("无法创建快捷方式");
            }

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, [arguments]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [workingDirectory ?? Path.GetDirectoryName(targetPath) ?? string.Empty]);
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, [description ?? InstallerPaths.ProductName]);
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{targetPath},0"]);
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    public static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Shortcut cleanup is best effort during uninstall.
        }
    }

    public static void DeleteIfOwned(string path, string expectedTargetPath)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [path]);
            if (shortcut is null)
            {
                return;
            }

            string? targetPath = shortcut.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;
            if (targetPath is not null &&
                string.Equals(
                    InstallerPaths.Normalize(targetPath),
                    InstallerPaths.Normalize(expectedTargetPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Shortcut cleanup is best effort during uninstall.
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
