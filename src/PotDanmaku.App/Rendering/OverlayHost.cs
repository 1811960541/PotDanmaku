using System.Runtime.InteropServices;
using PotDanmaku.App.Interop;

namespace PotDanmaku.App.Rendering;

internal sealed class OverlayHost : IDisposable
{
    private static readonly Native.WindowProc Procedure = WindowProc;
    private static ushort atom;
    public nint Handle { get; }
    public bool Visible { get; private set; }
    public OverlayHost()
    {
        var instance = Native.GetModuleHandle(null);
        if (atom == 0)
        {
            var definition = new Native.WindowClass { Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Procedure = Procedure, Instance = instance, ClassName = "PotDanmaku.NativeOverlay" };
            atom = Native.RegisterClassEx(ref definition);
            if (atom == 0) throw new System.ComponentModel.Win32Exception();
        }
        Handle = Native.CreateWindowEx(0x08000000 | 0x00000080 | 0x00000020 | 0x00080000 | 0x00200000,
            "PotDanmaku.NativeOverlay", "PotDanmaku.Overlay", 0x80000000,
            0, 0, 960, 540, 0, 0, instance, 0);
        if (Handle == 0) throw new System.ComponentModel.Win32Exception();
        if (!Native.SetLayeredWindowAttributes(Handle, 0, 255, 2))
        {
            int error = Marshal.GetLastWin32Error(); Native.DestroyWindow(Handle);
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == 0x0084) return -1;
        if (msg == 0x0021) return 3;
        if (msg == 0x0014) return 1;
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
    internal static bool IsTopmost(nint hwnd) => (Native.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x00000008) != 0;

    public bool Move(Native.Rect rect, nint target)
    {
        if (!Native.IsWindow(target) || !Native.IsWindowVisible(target) || Native.IsIconic(target) || Native.IsCloaked(target))
        { Hide(); return false; }

        // Match only the player's topmost band. Never change the player's own Z order.
        // In particular HWND_TOP would not demote an already-topmost overlay.
        bool topmost = IsTopmost(target);
        if (IsTopmost(Handle) != topmost && !Native.SetWindowPos(Handle, topmost ? -1 : -2,
            0, 0, 0, 0, 0x0010 | 0x0200 | 0x0001 | 0x0002))
        { Hide(); return false; }

        // Insert immediately above the target, below windows that already cover it.
        // This keeps an inactive visible video readable without drawing on work apps.
        // The overlay remains unowned: a player closing must not destroy its GPU HWND.
        nint above = Native.GetWindow(target, 3);
        if (above == Handle) above = Native.GetWindow(above, 3);
        if (above == 0 || !topmost && IsTopmost(above)) above = topmost ? -1 : 0;
        Visible = Native.SetWindowPos(Handle, above, rect.Left, rect.Top,
            Math.Max(1, rect.Width), Math.Max(1, rect.Height), 0x0010 | 0x0200 | 0x0040);
        if (!Visible) Native.ShowWindow(Handle, 0);
        return Visible;
    }
    public void Hide() { if (Visible) Native.ShowWindow(Handle, 0); Visible = false; }
    public void Dispose() => Native.DestroyWindow(Handle);
}
