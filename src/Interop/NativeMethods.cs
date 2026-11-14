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
    public const int WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int HTCAPTION = 0x0002;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint CWP_SKIPINVISIBLE = 0x0001;
    public const uint CWP_SKIPDISABLED = 0x0002;
    public const uint CWP_SKIPTRANSPARENT = 0x0004;
    public const uint GW_HWNDNEXT = 2;
    public const int CURSOR_SHOWING = 0x00000001;
    public const uint DI_NORMAL = 0x0003;
    public const uint DI_DEFAULTSIZE = 0x0008;
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    public const int VK_ESCAPE = 0x1B;
    public const uint GA_ROOT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

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
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

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

    [LibraryImport("user32.dll")]
    public static partial nint GetTopWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint cmd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int structSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLayeredWindowAttributes(
        nint hWnd,
        out uint crKey,
        out byte bAlpha,
        out uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static partial nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nint wParam,
        nint lParam,
        uint fuFlags,
        uint uTimeout,
        out nint lpdwResult);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

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

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

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

    public static bool WarmMouseHover(nint targetWindow, Point screenPoint)
    {
        if (targetWindow == 0)
        {
            return false;
        }

        var clientPoint = new POINT { X = screenPoint.X, Y = screenPoint.Y };
        if (!ScreenToClient(targetWindow, ref clientPoint))
        {
            return false;
        }

        nint clientLParam = unchecked((nint)(int)(((clientPoint.Y & 0xFFFF) << 16) | (clientPoint.X & 0xFFFF)));
        return PostMessage(targetWindow, WM_MOUSEMOVE, 0, clientLParam);
    }

    public static bool PostMouseWheelAtScreenPoint(
        nint targetWindow,
        Point screenPoint,
        nint wParam,
        bool warmHover = true)
    {
        if (targetWindow == 0)
        {
            return false;
        }

        if (warmHover)
        {
            WarmMouseHover(targetWindow, screenPoint);
        }

        nint screenLParam = unchecked((nint)(int)(((screenPoint.Y & 0xFFFF) << 16) | (screenPoint.X & 0xFFFF)));
        return PostMessage(targetWindow, WM_MOUSEWHEEL, wParam, screenLParam);
    }

    public static bool SendMouseWheelInput(int delta)
    {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].u.mi = new MOUSEINPUT
        {
            dx = 0,
            dy = 0,
            mouseData = unchecked((uint)delta),
            dwFlags = MOUSEEVENTF_WHEEL,
            time = 0,
            dwExtraInfo = 0
        };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    public static bool TryExcludeWindowFromCapture(nint handle)
    {
        if (handle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        try
        {
            return SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            return false;
        }
    }

    public static (nint RootWindow, nint TargetChildWindow) FindTargetWindowAtPoint(
        Point screenPoint,
        IReadOnlySet<nint>? ignoredRootHandles = null)
    {
        // WindowFromPoint respects layered transparency holes (such as ScrollCaptureForm's selection hole)
        // and skips transparent desktop overlays before falling back to raw Z-order enumeration.
        nint hit = WindowFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y });
        if (hit != 0)
        {
            nint hitRoot = GetAncestor(hit, GA_ROOT);
            if (hitRoot == 0)
            {
                hitRoot = hit;
            }

            if (IsValidTargetTopWindow(hitRoot, screenPoint, ignoredRootHandles))
            {
                nint child = ResolveDeepestChildWindow(hitRoot, screenPoint);
                return (hitRoot, child == 0 ? hitRoot : child);
            }
        }

        nint hwnd = GetTopWindow(0);
        while (hwnd != 0)
        {
            if (IsValidTargetTopWindow(hwnd, screenPoint, ignoredRootHandles))
            {
                nint child = ResolveDeepestChildWindow(hwnd, screenPoint);
                return (hwnd, child == 0 ? hwnd : child);
            }

            hwnd = GetWindow(hwnd, GW_HWNDNEXT);
        }

        return (0, 0);
    }

    private static bool IsValidTargetTopWindow(
        nint hwnd,
        Point screenPoint,
        IReadOnlySet<nint>? ignoredRootHandles)
    {
        if (hwnd == 0 ||
            (ignoredRootHandles is not null && ignoredRootHandles.Contains(hwnd)) ||
            !IsWindowVisible(hwnd) ||
            IsIconic(hwnd) ||
            !IsWindowEnabled(hwnd) ||
            IsWindowCloaked(hwnd))
        {
            return false;
        }

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TRANSPARENT) != 0)
        {
            return false;
        }

        if ((exStyle & WS_EX_LAYERED) != 0 &&
            GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) &&
            (flags & LWA_ALPHA) != 0 &&
            alpha == 0)
        {
            return false;
        }

        if (!TryGetVisibleWindowBounds(hwnd, out Rectangle bounds) || !bounds.Contains(screenPoint))
        {
            return false;
        }

        nint hitPoint = unchecked((nint)(int)(((screenPoint.Y & 0xFFFF) << 16) | (screenPoint.X & 0xFFFF)));
        if (SendMessageTimeout(hwnd, WM_NCHITTEST, 0, hitPoint, SMTO_ABORTIFHUNG, 35, out nint hitResult) != 0 &&
            hitResult == HTTRANSPARENT)
        {
            return false;
        }

        return true;
    }

    public static nint ResolveDeepestChildWindow(nint rootWindow, Point screenPoint)
    {
        if (rootWindow == 0)
        {
            return 0;
        }

        nint current = rootWindow;
        for (int depth = 0; depth < 32; depth++)
        {
            var clientPoint = new POINT { X = screenPoint.X, Y = screenPoint.Y };
            if (!ScreenToClient(current, ref clientPoint))
            {
                break;
            }

            nint child = ChildWindowFromPointEx(
                current,
                clientPoint,
                CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
            if (child == 0 || child == current)
            {
                break;
            }

            current = child;
        }

        // Chromium, Edge, Electron, and WebView2 mark Chrome_RenderWidgetHostHWND with
        // WS_EX_TRANSPARENT so CWP_SKIPTRANSPARENT skips it. Inspect visible enabled children
        // containing the cursor point so wheel input reaches the actual web input host.
        nint renderHost = FindPreferredInputChildWindow(current, screenPoint);
        return renderHost != 0 ? renderHost : current;
    }

    private static nint FindPreferredInputChildWindow(nint parent, Point screenPoint)
    {
        nint preferredWebHost = 0;
        nint firstNonOverlayChild = 0;
        var className = new System.Text.StringBuilder(128);

        EnumChildWindows(parent, (child, _) =>
        {
            if (!IsWindowVisible(child) || !IsWindowEnabled(child))
            {
                return true;
            }

            if (!GetWindowRect(child, out RECT rect) || !rect.ToRectangle().Contains(screenPoint))
            {
                return true;
            }

            className.Clear();
            int len = GetClassName(child, className, className.Capacity);
            string cls = len > 0 ? className.ToString() : string.Empty;

            if (cls.Equals("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal))
            {
                preferredWebHost = child;
                return false;
            }

            if (firstNonOverlayChild == 0 &&
                !cls.Equals("Intermediate D3D Window", StringComparison.Ordinal))
            {
                firstNonOverlayChild = child;
            }

            return true;
        }, 0);

        return preferredWebHost != 0 ? preferredWebHost : firstNonOverlayChild;
    }

    private static bool IsWindowCloaked(nint handle)
    {
        try
        {
            return DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetVisibleWindowBounds(nint handle, out Rectangle bounds)
    {
        try
        {
            if (DwmGetWindowAttribute(handle, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>()) == 0)
            {
                Rectangle candidate = dwmRect.ToRectangle();
                if (candidate.Width > 0 && candidate.Height > 0)
                {
                    bounds = candidate;
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to GetWindowRect
        }

        if (GetWindowRect(handle, out RECT winRect))
        {
            Rectangle candidate = winRect.ToRectangle();
            if (candidate.Width > 0 && candidate.Height > 0)
            {
                bounds = candidate;
                return true;
            }
        }

        bounds = Rectangle.Empty;
        return false;
    }
}
