using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    internal sealed class IslandNative
    {
        internal const int LayeredStyle = 0x00080000;
        internal const int NoActivateStyle = 0x08000000;
        const uint NoActivate = 0x0010, NoSize = 0x0001, NoMove = 0x0002;
        static readonly IntPtr Topmost = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        struct BlendFunction { public byte Operation, Flags, Opacity, AlphaFormat; }
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr target, ref Point position, ref Size size,
            IntPtr source, ref Point origin, uint colorKey, ref BlendFunction blend, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Native.Rect value, int size);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int length);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        static readonly HashSet<string> FullscreenDesktopApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe",
            "vivaldi.exe", "browser.exe", "waterfox.exe", "floorp.exe", "librewolf.exe",
            "vlc.exe", "mpv.exe", "mpc-hc.exe", "mpc-hc64.exe", "mpc-be.exe", "mpc-be64.exe",
            "PotPlayerMini.exe", "PotPlayerMini64.exe", "PowerPoint.exe", "Acrobat.exe", "AcroRd32.exe"
        };

        uint lastProcess;
        string foregroundPath;
        string lastAppName;
        bool knownGame;
        bool lastSuppressed;
        internal string LastAppName { get { return lastAppName; } }

        internal static bool Present(IntPtr window, Point position, Bitmap bitmap)
        {
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            IntPtr handle = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                handle = bitmap.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                previous = SelectObject(dc, handle);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return false;
                var size = bitmap.Size;
                var origin = Point.Empty;
                var blend = new BlendFunction { Opacity = 255, AlphaFormat = 1 };
                return UpdateLayeredWindow(window, IntPtr.Zero, ref position, ref size, dc, ref origin, 0, ref blend, 2);
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                if (handle != IntPtr.Zero) DeleteObject(handle);
                DeleteDC(dc);
            }
        }

        internal static void KeepTopmost(IntPtr window)
        {
            Native.SetWindowPos(window, Topmost, 0, 0, 0, 0, NoActivate | NoSize | NoMove);
        }

        internal bool ShouldHide(string[] excluded)
        {
            int state;
            const int notPresent = 1, direct3DFullscreen = 3;
            if (SHQueryUserNotificationState(out state) == 0 && (state == notPresent || state == direct3DFullscreen))
                return true;
            IntPtr foreground = Native.GetForegroundWindow();
            uint pid;
            GetWindowThreadProcessId(foreground, out pid);
            if (pid == GetCurrentProcessId()) return lastSuppressed;
            if (pid == 0) return false;

            var className = new StringBuilder(128);
            GetClassName(foreground, className, className.Capacity);
            string kind = className.ToString();
            if (kind == "Progman" || kind == "WorkerW" || kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd")
                return lastSuppressed = false;

            if (pid != lastProcess)
            {
                lastProcess = pid;
                foregroundPath = ProcessPath(pid);
                knownGame = IsGamePath(foregroundPath);
            }
            string name = string.IsNullOrEmpty(foregroundPath) ? null : Path.GetFileName(foregroundPath);
            if (name != null && !string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase)) lastAppName = name;
            bool manual = name != null && Array.Exists(excluded ?? new string[0], item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            return lastSuppressed = manual || knownGame || (IsFullscreenGameCandidate(name) && IsFullscreen(foreground));
        }

        internal static bool IsFullscreenGameCandidate(string executable)
        {
            return !FullscreenDesktopApps.Contains(executable);
        }

        static bool IsFullscreen(IntPtr window)
        {
            const int styleIndex = -16, caption = 0x00C00000, extendedFrame = 9;
            if (IsZoomed(window) && (GetWindowStyle(window, styleIndex) & caption) == caption) return false;
            Native.Rect bounds;
            if (DwmGetWindowAttribute(window, extendedFrame, out bounds, Marshal.SizeOf(typeof(Native.Rect))) != 0 &&
                !Native.GetWindowRect(window, out bounds)) return false;
            Rectangle screen = Screen.FromHandle(window).Bounds;
            return CoversMonitor(bounds.Rectangle, screen);
        }

        internal static bool CoversMonitor(Rectangle window, Rectangle monitor)
        {
            return window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 &&
                window.Right >= monitor.Right - 2 && window.Bottom >= monitor.Bottom - 2;
        }

        static string ProcessPath(uint pid)
        {
            const uint queryLimitedInformation = 0x1000;
            IntPtr process = OpenProcess(queryLimitedInformation, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int length = buffer.Capacity;
                return QueryFullProcessImageName(process, 0, buffer, ref length) ? buffer.ToString() : null;
            }
            finally { CloseHandle(process); }
        }

        internal static bool IsGamePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(@"\XboxGames\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return false;
            if (File.Exists(Path.Combine(directory, "UnityPlayer.dll"))) return true;
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(directory); i++)
            {
                if (Directory.Exists(Path.Combine(directory, ".egstore"))) return true;
                directory = Path.GetDirectoryName(directory);
            }
            return false;
        }
    }
}
