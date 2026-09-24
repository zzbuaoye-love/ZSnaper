using System.Runtime.InteropServices;

namespace ZSnaper.Interop;

internal static partial class NativeMethods
{
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    public const uint VK_Q = 0x51;
    public const uint VK_X = 0x58;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 0x0002;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const uint CWP_SKIPINVISIBLE = 0x0001;
    public const uint CWP_SKIPDISABLED = 0x0002;
    public const uint CWP_SKIPTRANSPARENT = 0x0004;
    public const int CURSOR_SHOWING = 0x00000001;
    public const uint DI_NORMAL = 0x0003;
    public const uint DI_DEFAULTSIZE = 0x0008;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const int VK_ESCAPE = 0x1B;
    public const uint GA_ROOT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // DWM
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_MAINWINDOW = 2;
    public const int DWMSBT_TRANSIENTWINDOW = 3;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageA")]
    public static partial nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorInfo(ref CURSORINFO cursorInfo);

    [LibraryImport("user32.dll")]
    public static partial nint CopyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint hIcon, out ICONINFO iconInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "mouse_event")]
    public static partial void MouseEvent(
        uint flags,
        uint dx,
        uint dy,
        int data,
        nuint extraInfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rectangle);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hWnd, ref POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint ChildWindowFromPointEx(nint hWndParent, POINT point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(nint hWnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(
        nint hdc,
        int xLeft,
        int yTop,
        nint hIcon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        nint hbrFlickerFreeDraw,
        uint diFlags);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint hObject);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attr, in int attrValue, int attrSize);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public static implicit operator Point(POINT point) => new(point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(nint hWnd, in MARGINS pMarInset);

    public static void EnableWindowDropShadowAndRoundCorners(nint handle, bool isDark)
    {
        try
        {
            var margins = new MARGINS { cxLeftWidth = 1, cxRightWidth = 1, cyTopHeight = 1, cyBottomHeight = 1 };
            DwmExtendFrameIntoClientArea(handle, in margins);

            int cornerVal = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, in cornerVal, sizeof(int));

            int darkVal = isDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, in darkVal, sizeof(int));
        }
        catch
        {
            // Fallback gracefully on older Windows
        }
    }

    public static bool TryEnableSystemBackdrop(nint handle, bool isDark, bool useDesktopAcrylic)
    {
        EnableWindowDropShadowAndRoundCorners(handle, isDark);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return false;
        }

        try
        {
            var fullClientFrame = new MARGINS
            {
                cxLeftWidth = -1,
                cxRightWidth = -1,
                cyTopHeight = -1,
                cyBottomHeight = -1
            };
            DwmExtendFrameIntoClientArea(handle, in fullClientFrame);

            int backdropType = useDesktopAcrylic ? DWMSBT_TRANSIENTWINDOW : DWMSBT_MAINWINDOW;
            int result = DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, in backdropType, sizeof(int));
            return result >= 0;
        }
        catch
        {
            return false;
        }
    }

    public static void DragWindow(nint handle)
    {
        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }
}
