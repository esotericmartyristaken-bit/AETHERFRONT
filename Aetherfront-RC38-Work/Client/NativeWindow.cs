using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Aetherfront.Presentation
{
    // Own-process windows only. No focus changes, input injection or global hooks.
    public static class NativeWindow
    {
        public const int StyleIndex = -16;
        public const uint OverlappedWindow = 0x00CF0000;
        public const uint Popup = 0x80000000;
        public const uint NoActivateFrameChange = 0x0034; // NOZORDER | NOACTIVATE | FRAMECHANGED
        [StructLayout(LayoutKind.Sequential)]
        public struct Rectangle { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public Rectangle Monitor, Work; public uint Flags; }

        [DllImport("user32", SetLastError=true)] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32")] public static extern bool IsWindow(IntPtr window);
        [DllImport("user32", EntryPoint="GetWindowLongW")] public static extern int GetStyle(IntPtr window, int index);
        [DllImport("user32", EntryPoint="SetWindowLongW", SetLastError=true)] private static extern int SetStyle(IntPtr window, int index, int value);
        [DllImport("user32", SetLastError=true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32", EntryPoint="GetMonitorInfoW", SetLastError=true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr LoadImage(IntPtr instance, string path, uint kind, int width, int height, uint flags);
        [DllImport("user32", EntryPoint="SendMessageW")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, IntPtr value);
        [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool SetWindowText(IntPtr window, string title);
        [DllImport("user32")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32")] private static extern int GetSystemMetrics(int index);
        [DllImport("shell32", CharSet=CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string id);
        [DllImport("kernel32")] private static extern void SetLastError(uint code);
        private delegate bool EnumerateWindow(IntPtr window, IntPtr parameter);
        [DllImport("user32")] private static extern bool EnumWindows(EnumerateWindow callback, IntPtr parameter);
        [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("kernel32")] private static extern uint GetCurrentProcessId();

        // KSP's shipped Mono returns zero for Process.MainWindowHandle even
        // when the actual Unity window exists. Discover its native HWND by
        // process ownership and exact class; do not inspect or change focus.
        public static IntPtr FindOwnWindow(string className = "UnityWndClass")
        {
            IntPtr found = IntPtr.Zero;
            uint current = GetCurrentProcessId();
            EnumWindows((candidate, unused) =>
            {
                uint owner;
                if (GetWindowThreadProcessId(candidate, out owner) == 0 || owner != current) return true;
                var name = new StringBuilder(256);
                if (GetClassName(candidate, name, name.Capacity) == 0 || !String.Equals(name.ToString(), className, StringComparison.OrdinalIgnoreCase)) return true;
                found = candidate;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static void RequireOwnWindow(IntPtr window)
        {
            uint owner;
            if (window == IntPtr.Zero || !IsWindow(window) || GetWindowThreadProcessId(window, out owner) == 0 ||
                owner != GetCurrentProcessId())
                throw new InvalidOperationException("Presentation changes require this process's window");
        }

        public static uint BorderlessStyle(uint style) => (style & ~(OverlappedWindow | 0x01000000u)) | Popup;

        public static Rectangle MonitorBounds(IntPtr window)
        {
            RequireOwnWindow(window);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info.Monitor.Right <= info.Monitor.Left || info.Monitor.Bottom <= info.Monitor.Top)
                throw new InvalidOperationException("Invalid monitor bounds");
            return info.Monitor;
        }

        public static void Borderless(IntPtr window, Rectangle bounds)
        {
            RequireOwnWindow(window);
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) throw new ArgumentException("Invalid window bounds");
            SetLastError(0);
            int old = SetStyle(window, StyleIndex, unchecked((int)BorderlessStyle((uint)GetStyle(window, StyleIndex))));
            if (old == 0 && Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetWindowPos(window, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right-bounds.Left, bounds.Bottom-bounds.Top, NoActivateFrameChange))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static IntPtr LoadIcon(string path, bool small)
        {
            var result = LoadImage(IntPtr.Zero, path, 1, GetSystemMetrics(small ? 49 : 11), GetSystemMetrics(small ? 50 : 12), 0x10);
            if (result == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
    }
}
