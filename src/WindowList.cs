using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Kadr
{
    /// <summary>Visible windows, topmost first, so a click on the overlay can take one window exactly.</summary>
    static class WindowList
    {
        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int max);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

        /// <summary>Window rectangles in overlay coordinates (relative to the virtual screen), topmost first.</summary>
        public static List<Rectangle> Get(Rectangle virtualScreen)
        {
            var list = new List<Rectangle>();
            uint me = (uint)Process.GetCurrentProcess().Id;
            var bounds = new Rectangle(0, 0, virtualScreen.Width, virtualScreen.Height);
            var cls = new StringBuilder(64);
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h) || IsIconic(h)) return true;
                    if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true;   // click-through overlays
                    int cloaked;
                    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true; // hidden store apps, other desktops
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == me) return true;
                    cls.Clear();
                    GetClassName(h, cls, cls.Capacity);
                    string c = cls.ToString();
                    if (c == "Progman" || c == "WorkerW") return true;   // the desktop: a click there takes the monitor
                    RECT r;
                    if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, 16) != 0 && !GetWindowRect(h, out r)) return true;
                    var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    rect.Offset(-virtualScreen.X, -virtualScreen.Y);
                    rect.Intersect(bounds);
                    if (rect.Width < 8 || rect.Height < 8) return true;
                    list.Add(rect);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }
    }
}
