using System.Runtime.InteropServices;

namespace ZSnaper.Helpers;

/// <summary>
/// 全局鼠标滚轮悬停滚动过滤器：
/// 实现 Windows 现代平滑滚轮逻辑，鼠标悬停在任何输入框、滚动面板或编辑区上时，
/// 无需先点击聚焦即可直接平滑滚轮滚动。
/// </summary>
internal sealed class MouseWheelHoverFilter : IMessageFilter
{
    private const int WmMouseWheel = 0x020A;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point pt);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == WmMouseWheel)
        {
            Point cursor = Cursor.Position;
            IntPtr targetHwnd = WindowFromPoint(cursor);
            if (targetHwnd != IntPtr.Zero && targetHwnd != m.HWnd)
            {
                Control? target = Control.FromHandle(targetHwnd);
                if (target != null)
                {
                    SendMessage(targetHwnd, m.Msg, m.WParam, m.LParam);
                    return true;
                }
            }
        }

        return false;
    }
}

