using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using PotDanmaku.App.Player;
using PotDanmaku.Core;

namespace PotDanmaku.App.Rendering;

internal sealed record RenderScene(bool Visible, int Width, int Height, bool Preview,
    DanmakuLayout? Layout, double OffsetMs, float Opacity, bool PreserveColor,
    PlayerReading? Reading, long Generation, double PreviewStartMs = 0, int FrameLimit = 0);
internal sealed record RenderHealth(long Frames, double MediaMs, double P95Ms, string? Error,
    bool DxgiStatisticsAvailable = false, uint PresentCount = 0, uint PresentRefreshCount = 0, long SyncQpc = 0,
    int Active = 0, int Accepted = 0, int Crowded = 0, string? FirstId = null, double FirstX = 0,
    long ClockDiscontinuities = 0, double SampleAgeMs = 0, bool Recovering = false,
    int DeviceRebuilds = 0, string? LastRecoveryError = null);

internal sealed class RenderLoop : IDisposable
{
    private readonly Thread thread;
    private readonly CancellationTokenSource cancellation = new();
    private RenderScene scene = new(false, 960, 540, false, null, 0, 1, true, null, 0);
    private RenderHealth health = new(0, 0, 0, null);
    private readonly ConcurrentQueue<string> rows = new();
    private int rebuildRequested;
    public RenderHealth Health => Volatile.Read(ref health);
    public void Update(RenderScene value) => Volatile.Write(ref scene, value);
    public void RequestDeviceRebuild(bool simulateLoss = false) => Interlocked.Exchange(ref rebuildRequested, simulateLoss ? 2 : 1);

    public RenderLoop(nint hwnd)
    {
        thread = new Thread(() => Run(hwnd)) { IsBackground = true, Name = "PotDanmaku.GPU" }; thread.Start();
    }
    private void Run(nint hwnd)
    {
        GpuRenderer? renderer = null;
        try
        {
            using var waiter = new FrameWaiter();
            var clock = new MediaClock(); var intervals = new Queue<double>(); var active = new List<ScheduledItem>();
            long frame = 0, generation = -1;
            double origin = PotPlayerClient.Now, last = 0, lastDrawStart = 0; PlaybackSample? previousSample = null;
            int consecutiveFailures = 0, goodFrames = 0, rebuilds = 0;
            string? lastRecoveryError = null;
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                int rebuild = Interlocked.Exchange(ref rebuildRequested, 0);
                if (rebuild == 2) throw new COMException("Smoke test: simulated DXGI_ERROR_DEVICE_REMOVED", unchecked((int)0x887A0005));
                if (rebuild == 1 && renderer != null)
                {
                    renderer.Dispose(); renderer = null; rebuilds++; intervals.Clear(); last = lastDrawStart = 0;
                    Volatile.Write(ref health, Health with { Recovering = true, DeviceRebuilds = rebuilds, DxgiStatisticsAvailable = false });
                }
                if (renderer == null)
                {
                    renderer = new GpuRenderer(hwnd, 960, 540);
                    Volatile.Write(ref health, Health with { Recovering = false, DeviceRebuilds = rebuilds });
                }
                var current = Volatile.Read(ref scene);
                if (current.Visible && current.FrameLimit > 0 && lastDrawStart > 0)
                {
                    waiter.WaitUntil(lastDrawStart + 1.0 / current.FrameLimit, cancellation.Token);
                    if (cancellation.IsCancellationRequested) break;
                    current = Volatile.Read(ref scene);
                }
                double now = PotPlayerClient.Now;
                if (generation != current.Generation) { generation = current.Generation; clock.Reset(); previousSample = null; origin = now; }
                // Keep calibrating when the window is hidden, so returning from the background isn't a seek.
                if (current.Reading?.Sample is { } sample && sample != previousSample) { clock.Update(sample); previousSample = sample; }
                if (!current.Visible || current.Layout == null)
                {
                    cancellation.Token.WaitHandle.WaitOne(20); last = lastDrawStart = 0; continue;
                }
                double mediaMs = current.Preview ? current.PreviewStartMs + (now - origin) * 1000 : clock.Read(now);
                double commentMs = mediaMs - current.OffsetMs;
                var layout = current.Layout;
                layout.GetActive(commentMs, active);
                renderer.Resize(current.Width, current.Height);
                lastDrawStart = now;
                renderer.Draw(active, commentMs, layout.Options.FontSize, current.Opacity, layout.Options.Stroke, current.PreserveColor);
                bool hasStats = renderer.TryGetPresentationStatistics(out var dxgiStats);
                double after = PotPlayerClient.Now, interval = last == 0 ? 0 : (after - last) * 1000;
                last = after; frame++;
                if (++goodFrames >= 120) consecutiveFailures = 0;
                if (interval > 0) { intervals.Enqueue(interval); if (intervals.Count > 1000) intervals.Dequeue(); }
                var first = active.FirstOrDefault();
                float x = first?.X(commentMs) ?? 0;
                double age = current.Reading?.Sample is { } observed ? (now - observed.ObservedSeconds) * 1000 : 0;
                string state = current.Preview ? "Preview" : current.Reading?.Sample?.State.ToString() ?? "Unknown";
                double rate = current.Preview ? 1 : current.Reading?.Sample?.Rate ?? 0;
                if (rows.Count < 100_000)
                    rows.Enqueue(FormattableString.Invariant($"{after:F6},{mediaMs:F3},{commentMs:F3},{x:F3},{interval:F3},{(after-now)*1000:F3},{clock.Discontinuities},{generation},{hasStats},{dxgiStats.PresentCount},{dxgiStats.PresentRefreshCount},{dxgiStats.SyncQPCTime},{active.Count},{first?.Item.SourceIndex ?? -1},{state},{rate:F3},{age:F3},{first?.Item.Mode.ToString() ?? "None"},{x * current.Height / 1080:F3},{rebuilds},{current.FrameLimit}"));
                if (frame % 30 == 0)
                    Volatile.Write(ref health, new(frame, mediaMs, FrameStatistics.Summarize(intervals).P95IntervalMs, null,
                        hasStats, dxgiStats.PresentCount, dxgiStats.PresentRefreshCount, dxgiStats.SyncQPCTime,
                        active.Count, layout.Report.Accepted, layout.Report.Crowded, first?.Item.Id, x, clock.Discontinuities, age,
                        DeviceRebuilds: rebuilds, LastRecoveryError: lastRecoveryError));
                }
                catch (Exception ex) when (GraphicsRecovery.IsRecoverable(ex.HResult) && consecutiveFailures < GraphicsRecovery.MaxAttempts)
                {
                    renderer?.Dispose(); renderer = null; rebuilds++; consecutiveFailures++; goodFrames = 0;
                    lastRecoveryError = ex.Message; intervals.Clear(); last = lastDrawStart = 0;
                    Volatile.Write(ref health, Health with { Recovering = true, DeviceRebuilds = rebuilds,
                        LastRecoveryError = ex.Message, DxgiStatisticsAvailable = false });
                    AppLog.Write($"GPU 恢复 {consecutiveFailures}/{GraphicsRecovery.MaxAttempts}：0x{ex.HResult:X8} {ex.Message}");
                    cancellation.Token.WaitHandle.WaitOne(GraphicsRecovery.RetryDelayMs(consecutiveFailures));
                }
            }
        }
        catch (Exception ex) { Volatile.Write(ref health, Health with { Error = ex.ToString() }); }
        finally { renderer?.Dispose(); }
    }
    public void Export(string path) => File.WriteAllLines(path, new[] {
        "monotonic_s,media_ms,comment_ms,x_ref,submission_interval_ms,draw_and_present_ms,discontinuities,generation,dxgi_available,present_count,present_refresh_count,sync_qpc,active_count,first_source_index,clock_state,rate,sample_age_ms,first_mode,x_physical,device_rebuilds,frame_limit"
    }.Concat(rows.ToArray()));
    public void Dispose() { cancellation.Cancel(); thread.Join(); cancellation.Dispose(); }
}
