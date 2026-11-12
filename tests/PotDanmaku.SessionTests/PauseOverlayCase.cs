using System.IO;
using System.Text.Json;
using PotDanmaku.App;
using PotDanmaku.App.Interop;
using PotDanmaku.App.Player;
using PotDanmaku.Core;

internal static class PauseOverlayCase
{
    internal static async Task Run(string root, List<string> passed)
    {
        string video = Path.Combine(root, "暂停测试.mkv");
        File.WriteAllText(Path.ChangeExtension(video, ".xml"), "<i><d p=\"0,1,25,16777215,0,0,0,0\">pause overlay</d></i>");
        using var player = new FakePlayer(video, visible: true);
        var window = new MainWindow(false);
        window.Show();
        try
        {
            await window.Connect(player.Target);
            await Wait(() => window.SmokePassed, "initial drawing");
            nint overlay = FindOverlay();
            if (overlay == 0) throw new InvalidOperationException("Test overlay window is missing.");
            int replies = player.PathReplies;
            await Wait(() => player.PathReplies > replies, "current video identity");

            double titleBegin = PotPlayerClient.Now;
            player.ReplyToPath = false;
            player.ChangeTitle("same video · paused");
            player.State = PlaybackState.Paused;
            await Task.Delay(180);
            player.ReplyToPath = true;
            await Task.Delay(350);
            double titleEnd = PotPlayerClient.Now;

            player.State = PlaybackState.Playing;
            await Task.Delay(180);
            double dropoutBegin = PotPlayerClient.Now;
            player.State = PlaybackState.Paused;
            player.Speed = 0; // Paused state arrived, but the first speed query is unusable.
            await Task.Delay(120);
            player.Speed = 3000;
            await Task.Delay(250);
            double dropoutEnd = PotPlayerClient.Now;

            string diagnostics = window.ExportDiagnostics(Path.Combine(root, "overlay-diagnostics"));
            using var samples = JsonDocument.Parse(File.ReadAllText(Path.Combine(diagnostics, "window-samples.json")));
            int HiddenBetween(double begin, double end) => samples.RootElement.EnumerateArray().Count(row =>
                row.GetProperty("MonotonicSeconds").GetDouble() >= begin && row.GetProperty("MonotonicSeconds").GetDouble() <= end &&
                !row.GetProperty("OverlayVisible").GetBoolean());
            int titleHides = HiddenBetween(titleBegin, titleEnd), dropoutHides = HiddenBetween(dropoutBegin, dropoutEnd);
            if (titleHides != 0 || dropoutHides != 0)
                throw new InvalidOperationException($"Pause overlay blink: title-only hides={titleHides}, temporary sample-loss hides={dropoutHides}.");
            passed.Add("paused title change keeps the overlay visible while the same media is confirmed");
            passed.Add("transient pause query failure keeps the overlay visible");

            player.Speed = 0;
            await Wait(() => !Native.IsWindowVisible(overlay), "sustained sample loss hides the overlay");
            player.Speed = 3000;
            await Wait(() => Native.IsWindowVisible(overlay), "valid samples restore the overlay");
            passed.Add("sustained sample loss hides until valid progress returns");

            player.ReplyToPath = false;
            player.ChangeTitle("unconfirmed media");
            await Wait(() => !Native.IsWindowVisible(overlay), "unconfirmed title change eventually hides the overlay");
            player.ReplyToPath = true;
            await Wait(() => Native.IsWindowVisible(overlay), "confirmed same media restores the overlay");
            passed.Add("title change has a bounded wait for media confirmation");

            Native.ShowWindow(player.Target.Handle, 6); // Minimize only the fake player.
            await Wait(() => !Native.IsWindowVisible(overlay), "minimized player hides the overlay");
            Native.ShowWindow(player.Target.Handle, 9);
            await Wait(() => Native.IsWindowVisible(overlay), "restored player restores the overlay");
            passed.Add("minimizing the player still hides its overlay");

            player.MediaPath = Path.Combine(root, "未匹配的下一集.mkv");
            await Wait(() => window.VideoSession.Video == player.MediaPath && !window.VideoSession.Loading && !Native.IsWindowVisible(overlay), "real media change clears old comments");
            passed.Add("a confirmed media change clears the previous episode overlay");
        }
        finally { window.Close(); }
    }

    private static nint FindOverlay()
    {
        nint overlay = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != Environment.ProcessId || Native.Class(hwnd) != "PotDanmaku.NativeOverlay") return true;
            overlay = hwnd;
            return false;
        }, 0);
        return overlay;
    }

    private static async Task Wait(Func<bool> condition, string stage)
    {
        double deadline = PotPlayerClient.Now + 5;
        while (!condition())
        {
            if (PotPlayerClient.Now >= deadline) throw new TimeoutException(stage);
            await Task.Delay(20);
        }
    }
}
