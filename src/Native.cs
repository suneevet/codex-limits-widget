using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [StructLayout(LayoutKind.Sequential)] internal struct ComboBoxInfo {public int Size;public Rect Item,Button;public uint State;public IntPtr Combo,Edit,List;}
        [DllImport("user32.dll")] internal static extern bool GetComboBoxInfo(IntPtr combo,ref ComboBoxInfo info);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", ExactSpelling = true)] static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

        const int IdcHand = 32649;
        const int ErrorAccessDenied = 5;
        static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
        static Cursor systemHand;

        internal static Cursor SystemHand()
        {
            IntPtr handle = LoadCursorW(IntPtr.Zero, new IntPtr(IdcHand));
            if (handle == IntPtr.Zero) return Cursors.Hand;
            if (systemHand == null || systemHand.Handle != handle) systemHand = new Cursor(handle);
            return systemHand;
        }

        public static void EnableDpi()
        {
            try
            {
                // Windows can already have applied the manifest before Main runs.
                if (SetProcessDpiAwarenessContext(PerMonitorAwareV2) || Marshal.GetLastWin32Error() == ErrorAccessDenied)
                    return;
            }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }

        internal static uint? WindowDpi(IntPtr window)
        {
            try
            {
                uint dpi = GetDpiForWindow(window);
                return dpi == 0 ? (uint?)null : dpi;
            }
            catch (EntryPointNotFoundException) { return null; }
        }

        internal static bool? IsPerMonitorAwareV2(IntPtr window)
        {
            try
            {
                IntPtr context = GetWindowDpiAwarenessContext(window);
                return context == IntPtr.Zero ? (bool?)null : AreDpiAwarenessContextsEqual(context, PerMonitorAwareV2);
            }
            catch (EntryPointNotFoundException) { return null; }
        }
        public static float Dpi(IntPtr window)
        {
            return (WindowDpi(window) ?? 96) / 96f;
        }
        public static Rectangle GetPlacement(Rectangle bar, Rectangle notification, float scale, int offset, bool above, int logicalWidth = 388)
        {
            int width = (int)Math.Round(logicalWidth * scale);
            int height = (int)Math.Round(40 * scale);
            int margin = Math.Max(2, (int)Math.Round(4 * scale));
            int right = notification.Width > 0 ? notification.Left : bar.Right - (int)(180 * scale);
            int x = Math.Max(bar.Left + margin, Math.Min(bar.Right - width - margin, right - width - margin - (int)(offset * scale)));
            int y = above ? bar.Top - height - margin : bar.Top + (bar.Height - height) / 2;
            if (bar.Height > bar.Width)
            {
                // Vertical taskbars have insufficient width for the horizontal card.
                var screen = Screen.FromRectangle(bar).Bounds;
                x = bar.Left <= screen.Left ? bar.Right + margin : bar.Left - width - margin;
                y = Math.Max(bar.Top + margin, bar.Bottom - height - (int)(180 * scale));
            }
            return new Rectangle(x, y, width, height);
        }
        public static bool IsFullscreen(IntPtr ownWindow, IntPtr taskbar)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == ownWindow || foreground == taskbar) return false;
            // Desktop and taskbar should keep the card visible.
            if (foreground == FindWindow("Progman", null) || foreground == FindWindow("WorkerW", null)) return false;
            Rect rect;
            if (!GetWindowRect(foreground, out rect)) return false;
            Rectangle screen = Screen.FromHandle(taskbar).Bounds;
            return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        }
    }
}
