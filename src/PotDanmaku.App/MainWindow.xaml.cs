using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PotDanmaku.App.Interop;
using PotDanmaku.App.Player;
using PotDanmaku.App.Rendering;
using PotDanmaku.Core;
using PotDanmaku.Formats;

namespace PotDanmaku.App;

public partial class MainWindow : Window
{
    private const double PlaybackSampleLifetime = 0.25;
    private const double MediaIdentityGrace = 0.5;
    private sealed record LayoutRequest(DanmakuDocument Document, float Width, LayoutOptions Options);
    private sealed record WindowObservation(double MonotonicSeconds, long TargetHandle, long ForegroundHandle,
        bool TargetForeground, bool TargetMinimized, bool TargetCloaked, bool TargetTopmost, bool OverlayTopmost,
        bool OverlayVisible, bool OverlayImmediatelyAboveTarget, bool TopmostMatches, bool ForegroundUnchangedDuringPlacement);
    internal bool SmokePassed => renderer?.Health is { Frames: > 60, Error: null, Accepted: > 0 } && lastVisibleHitTest == true;
    private OverlayHost? overlay;
    private RenderLoop? renderer;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private CancellationTokenSource? polling, importing, building;
    private Task? pollingTask;
    private PlayerReading? reading;
    private PlayerTarget? player;
    private Window? preview;
    private double aspect, previewStartMs;
    private long generation;
    private bool running, closed;
    private string? connectedTitle;
    private double lastValidPlaybackAt = double.NegativeInfinity;
    private Native.Rect lastRect;
    private readonly List<PlayerReading> samples = [];
    private readonly List<WindowObservation> windowSamples = [];
    private bool? lastVisibleHitTest;
    private double lastHitTestSeconds;
    private DanmakuDocument document = DemoDocument();
    private ImportReport? lastImportReport;
    private DanmakuLayout? layout;
    private LayoutRequest? desiredLayout, completedLayout;
    private double layoutRequestedAt;
    private UserSettings? lastSettings;
    private double settingsChangedAt;
    private int tickCount;
    private TrayController? tray;
    private double manualRate;
    private nint lastMonitor;
    private uint lastDpi;

    public MainWindow(bool createTray = true, bool? autoDiscover = null)
    {
        InitializeComponent();
        Title = $"PotDanmaku · {App.ProductVersion}";
        var settings = SettingsStore.Load(out string? warning); ApplySettings(settings); lastSettings = CurrentSettings();
        videoDefaults = CurrentSettings(); discoveryAllowed = autoDiscover ?? createTray;
        if (associations.Warning != null) VideoText.Text = associations.Warning;
        if (warning != null || AppPaths.Warning != null) { StatusText.Text = warning ?? AppPaths.Warning ?? ""; AppLog.Write(StatusText.Text); }
        Loaded += (_, _) =>
        {
            RefreshPlayers(); timer.Start();
            if (createTray)
                try { tray = new TrayController(OpenSettings, () => SetDanmakuEnabled(DanmakuEnabled.IsChecked != true), () => System.Windows.Application.Current.Shutdown()); }
                catch (Exception ex) { StatusText.Text = "托盘初始化失败，仍可使用主窗口：" + ex.Message; AppLog.Write(StatusText.Text); }
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && tray != null) Hide(); };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        timer.Tick += (_, _) => Tick();
        Closed += (_, _) =>
        {
            closed = true; timer.Stop(); importing?.Cancel(); building?.Cancel();
            SaveSettings(); StopSession(); renderer?.Dispose(); overlay?.Dispose();
            tray?.Dispose();
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            AppLog.Write("程序退出");
        };
    }
    internal void OpenSettings() { Show(); WindowState = WindowState.Normal; Activate(); AppLog.Write("打开设置窗口"); }
    private void SetDanmakuEnabled(bool enabled) { DanmakuEnabled.IsChecked = enabled; tray?.Update(enabled); if (!enabled) overlay?.Hide(); }
    private void VisibilityClick(object sender, RoutedEventArgs e) => SetDanmakuEnabled(DanmakuEnabled.IsChecked == true);
    private void HideToTrayClick(object sender, RoutedEventArgs e) { if (tray != null) { Hide(); AppLog.Write("已收起到托盘"); } else StatusText.Text = "托盘暂不可用，请保留主窗口。"; }
    private int FrameLimit => FrameLimitChoice.SelectedIndex switch { 1 => 60, 2 => 120, _ => 0 };
    internal void SetFrameLimit(int limit) => FrameLimitChoice.SelectedIndex = limit switch { 60 => 1, 120 => 2, _ => 0 };
    private double ManualRate => RateChoice.SelectedIndex switch { 1 => 0.5, 2 => 1, 3 => 1.5, 4 => 2, 5 => 3, _ => 0 };
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => RebuildRenderer());
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => RebuildRenderer()); }
    internal void RebuildRenderer(bool simulateLoss = false)
    { if (!closed) { renderer?.RequestDeviceRebuild(simulateLoss); AppLog.Write(simulateLoss ? "测试注入设备丢失" : "请求重建绘制层"); } }
    private void RebuildClick(object sender, RoutedEventArgs e) => RebuildRenderer();

    private static DanmakuDocument DemoDocument()
    {
        string[] text = ["道友请留步", "独立绘制，随视频同步", "顶部四分之一 · 慢速", "中文与 emoji 🎉", "调节字号和透明度", "暂停和跳转也能同步"];
        var values = Enumerable.Range(0, 360).Select(i => new DanmakuItem($"demo-{i}", i, i * 500,
            text[i % text.Length], i % 31 == 0 ? DanmakuMode.Top : i % 29 == 0 ? DanmakuMode.Reverse : i % 23 == 0 ? DanmakuMode.Bottom : DanmakuMode.Scroll,
            i % 4 == 0 ? new DanmakuColor(94, 234, 212) : DanmakuColor.White)).ToArray();
        return new("示例弹幕", Array.AsReadOnly(values), new("内置示例", values.Length, values.Length, 0, 0, [], new Dictionary<string, int>(), "unicode"));
    }
    private void RefreshPlayers() { Players.ItemsSource = PotPlayerClient.FindPlayers(); if (Players.Items.Count > 0) Players.SelectedIndex = 0; }
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshPlayers();
    private void EnsureRenderer()
    {
        if (renderer?.Health.Error != null) { renderer.Dispose(); renderer = null; overlay?.Dispose(); overlay = null; }
        if (renderer == null) { overlay = new OverlayHost(); renderer = new RenderLoop(overlay.Handle); }
    }
    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "弹幕文件|*.xml;*.json;*.ass|XML|*.xml|JSON|*.json|ASS|*.ass", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await ImportFile(picker.FileName);
    }
    private void FileDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void FileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1) await ImportFile(paths[0]);
        else StatusText.Text = "请一次拖入一个弹幕文件。";
    }
    private void CancelImportClick(object sender, RoutedEventArgs e) => importing?.Cancel();
    internal async Task<bool> ImportFile(string path, IReadOnlySet<string>? selectedStyles = null, bool automatic = false)
    {
        if (!automatic && switchingMedia) { ++mediaRevision; switchingMedia = false; }
        long revision = mediaRevision;
        importing?.Cancel(); var source = new CancellationTokenSource(); importing = source;
        var options = new ImportOptions(EncodingChoice.SelectedIndex == 1 ? "gb18030" : null, IncludeAss.IsChecked == true, selectedStyles);
        ImportText.Text = "正在导入…"; OpenButton.IsEnabled = false;
        AppLog.Write("开始导入：" + Path.GetFileName(path));
        lastImportReport = null;
        try
        {
            var result = await Task.Run(() => DanmakuImporter.Load(path, options, source.Token));
            if (closed || source.IsCancellationRequested || importing != source || revision != mediaRevision) return false;
            lastImportReport = result.Report;
            if (result.Items.Count == 0) throw new InvalidDataException("文件没有可播放的弹幕。请查看格式与 ASS 普通字幕选项。");
            document = result; layout = null; desiredLayout = null; building?.Cancel();
            FileText.Text = Path.GetFileName(path);
            ImportText.Text = $"{result.Report.Format} · 成功 {result.Report.Accepted:N0} · 无效 {result.Report.Invalid:N0} · 未支持 {result.Report.Unsupported:N0} · 样式排除 {result.Report.Filtered:N0}";
            AssStylesButton.IsEnabled = result.Report.AssStyles is { Count: > 0 };
            OffsetText.Text = "0.0";
            importedEncoding = options.EncodingName; importedSubtitles = options.IncludeUnpositionedAss;
            importedStyles = selectedStyles?.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            hasFile = true; mediaReady = true;
            if (!automatic) { manualPending = player == null && preview == null; SaveVideoProfile(); }
            if (preview != null) { previewStartMs = 0; generation++; }
            StatusText.Text = "导入完成。连接播放器，或打开独立预览。";
            AppLog.Write($"导入完成：{result.Report.Format} 成功={result.Report.Accepted} 无效={result.Report.Invalid} 未支持={result.Report.Unsupported}");
            return true;
        }
        catch (OperationCanceledException) { if (!closed && importing == source) ImportText.Text = "导入已取消，保留上一份弹幕。"; return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or JsonException or ArgumentException)
        { if (!closed && importing == source) { ImportText.Text = "导入失败，保留上一份弹幕。"; StatusText.Text = ex.Message; AppLog.Write("导入失败：" + ex.Message); } return false; }
        finally { if (importing == source) { importing = null; if (!closed) OpenButton.IsEnabled = true; } source.Dispose(); }
    }
    private void ReportClick(object sender, RoutedEventArgs e)
    {
        var r = lastImportReport ?? document.Report;
        string text = $"格式：{r.Format}\n编码：{r.Encoding}\n总数：{r.Total:N0}\n成功：{r.Accepted:N0}\n无效：{r.Invalid:N0}\n未支持：{r.Unsupported:N0}\n样式排除：{r.Filtered:N0}\n\n" +
            string.Join("\n", r.Counts.Select(x => $"{x.Key}: {x.Value:N0}")) + "\n\n" + string.Join("\n", r.Issues.Select(x => $"位置 {x.Location} · {x.Message}"));
        var content = new System.Windows.Controls.TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        new Window { Title = "弹幕导入报告（最多显示前 100 条提示）", Owner = this, Width = 660, Height = 580, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    private async void AssStylesClick(object sender, RoutedEventArgs e)
    {
        if (document.Report.AssStyles is not { Count: > 0 } styles || importing != null) return;
        var choices = styles.Select(s => (Style: s, Box: new System.Windows.Controls.CheckBox {
            Content = $"{s.Name} · {s.Total:N0} 条（当前成功 {s.Accepted:N0}）", IsChecked = s.Accepted > 0, Margin = new Thickness(0, 5, 0, 5) })).ToArray();
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "选择要显示的样式。选中样式中的普通对白也会导入；复杂特效仍会跳过。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var list = new System.Windows.Controls.StackPanel(); foreach (var choice in choices) list.Children.Add(choice.Box);
        panel.Children.Add(new System.Windows.Controls.ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto });
        var buttons = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var all = new System.Windows.Controls.Button { Content = "全选", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 12, 0) };
        var apply = new System.Windows.Controls.Button { Content = "应用并重新导入", Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(all); buttons.Children.Add(apply); panel.Children.Add(buttons);
        var dialog = new Window { Title = "ASS 样式筛选", Owner = this, Width = 540, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        IReadOnlySet<string>? selection = null;
        all.Click += (_, _) => { foreach (var choice in choices) choice.Box.IsChecked = true; };
        apply.Click += (_, _) =>
        {
            var selected = choices.Where(c => c.Box.IsChecked == true).Select(c => c.Style.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0) { apply.Content = "请至少选择一个样式"; return; }
            selection = selected; dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() == true) await ImportFile(document.FilePath, selection);
    }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (Players.SelectedItem is not PlayerTarget target) { StatusText.Text = "没有找到 PotPlayer，请先打开播放器和视频。"; return; }
        await Connect(target);
    }
    internal async Task Connect(PlayerTarget target)
    {
        if (connecting) return;
        connecting = true; autoConnectSuspended = false;
        try
        {
        StopSession(); EnsureRenderer(); player = target; long request = ++generation;
        var client = new PotPlayerClient(target); double value = await Task.Run(client.ReadAspect);
        if (closed || request != generation) return;
        aspect = value; connectedTitle = Native.Title(target.Handle); running = true; samples.Clear(); windowSamples.Clear();
        mediaReady = hasFile && manualPending;
        VideoText.Text = "正在识别视频路径；支持的本地视频会自动匹配并记住设置。";
        var probe = new PlayerMediaProbe(target); mediaProbe = probe;
        var source = new CancellationTokenSource(); polling = source;
        pollingTask = Task.Run(async () =>
        {
            try
            {
                using var periodic = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
                double nextAspectProbe = 0;
                double lastMediaProbe = double.NegativeInfinity;
                string? lastProbedTitle = null;
                do
                {
                    double rate = Volatile.Read(ref manualRate);
                    var result = client.Read(rate > 0 ? rate : null); if (source.IsCancellationRequested) break; Volatile.Write(ref reading, result);
                    // A new title can indicate a switch. Confirm the path sooner without blanking on pause labels.
                    double probeInterval = string.Equals(result.Title, lastProbedTitle, StringComparison.Ordinal) ? 0.25 : 0.1;
                    if (PotPlayerClient.Now - lastMediaProbe >= probeInterval)
                    {
                        probe.Request();
                        lastProbedTitle = result.Title;
                        lastMediaProbe = PotPlayerClient.Now;
                    }
                    // A connection can happen before PotPlayer has finished opening the video.
                    if (Volatile.Read(ref aspect) <= 0 && result.Sample?.State is PlaybackState.Playing or PlaybackState.Paused && PotPlayerClient.Now >= nextAspectProbe)
                    {
                        double discovered = client.ReadAspect(); if (discovered > 0) Volatile.Write(ref aspect, discovered);
                        nextAspectProbe = PotPlayerClient.Now + 1;
                    }
                }
                while (await periodic.WaitForNextTickAsync(source.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Write("读取播放器失败：" + ex.Message); Volatile.Write(ref reading, new(null, "读取播放器失败：" + ex.Message, connectedTitle ?? "", -1, 0, 0, 0)); }
        });
        StatusText.Text = "已连接。播放器可见时会同步显示弹幕，可以切到其他软件。";
        AppLog.Write($"连接播放器 PID={target.ProcessId}");
        }
        finally { connecting = false; }
    }
    private void PreviewClick(object sender, RoutedEventArgs e) => StartPreview();
    internal void StartPreview(double startMs = 0)
    {
        autoConnectSuspended = true; manualPending = false;
        if (!hasFile) document = DemoDocument();
        StopSession(); EnsureRenderer(); previewStartMs = startMs; generation++; running = true;
        samples.Clear(); windowSamples.Clear();
        var canvas = new System.Windows.Controls.Grid { Background = new SolidColorBrush(Color.FromRgb(25, 46, 57)) };
        for (int i = 0; i < 12; i++)
        {
            canvas.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            var band = new System.Windows.Controls.Border { Background = new SolidColorBrush(i % 2 == 0 ? Color.FromRgb(28, 50, 65) : Color.FromRgb(35, 60, 71)) };
            System.Windows.Controls.Grid.SetColumn(band, i); canvas.Children.Add(band);
        }
        var label = new System.Windows.Controls.TextBlock { Text = "弹幕独立预览\n\n从文件时间轴开头播放\n返回主界面可调整区域、速度和字号\n点击背景检查输入穿透", Foreground = Brushes.White,
            FontSize = 20, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetColumnSpan(label, 12); canvas.Children.Add(label); int clicks = 0;
        canvas.MouseDown += (_, _) => label.Text = $"弹幕独立预览\n\n背景收到鼠标输入：{++clicks} 次";
        preview = new Window { Title = "PotDanmaku · 弹幕预览", Width = 1100, Height = 650, Content = canvas, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        preview.Closed += (_, _) => { preview = null; if (player == null) { running = false; HideScene(); } };
        preview.Show();
    }
    private void StopClick(object sender, RoutedEventArgs e) { autoConnectSuspended = true; StopSession(); StatusText.Text = "已停止。点击连接可恢复自动跟随。"; }
    private void HideScene()
    {
        lastValidPlaybackAt = double.NegativeInfinity;
        overlay?.Hide(); renderer?.Update(new(false, 960, 540, false, null, 0, 1, true, null, ++generation));
    }
    private void StopSession()
    {
        SaveVideoProfile(); ++mediaRevision; importing?.Cancel();
        running = false; polling?.Cancel(); pollingTask?.GetAwaiter().GetResult(); polling?.Dispose(); polling = null; pollingTask = null;
        mediaProbe?.Dispose(); mediaProbe = null; activeVideo = null; lastMediaSequence = 0;
        lastMediaConfirmedAt = double.NegativeInfinity;
        mediaReady = false; switchingMedia = false; savedProfile = null; trackedProfile = null;
        player = null; Volatile.Write(ref reading, null); preview?.Close(); preview = null; HideScene(); lastVisibleHitTest = null;
    }
    private LayoutOptions Options() => new((float)Math.Round(FontSizeControl.Value), (float)(AreaControl.Value / 100),
        Math.Round(DurationControl.Value), ParseDouble(FixedText.Text, 5, 1, 60), (int)ParseDouble(RowsText.Text, 13, 1, 100),
        200, (int)Math.Round(DensityControl.Value), (float)ParseDouble(StrokeText.Text, 0.6, 0, 5));
    private static double ParseDouble(string text, double fallback, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result) && result >= min && result <= max ? result : fallback;

    private void RequestLayout(float width)
    {
        var request = new LayoutRequest(document, MathF.Round(width, 2), Options());
        if (request != desiredLayout) { desiredLayout = request; layoutRequestedAt = PotPlayerClient.Now; building?.Cancel(); }
        if (request == completedLayout && layout != null || building != null || PotPlayerClient.Now - layoutRequestedAt < 0.18) return;
        BuildLayout(request);
    }
    private async void BuildLayout(LayoutRequest request)
    {
        var source = new CancellationTokenSource(); building = source; LayoutText.Text = "正在排布弹幕…";
        try
        {
            var result = await Task.Run(() => { using var measurer = new TextMeasurer(); return LayoutBuilder.Build(request.Document.Items, request.Width, request.Options, measurer.Measure, source.Token); });
            if (closed || source.IsCancellationRequested || request != desiredLayout) return;
            layout = result; completedLayout = request;
            LayoutText.Text = $"排布 {result.Report.Accepted:N0} 条 · 拥挤跳过 {result.Report.Crowded:N0} · 密度过滤 {result.Report.DensityFiltered:N0} · 过长 {result.Report.TooWide:N0} · {result.Report.Rows} 行";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) { StatusText.Text = "排布失败：" + ex.Message; desiredLayout = null; running = false; HideScene(); } }
        finally { if (building == source) building = null; source.Dispose(); }
    }
    private void Tick()
    {
        TrackSettings();
        TrackVideoProfile(); DiscoverPlayer(); ObserveMedia();
        Volatile.Write(ref manualRate, ManualRate);
        if (!running || renderer == null || overlay == null) return;
        var current = Volatile.Read(ref reading);
        if (renderer.Health.Error is { } error) { HideScene(); running = false; StatusText.Text = "绘制失败，请重新连接：" + error; return; }
        nint hwnd = preview != null ? new WindowInteropHelper(preview).Handle : player?.Handle ?? 0;
        nint foreground = Native.GetForegroundWindow();
        bool targetForeground = Native.GetAncestor(foreground, 2) == hwnd;
        bool visible = hwnd != 0 && Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && !Native.IsCloaked(hwnd);
        nint monitor = Native.MonitorFromWindow(hwnd, 2); uint dpi = Native.GetDpiForWindow(hwnd);
        if (lastMonitor != 0 && (monitor != lastMonitor || dpi != lastDpi)) RebuildRenderer();
        lastMonitor = monitor; lastDpi = dpi;
        if (player != null)
        {
            if (current != null && (current.Status == "播放器已关闭" || current.Status == "窗口已属于其他进程"))
            { StopSession(); StatusText.Text = current.Status + "。重新打开播放器后连接即可。"; return; }
            double now = PotPlayerClient.Now;
            var playback = current?.Sample;
            // Even an incomplete sample can explicitly report that playback stopped.
            if (current?.RawState == (int)PlaybackState.Stopped || playback?.State == PlaybackState.Stopped)
                lastValidPlaybackAt = double.NegativeInfinity;
            else if (playback != null) lastValidPlaybackAt = playback.ObservedSeconds;
            bool canHoldFrame = playback == null && overlay.Visible;
            visible &= (playback != null || canHoldFrame) && now - lastValidPlaybackAt <= PlaybackSampleLifetime;

            bool recentMedia = activeVideo != null && now - lastMediaConfirmedAt <= MediaIdentityGrace;
            if (!recentMedia && current != null && !string.Equals(current.Title, connectedTitle, StringComparison.Ordinal))
                visible = false;
            string displayStatus = !visible ? "等待播放器可见或有效进度" : canHoldFrame ? "暂时保留当前弹幕" : "正在叠加";
            StatusText.Text = current == null ? "正在读取播放器…" : $"{current.Status} · {(playback?.Rate ?? 0):F2}× · {displayStatus}\n{current.Title}";
        }
        else StatusText.Text = visible ? "独立预览中" : "预览不可见，弹幕层已隐藏";
        if (!Native.GetClientRect(hwnd, out var rect)) visible = false;
        var origin = new Native.Point(0, 0); Native.ClientToScreen(hwnd, ref origin);
        rect.Left += origin.X; rect.Right += origin.X; rect.Top += origin.Y; rect.Bottom += origin.Y;
        if (player != null)
        {
            int w = rect.Width, h = rect.Height;
            rect.Left += (int)(w * LeftMargin.Value / 100); rect.Right -= (int)(w * RightMargin.Value / 100);
            rect.Top += (int)(h * TopMargin.Value / 100); rect.Bottom -= (int)(h * BottomMargin.Value / 100);
            if (aspect > 0 && rect.Height > 0)
            {
                if (rect.Width / (double)rect.Height > aspect) { int margin = (int)((rect.Width - rect.Height * aspect) / 2); rect.Left += margin; rect.Right -= margin; }
                else { int margin = (int)((rect.Height - rect.Width / aspect) / 2); rect.Top += margin; rect.Bottom -= margin; }
            }
        }
        if (rect.Width > 0 && rect.Height > 0) RequestLayout(rect.Width / (float)rect.Height * 1080);
        else visible = false;
        visible &= layout != null && completedLayout == desiredLayout && DanmakuEnabled.IsChecked == true && !renderer.Health.Recovering && (player == null || (mediaReady && !switchingMedia));
        if (renderer.Health.Recovering) StatusText.Text = "正在恢复绘制设备，恢复后将继续当前视频。";
        else if (DanmakuEnabled.IsChecked != true) StatusText.Text += "\n弹幕显示已关闭，播放时间仍在同步。";
        if (visible)
        {
            visible = overlay.Move(rect, hwnd);
            if (visible) lastRect = rect;
            else StatusText.Text = "暂时无法定位弹幕层，正在等待播放器窗口恢复。";
        }
        else overlay.Hide();
        nint foregroundAfterPlacement = Native.GetForegroundWindow();
        if (visible && PotPlayerClient.Now - lastHitTestSeconds > 1)
        {
            nint hit = Native.WindowFromPoint(new(rect.Left + rect.Width / 2, rect.Top + 30));
            // A covering work window may legitimately receive the input instead.
            lastVisibleHitTest = hit != overlay.Handle; lastHitTestSeconds = PotPlayerClient.Now;
        }
        renderer.Update(new(visible, Math.Max(1, rect.Width), Math.Max(1, rect.Height), preview != null, layout,
            ParseDouble(OffsetText.Text, 0, -86400, 86400) * 1000, (float)(OpacityControl.Value / 100), PreserveColor.IsChecked == true, current, generation, previewStartMs, FrameLimit));
        var health = renderer.Health;
        MetricsText.Text = $"显示 {health.Active} 条 · 媒体时间 {health.MediaMs / 1000:F2} s\n提交帧数 {health.Frames:N0} · 提交间隔 P95 {health.P95Ms:F2} ms · 查询 {current?.RequestMs ?? 0:F2} ms\n提交统计不等同于显示器实际帧率。";
        bool sampleTick = ++tickCount % 2 == 0;
        if (sampleTick && current != null && samples.Count < 20_000) samples.Add(current);
        // A hide/show can last only one UI tick. Keep visibility changes even between regular samples.
        if (windowSamples.Count < 20_000 && (sampleTick || windowSamples.Count == 0 || windowSamples[^1].OverlayVisible != overlay.Visible))
            windowSamples.Add(new(PotPlayerClient.Now, hwnd.ToInt64(), foreground.ToInt64(),
                targetForeground, Native.IsIconic(hwnd), Native.IsCloaked(hwnd), OverlayHost.IsTopmost(hwnd), OverlayHost.IsTopmost(overlay.Handle),
                overlay.Visible, Native.GetWindow(hwnd, 3) == overlay.Handle,
                OverlayHost.IsTopmost(hwnd) == OverlayHost.IsTopmost(overlay.Handle), foreground == foregroundAfterPlacement));
    }
    private void ChangeOffset(double change) => OffsetText.Text = (ParseDouble(OffsetText.Text, 0, -86400, 86400) + change).ToString("F1", CultureInfo.InvariantCulture);
    private void OffsetLessClick(object sender, RoutedEventArgs e) => ChangeOffset(-0.1);
    private void OffsetMoreClick(object sender, RoutedEventArgs e) => ChangeOffset(0.1);
    private void OffsetResetClick(object sender, RoutedEventArgs e) => OffsetText.Text = "0.0";
    private void PresetClick(object sender, RoutedEventArgs e) { ApplySettings(new UserSettings(Left: LeftMargin.Value, Top: TopMargin.Value, Right: RightMargin.Value, Bottom: BottomMargin.Value)); }
    private UserSettings CurrentSettings() => new(FontSize: FontSizeControl.Value, Area: AreaControl.Value, Duration: DurationControl.Value,
        FixedDuration: ParseDouble(FixedText.Text, 5, 1, 60), Opacity: OpacityControl.Value, Density: DensityControl.Value,
        Stroke: ParseDouble(StrokeText.Text, 0.6, 0, 5), Rows: (int)ParseDouble(RowsText.Text, 13, 1, 100),
        Left: LeftMargin.Value, Top: TopMargin.Value, Right: RightMargin.Value, Bottom: BottomMargin.Value, PreserveColor: PreserveColor.IsChecked == true,
        FrameLimit: FrameLimit, ManualRate: ManualRate);
    private void ApplySettings(UserSettings value)
    {
        static double Clamp(double n, double min, double max, double fallback) => double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
        FontSizeControl.Value = Clamp(value.FontSize, 8, 80, 16); AreaControl.Value = Clamp(value.Area, 10, 100, 25);
        DurationControl.Value = Clamp(value.Duration, 3, 40, 18); OpacityControl.Value = Clamp(value.Opacity, 10, 100, 100);
        DensityControl.Value = Clamp(value.Density, 1, 100, 100); RowsText.Text = Math.Clamp(value.Rows, 1, 100).ToString(CultureInfo.InvariantCulture);
        FixedText.Text = Clamp(value.FixedDuration, 1, 60, 5).ToString(CultureInfo.InvariantCulture); StrokeText.Text = Clamp(value.Stroke, 0, 5, 0.6).ToString(CultureInfo.InvariantCulture);
        LeftMargin.Value = Clamp(value.Left, 0, 40, 0); TopMargin.Value = Clamp(value.Top, 0, 40, 0); RightMargin.Value = Clamp(value.Right, 0, 40, 0); BottomMargin.Value = Clamp(value.Bottom, 0, 40, 0);
        PreserveColor.IsChecked = value.PreserveColor;
        FrameLimitChoice.SelectedIndex = value.FrameLimit switch { 60 => 1, 120 => 2, _ => 0 };
        RateChoice.SelectedIndex = value.ManualRate switch { 0.5 => 1, 1 => 2, 1.5 => 3, 2 => 4, 3 => 5, _ => 0 };
        Volatile.Write(ref manualRate, ManualRate);
    }
    private void TrackSettings()
    {
        var value = CurrentSettings();
        if (value != lastSettings) { lastSettings = value; settingsChangedAt = PotPlayerClient.Now; }
        else if (settingsChangedAt > 0 && PotPlayerClient.Now - settingsChangedAt > 0.6) { SaveSettings(); settingsChangedAt = 0; }
    }
    private void SaveSettings() { try { SettingsStore.Save(CurrentSettings()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "设置保存失败：" + ex.Message; } }
    internal string ExportDiagnostics(string? destination = null)
    {
        string folder = destination ?? Path.Combine(AppPaths.DataDirectory, "diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder); renderer?.Export(Path.Combine(folder, "frames.csv"));
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(new {
            Created = DateTimeOffset.Now, Version = App.ProductVersion, Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString(),
            File = Path.GetFileName(document.FilePath), Import = document.Report, Layout = layout?.Report, Settings = CurrentSettings(),
            Player = player == null ? null : new { player.ProcessId, player.Title, Handle = player.Handle.ToInt64() },
            PlayerReading = Volatile.Read(ref reading), Renderer = renderer?.Health, LastVisibleHitPassed = lastVisibleHitTest,
            Viewport = new { lastRect.Left, lastRect.Top, Width = lastRect.Width, Height = lastRect.Height },
            Notes = "Submission intervals aren't physical display intervals. Video rectangle is estimated."
        }, options));
        File.WriteAllText(Path.Combine(folder, "player-samples.json"), JsonSerializer.Serialize(samples, options));
        File.WriteAllText(Path.Combine(folder, "window-samples.json"), JsonSerializer.Serialize(windowSamples, options)); return folder;
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try { string folder = ExportDiagnostics(); Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }
    private void ReadmeClick(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "README.md");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); else StatusText.Text = "使用说明位于项目根目录 README.md。";
    }
}
