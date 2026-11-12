using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PotDanmaku.App.Player;

namespace PotDanmaku.App.Rendering;

/// <summary>Cancellable high-resolution pacing without busy-waiting on the GPU thread.</summary>
internal sealed class FrameWaiter : IDisposable
{
    private sealed class TimerHandle : WaitHandle
    { public void Initialize(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, true); }
    private readonly TimerHandle? timer;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(nint timer, ref long dueTime, int period, nint callback, nint argument, bool resume);
    public FrameWaiter()
    {
        nint handle = CreateWaitableTimerEx(0, null, 2, 0x001F0003);
        if (handle == 0) handle = CreateWaitableTimerEx(0, null, 0, 0x001F0003);
        if (handle != 0) { timer = new(); timer.Initialize(handle); }
    }
    public void WaitUntil(double deadline, CancellationToken cancellation)
    {
        double remaining = deadline - PotPlayerClient.Now;
        if (remaining <= 0) return;
        long due = -Math.Max(1, (long)(remaining * 10_000_000));
        if (timer != null && SetWaitableTimer(timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, 0, 0, false))
            WaitHandle.WaitAny([timer, cancellation.WaitHandle]);
        else cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(remaining));
    }
    public void Dispose() => timer?.Dispose();
}
