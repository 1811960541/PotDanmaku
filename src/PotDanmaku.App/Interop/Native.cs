using System.Runtime.InteropServices;
using System.Text;

namespace PotDanmaku.App.Interop;

internal static class Native
{
    internal delegate nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out uint value, uint size);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeoutMs, out nuint result);

    internal static string Title(nint hwnd) { var b = new StringBuilder(2048); GetWindowText(hwnd, b, b.Capacity); return b.ToString(); }
    internal static string Class(nint hwnd) { var b = new StringBuilder(256); GetClassName(hwnd, b, b.Capacity); return b.ToString(); }
    internal static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, 14, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0;
}
