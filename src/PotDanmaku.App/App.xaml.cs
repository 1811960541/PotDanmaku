using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using PotDanmaku.App.Player;
using PotDanmaku.App.Rendering;
using PotDanmaku.Core;
using PotDanmaku.Formats;

namespace PotDanmaku.App;

public partial class App : System.Windows.Application
{
    private SingleInstance? instance;
    internal static string ProductVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Configure(e.Args.Contains("--portable")); AppLog.Write("启动 " + ProductVersion);
        bool smoke = e.Args.Contains("--smoke") || e.Args.Contains("--playback-smoke");
        bool headless = e.Args.Contains("--probe") || e.Args.Contains("--inspect");
        DispatcherUnhandledException += (_, args) =>
        {
            var folder = AppPaths.DataDirectory;
            try { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "last-error.txt"), args.Exception.ToString()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!smoke && !headless) MessageBox.Show(args.Exception.Message, "PotDanmaku 出错");
            args.Handled = true; Shutdown(1);
        };
        if (headless)
        {
            string path = Argument(e.Args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", e.Args.Contains("--probe") ? "probe.json" : "inspect.json");
            try
            {
                object result;
                if (e.Args.Contains("--probe"))
                    result = await Task.Run(() => PotPlayerClient.FindPlayers().Select(p => new {
                        p.ProcessId, p.Title, Handle = p.Handle.ToInt64(), Reading = new PotPlayerClient(p).Read(), Aspect = new PotPlayerClient(p).ReadAspect()
                    }).ToArray());
                else
                    result = await Task.Run(() => Inspect(Argument(e.Args, "--file") ?? throw new InvalidDataException("--inspect 需要 --file 文件路径。"), e.Args));
                WriteJson(path, result); Shutdown();
            }
            catch (Exception ex) { WriteJson(path, new { Error = ex.Message }); Shutdown(1); }
            return;
        }
        if (!smoke)
        {
            instance = new SingleInstance();
            if (!instance.TryAcquire())
            {
                try { await instance.OpenExisting(); Shutdown(); }
                catch (Exception ex) { MessageBox.Show("程序已在运行，但无法打开设置：" + ex.Message, "PotDanmaku"); Shutdown(1); }
                return;
            }
        }
        var window = new MainWindow(!smoke); MainWindow = window; window.Show();
        instance?.Listen(window.OpenSettings);
        if (int.TryParse(Argument(e.Args, "--frame-limit"), out int frameLimit) && frameLimit is 0 or 60 or 120)
            window.SetFrameLimit(frameLimit);
        if (Argument(e.Args, "--file") is { } file && !await window.ImportFile(file))
        { if (smoke) { window.ExportDiagnostics(Argument(e.Args, "--output")); Shutdown(1); } return; }
        double start = double.TryParse(Argument(e.Args, "--start"), NumberStyles.Float, CultureInfo.InvariantCulture, out double secondsStart) &&
            double.IsFinite(secondsStart) ? Math.Clamp(secondsStart, 0, 604800) * 1000 : 0;
        if (smoke)
        {
            await Task.Delay(500);
            if (e.Args.Contains("--playback-smoke"))
            {
                var players = PotPlayerClient.FindPlayers();
                var target = int.TryParse(Argument(e.Args, "--pid"), out int pid) ? players.FirstOrDefault(p => p.ProcessId == pid) : players.FirstOrDefault();
                if (target == null) { WriteJson(Path.Combine(Argument(e.Args, "--output") ?? "artifacts/playback-smoke", "error.json"), new { Error = "没有找到指定的 PotPlayer。" }); Shutdown(1); return; }
                await window.Connect(target);
            }
            else window.StartPreview(start);
            int seconds = int.TryParse(Argument(e.Args, "--seconds"), out var requested) ? Math.Clamp(requested, 1, 600) : 8;
            if (e.Args.Contains("--simulate-device-loss"))
            { await Task.Delay(seconds * 500); window.RebuildRenderer(true); await Task.Delay(seconds * 500); }
            else await Task.Delay(seconds * 1000);
            window.ExportDiagnostics(Argument(e.Args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "smoke"));
            Shutdown(window.SmokePassed ? 0 : 2);
        }
        else if (e.Args.Contains("--preview")) { await Task.Delay(500); window.StartPreview(start); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
    private static object Inspect(string path, string[] args)
    {
        var timer = Stopwatch.StartNew();
        var document = DanmakuImporter.Load(path, new(Argument(args, "--encoding"), args.Contains("--include-subtitles")));
        double parseMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
        using var measurer = new TextMeasurer();
        var layout = LayoutBuilder.Build(document.Items, 1920, new(), measurer.Measure);
        return new {
            File = Path.GetFileName(path), Import = document.Report, Layout = layout.Report, ParseMs = parseMs, LayoutMs = timer.Elapsed.TotalMilliseconds,
            FirstMs = document.Items.FirstOrDefault()?.StartMs, LastMs = document.Items.LastOrDefault()?.StartMs,
            Modes = document.Items.GroupBy(x => x.Mode).ToDictionary(x => x.Key.ToString(), x => x.Count()),
            WorkingSetAfterLayoutMB = Environment.WorkingSet / (1024.0 * 1024),
            PrivateAfterLayoutMB = Process.GetCurrentProcess().PrivateMemorySize64 / (1024.0 * 1024),
            PeakWorkingSetMB = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024),
            Notes = "默认设置下的 1920×1080 轨道排布；不包含弹幕正文。"
        };
    }
    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string? Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
