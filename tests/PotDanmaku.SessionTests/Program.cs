using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PotDanmaku.App;
using PotDanmaku.App.Interop;
using PotDanmaku.App.Player;
using PotDanmaku.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int exit = 1;
        string root = Path.Combine(AppContext.BaseDirectory, "session-cases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppPaths.Configure(true); // Isolated test executable directory; never changes the user's normal configuration.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["AppIcon"] = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/PotDanmaku.App;component/Assets/PotDanmaku.ico"));
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            var passed = new List<string>();
            try
            {
                if (args is ["--real-player", var executable])
                {
                    await RealPlayerCase.Run(root, executable, passed); exit = 0; return;
                }
                if (args is ["--real-pause", var pauseExecutable])
                {
                    await RealPauseCase.Run(root, pauseExecutable, passed); exit = 0; return;
                }
                if (args is ["--pause-overlay"])
                {
                    await PauseOverlayCase.Run(root, passed); exit = 0; return;
                }
                string a = Video(root, "Season 1/第01集.mkv"), b = Video(root, "Season 2/第01集.mkv"), c = Video(root, "第03集.mkv");
                string ax = Path.ChangeExtension(a, ".xml"), bj = Path.ChangeExtension(b, ".json"), custom = Path.Combine(root, "手动关联.xml");
                File.WriteAllText(ax, Xml("A")); File.WriteAllText(custom, Xml("custom"));
                File.WriteAllText(bj, "{\"schema\":\"potdanmaku/v1\",\"timeUnit\":\"ms\",\"comments\":[{\"time\":0,\"text\":\"B\",\"mode\":\"scroll\"}]}");
                using var player = new FakePlayer(a);
                window = new MainWindow(false); window.Show();
                await window.Connect(player.Target);
                await Wait(() => Ready(window, a));
                Check(window.VideoSession.Danmaku == ax, "initial automatic XML match", passed);
                Check(await window.ImportFile(custom), "manual association import", passed);
                window.FontSizeControl.Value = 31; window.OpacityControl.Value = 65; window.OffsetText.Text = "-1.75";
                player.MediaPath = b;
                await Wait(() => Ready(window, b));
                Check(window.VideoSession.Danmaku == bj && window.VideoSession.Offset == 0, "same-title episode switch and offset reset", passed);
                window.FontSizeControl.Value = 22; window.OffsetText.Text = "2.5";
                player.MediaPath = a; await Wait(() => Ready(window, a));
                Check(window.VideoSession.Danmaku == custom && window.VideoSession.Offset == -1.75 && window.VideoSession.Settings.FontSize == 31 && window.VideoSession.Settings.Opacity == 65,
                    "return restores custom association and preferences", passed);
                player.MediaPath = c;
                await Wait(() => window.VideoSession.Video == c && !window.VideoSession.Loading);
                Check(!window.VideoSession.Ready && window.VideoSession.Danmaku == "", "missing sibling clears old comments", passed);
                Check(await window.ImportFile(custom), "manual association for unmatched video", passed);
                window.OffsetText.Text = "7.25";
                // Start a deliberately large import, then switch episodes while parsing/layout are in flight.
                string large = Path.Combine(root, "large.xml");
                using (var output = new StreamWriter(large))
                {
                    output.Write("<i>");
                    for (int i = 0; i < 400000; i++) output.Write($"<d p=\"{i},1,25,16777215,0,0,0,0\">race {i}</d>");
                    output.Write("</i>");
                }
                var import = window.ImportFile(large); player.MediaPath = b;
                await Wait(() => Ready(window, b)); await import; await Task.Delay(350);
                Check(window.VideoSession.Video == b && window.VideoSession.Danmaku == bj && window.VideoSession.Offset == 2.5,
                    "late import cannot overwrite next episode", passed);
                window.Close(); window = new MainWindow(false); window.Show();
                player.MediaPath = a; await window.Connect(player.Target); await Wait(() => Ready(window, a));
                Check(window.VideoSession.Danmaku == custom && window.VideoSession.Offset == -1.75 && window.VideoSession.Settings.FontSize == 31,
                    "new window reloads persisted associations", passed);
                player.MediaPath = c; await Wait(() => Ready(window, c));
                Check(window.VideoSession.Offset == 7.25, "pending import does not lose prior saved offset", passed);
                player.MediaPath = "https://example.com/stream";
                await Wait(() => window.VideoSession.Video == null && !window.VideoSession.Loading);
                Check(!window.VideoSession.Ready, "unsupported source clears previous local comments", passed);
                Check(await window.ImportFile(custom), "manual mode works for unsupported source", passed);
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                window?.Close();
                var assemblies = new[] { typeof(MainWindow).Assembly, typeof(PotDanmaku.Core.MediaClock).Assembly, typeof(PotDanmaku.Formats.DanmakuImporter).Assembly }
                    .Select(assembly => new { Name = assembly.GetName().Name, assembly.Location,
                        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))) });
                string result = JsonSerializer.Serialize(new { Passed = exit == 0, Cases = passed, Root = root,
                    Assemblies = assemblies, RuntimeAssembly = typeof(object).Assembly.Location }, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(result); File.WriteAllText(Path.Combine(root, "verification.json"), result);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run(); return exit;
    }
    private static string Xml(string text) => $"<i><d p=\"0,1,25,16777215,0,0,0,0\">{text}</d></i>";
    private static string Video(string root, string relative) { string path = Path.GetFullPath(Path.Combine(root, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path; }
    private static bool Ready(MainWindow window, string path) => window.VideoSession.Video == path && window.VideoSession.Ready && !window.VideoSession.Loading;
    private static void Check(bool condition, string name, List<string> passed) { if (!condition) throw new InvalidOperationException(name); passed.Add(name); }
    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Video transition timed out."); await Task.Delay(25); }
    }
}

internal sealed class FakePlayer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct CopyData { public nuint Command; public int Length; public nint Data; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint message, nint sender, ref CopyData payload);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint hwnd, string title);
    private readonly HwndSource source;
    public string MediaPath { get; set; }
    public PlaybackState State { get; set; } = PlaybackState.Playing;
    public int Speed { get; set; } = 3000;
    public bool ReplyToPath { get; set; } = true;
    public int PathReplies { get; private set; }
    public PlayerTarget Target => new(source.Handle, Environment.ProcessId, Native.Title(source.Handle));
    public void ChangeTitle(string title) { if (!SetWindowText(source.Handle, title)) throw new InvalidOperationException("Could not change test player title."); }
    public FakePlayer(string path, bool visible = false)
    {
        MediaPath = path;
        source = new HwndSource(new HwndSourceParameters("same title", 640, 360) { WindowStyle = visible ? 0x10cf0000 : 0 });
        source.AddHook(Receive);
    }
    private nint Receive(nint hwnd, int message, nint command, nint parameter, ref bool handled)
    {
        if (message != 0x400) return 0;
        handled = true;
        if (command == 0x6020)
        {
            if (!ReplyToPath) return 0;
            PathReplies++;
            byte[] bytes = Encoding.UTF8.GetBytes(MediaPath + "\0"); nint memory = Marshal.AllocHGlobal(bytes.Length);
            try { Marshal.Copy(bytes, 0, memory, bytes.Length); var data = new CopyData { Command = 0x6020, Length = bytes.Length, Data = memory }; return SendMessage(parameter, 0x4A, hwnd, ref data); }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return (int)command switch { 0x5004 => 2000, 0x5006 => (int)State, 0x5015 => Speed, 0x6030 => 1920, 0x6031 => 1080, _ => 0 };
    }
    public void Dispose() { source.RemoveHook(Receive); source.Dispose(); }
}
