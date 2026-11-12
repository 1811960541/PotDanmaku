using System.Diagnostics;
using System.IO;
using System.Text;
using PotDanmaku.App;

internal static class RealPlayerCase
{
    internal static async Task Run(string root, string executable, List<string> passed)
    {
        if (Process.GetProcessesByName("PotPlayerMini64").Length != 0) throw new InvalidOperationException("An existing player is open; isolated real-player test skipped.");
        string first = Path.Combine(root, "自动切集 01.avi"), second = Path.Combine(root, "自动切集 02.avi");
        MakeVideo(first, 6, 0x50); MakeVideo(second, 6, 0x90);
        File.WriteAllText(Path.ChangeExtension(first, ".xml"), "<i><d p=\"0,1,25,16777215,0,0,0,0\">first episode</d></i>");
        File.WriteAllText(Path.ChangeExtension(second, ".json"), "{\"schema\":\"potdanmaku/v1\",\"timeUnit\":\"ms\",\"comments\":[{\"time\":0,\"text\":\"second episode\"}]}");
        string playlist = Path.Combine(root, "auto-next.m3u8"); File.WriteAllText(playlist, "#EXTM3U\n" + first + "\n" + second + "\n", new UTF8Encoding(true));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(playlist);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch isolated player.");
        var window = new MainWindow(false, autoDiscover: true); window.Show();
        try
        {
            await Wait(() => window.VideoSession.Video == first && window.VideoSession.Ready && !window.VideoSession.Loading);
            passed.Add("real PotPlayer automatic discovery and UTF-8 full-path XML match");
            window.OffsetText.Text = "-0.5"; window.FontSizeControl.Value = 24;
            await Wait(() => window.VideoSession.Video == second && window.VideoSession.Ready && !window.VideoSession.Loading);
            if (window.VideoSession.Danmaku != Path.ChangeExtension(second, ".json") || window.VideoSession.Offset != 0)
                throw new InvalidOperationException("Real next episode retained old comments or offset.");
            passed.Add("real PotPlayer playlist advances and automatically loads next JSON");
            window.ExportDiagnostics(Path.Combine(root, "real-player-diagnostics"));
        }
        finally { window.Close(); if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Real PotPlayer media identity or next episode was not observed."); await Task.Delay(20); }
    }
    internal static void MakeVideo(string path, int seconds, byte shade)
    {
        const int width = 160, height = 90, fps = 25, frameSize = width * height * 3;
        int frames = seconds * fps;
        using var file = File.Create(path); using var writer = new BinaryWriter(file);
        void Four(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        long Begin(string id) { Four(id); writer.Write(0); return file.Position; }
        void End(long start) { long end = file.Position; file.Position = start - 4; writer.Write((int)(end - start)); file.Position = end; if (((end - start) & 1) != 0) writer.Write((byte)0); }
        long riff = Begin("RIFF"); Four("AVI ");
        long headers = Begin("LIST"); Four("hdrl");
        long avih = Begin("avih");
        foreach (int value in new[] { 1000000 / fps, frameSize * fps, 0, 16, frames, 0, 1, frameSize, width, height, 0, 0, 0, 0 }) writer.Write(value);
        End(avih);
        long stream = Begin("LIST"); Four("strl");
        long strh = Begin("strh"); Four("vids"); Four("DIB "); writer.Write(0); writer.Write((short)0); writer.Write((short)0);
        foreach (int value in new[] { 0, 1, fps, 0, frames, frameSize, -1, 0 }) writer.Write(value);
        writer.Write((short)0); writer.Write((short)0); writer.Write((short)width); writer.Write((short)height); End(strh);
        long strf = Begin("strf"); writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((short)1); writer.Write((short)24);
        foreach (int value in new[] { 0, frameSize, 0, 0, 0, 0 }) writer.Write(value);
        End(strf); End(stream); End(headers);
        long movie = Begin("LIST"); Four("movi");
        byte[] pixels = new byte[frameSize]; Array.Fill(pixels, shade);
        for (int i = 0; i < frames; i++) { long frame = Begin("00db"); writer.Write(pixels); End(frame); }
        End(movie);
        long index = Begin("idx1");
        for (int i = 0; i < frames; i++) { Four("00db"); writer.Write(16); writer.Write(4 + i * (frameSize + 8)); writer.Write(frameSize); }
        End(index); End(riff);
    }
}
