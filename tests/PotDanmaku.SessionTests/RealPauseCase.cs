using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PotDanmaku.App;
using PotDanmaku.App.Interop;
using PotDanmaku.App.Player;
using PotDanmaku.Core;

internal static class RealPauseCase
{
    internal static async Task Run(string root, string executable, List<string> passed)
    {
        string video = Path.Combine(root, "暂停恢复 1x 3x.avi");
        RealPlayerCase.MakeVideo(video, 30, 0x60);
        File.WriteAllText(Path.ChangeExtension(video, ".xml"), "<i><d p=\"0,1,25,16777215,0,0,0,0\">pause continuity</d></i>");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(video); start.ArgumentList.Add("/new"); start.ArgumentList.Add("/volume=0");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch isolated player.");
        MainWindow? window = null;
        Task? sampling = null;
        using var cancellation = new CancellationTokenSource();
        var observations = new List<PlaybackSample>();
        var phases = new List<object>();
        try
        {
            PlayerTarget? target = null;
            await Wait(() => (target = PotPlayerClient.FindPlayers().FirstOrDefault(p => p.ProcessId == process.Id)) != null);
            var client = new PotPlayerClient(target!);
            void Command(uint command, int argument)
            {
                Native.GetWindowThreadProcessId(target!.Handle, out uint pid);
                if (process.HasExited || pid != process.Id ||
                    Native.SendMessageTimeout(target.Handle, 0x400, command, argument, 0x22, 100, out _) == 0)
                    throw new InvalidOperationException("Isolated player command failed.");
            }
            sampling = Task.Run(async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (client.Read().Sample is { } sample) observations.Add(sample);
                    await Task.Delay(20, cancellation.Token);
                }
            });
            window = new MainWindow(false); window.Show(); await window.Connect(target!);
            await Wait(() => window.VideoSession.Video == video && window.VideoSession.Ready && !window.VideoSession.Loading);
            window.DurationControl.Value = 40; window.OffsetText.Text = "0";
            await Wait(() => window.SmokePassed);
            double begin = PotPlayerClient.Now;
            foreach (int speed in new[] { 1000, 3000 })
            {
                Command(0x5016, speed);
                await Wait(() => client.Read().Sample?.Rate == speed / 1000.0);
                foreach (int delay in new[] { 120, 330, 470 })
                {
                    await Task.Delay(delay);
                    double pauseSent = PotPlayerClient.Now;
                    Command(0x5007, 1);
                    await Wait(() => client.Read().Sample?.State == PlaybackState.Paused);
                    await Task.Delay(800);
                    double resumeSent = PotPlayerClient.Now;
                    Command(0x5007, 2);
                    await Wait(() => client.Read().Sample?.State == PlaybackState.Playing);
                    await Task.Delay(250);
                    phases.Add(new { Rate = speed / 1000.0, PauseSent = pauseSent, ResumeSent = resumeSent });
                }
            }
            double end = PotPlayerClient.Now;
            string folder = window.ExportDiagnostics(Path.Combine(root, "pause-diagnostics"));
            var frames = File.ReadLines(Path.Combine(folder, "frames.csv")).Skip(1).Select(line => line.Split(','))
                .Where(row => Number(row[0]) >= begin && Number(row[0]) <= end).ToArray();
            int pauseEdges = 0, pausedPairs = 0, backward = 0, pauseJumps = 0, resumedJumps = 0;
            for (int i = 1; i < frames.Length; i++)
            {
                var a = frames[i - 1]; var b = frames[i];
                if (a[7] != b[7]) throw new InvalidOperationException("Clock generation changed during pause test.");
                double step = Number(b[1]) - Number(a[1]);
                if (step < -0.001) backward++;
                if (a[14] == "Playing" && b[14] == "Paused") { pauseEdges++; if (Math.Abs(step) > 0.001) pauseJumps++; }
                if (a[14] == "Paused" && b[14] == "Paused")
                {
                    pausedPairs++;
                    if (Math.Abs(step) > 0.001 || a[3] != b[3] || a[12] != b[12] || a[13] != b[13]) pauseJumps++;
                }
                if (a[14] == "Paused" && b[14] == "Playing")
                {
                    double elapsed = (Number(b[0]) - Number(a[0])) * 1000 + Number(a[5]) - Number(b[5]);
                    if (step > elapsed * Number(b[15]) + 5) resumedJumps++;
                }
            }
            using var windows = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "window-samples.json")));
            int hidden = windows.RootElement.EnumerateArray().Count(row => row.GetProperty("MonotonicSeconds").GetDouble() >= begin &&
                row.GetProperty("MonotonicSeconds").GetDouble() <= end && !row.GetProperty("OverlayVisible").GetBoolean());
            var metrics = new { Frames = frames.Length, PauseEdges = pauseEdges, PausedPairs = pausedPairs,
                BackwardSteps = backward, PauseJumps = pauseJumps, ResumeJumps = resumedJumps, HiddenSamples = hidden };
            Write(Path.Combine(root, "pause-metrics.json"), metrics);
            if (pauseEdges != 6 || pausedPairs < 60 || backward != 0 || pauseJumps != 0 || resumedJumps != 0 || hidden != 0)
                throw new InvalidOperationException("Pause continuity failed: " + JsonSerializer.Serialize(metrics));
            passed.Add("real PotPlayer 1x and 3x: six pauses preserve the last frame, remain frozen, and resume continuously");
            passed.Add("real PotPlayer overlay remains visible across pause and resume");
        }
        finally
        {
            cancellation.Cancel();
            if (sampling != null) try { await sampling; } catch (OperationCanceledException) { }
            Write(Path.Combine(root, "pause-player-samples.json"), observations);
            Write(Path.Combine(root, "pause-phases.json"), phases);
            window?.Close();
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); }
        }
    }
    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Isolated pause test timed out."); await Task.Delay(20); }
    }
}
