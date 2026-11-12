using System.Diagnostics;
using System.Runtime.InteropServices;
using PotDanmaku.App.Interop;
using PotDanmaku.Core;

namespace PotDanmaku.App.Player;

public sealed record PlayerTarget(nint Handle, int ProcessId, string Title)
{
    public override string ToString() => $"{Title}  (PID {ProcessId})";
}
public sealed record PlayerReading(PlaybackSample? Sample, string Status, string Title,
    int RawState, int RawSpeed, double RequestMs, int Error, double VideoAspect = 0, string RateSource = "player");

public sealed class PotPlayerClient
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public static IReadOnlyList<PlayerTarget> FindPlayers()
    {
        var results = new List<PlayerTarget>();
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!Native.IsWindowVisible(hwnd) || !Native.Class(hwnd).StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName.StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase))
                    results.Add(new(hwnd, (int)pid, Native.Title(hwnd)));
            }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return results;
    }

    public PlayerTarget Target { get; }
    public PotPlayerClient(PlayerTarget target) => Target = target;

    private bool Query(uint command, out long value, out int error)
    {
        // Only scalar, read-only community commands. No remote pointers or injection.
        Marshal.SetLastPInvokeError(0);
        var ok = Native.SendMessageTimeout(Target.Handle, 0x400, command, 0, 0x22, 20, out var raw);
        error = ok == 0 ? Marshal.GetLastWin32Error() : 0;
        value = (long)raw;
        return ok != 0;
    }

    public PlayerReading Read(double? manualRate = null)
    {
        double start = Now;
        if (!Native.IsWindow(Target.Handle)) return new(null, "播放器已关闭", "", -1, 0, 0, 0);
        Native.GetWindowThreadProcessId(Target.Handle, out uint pid);
        if (pid != Target.ProcessId) return new(null, "窗口已属于其他进程", "", -1, 0, 0, 0);
        string title = Native.Title(Target.Handle);
        if (!Query(0x5004, out long position, out int error))
            return new(null, "读取进度超时或被拒绝", title, -1, 0, (Now - start) * 1000, error);
        double observed = (start + Now) / 2;
        if (!Query(0x5006, out long state, out error))
            return new(null, "读取播放状态失败", title, -1, 0, (Now - start) * 1000, error);
        bool hasSpeed = Query(0x5015, out long speed, out _);
        bool manual = manualRate is { } supplied && double.IsFinite(supplied) && supplied is >= 0.2 and <= 12;
        if (!manual && (!hasSpeed || speed < 200 || speed > 12000))
            return new(null, "该版本未返回有效倍速，暂不启用自动同步", title, (int)state, (int)speed, (Now - start) * 1000, 0);
        if (state is < 0 or > 2 || position < 0)
            return new(null, "播放器返回了未知状态", title, (int)state, (int)speed, (Now - start) * 1000, 0);
        // Optional metadata is deliberately queried only at connection time elsewhere.
        var sample = new PlaybackSample(position, observed, (PlaybackState)state, manual ? manualRate!.Value : speed / 1000.0);
        return new(sample, (state switch { 0 => "停止", 1 => "暂停", _ => "播放中" }) + (manual ? " · 手动倍速" : ""), title,
            (int)state, (int)speed, (Now - start) * 1000, 0, RateSource: manual ? "manual" : "player");
    }

    public double ReadAspect()
    {
        return Query(0x6030, out long width, out _) && Query(0x6031, out long height, out _) && width > 0 && height > 0
            ? width / (double)height : 0;
    }
}
