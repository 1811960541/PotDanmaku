using System.Globalization;
using System.IO;
using System.Windows;
using PotDanmaku.App.Player;
using PotDanmaku.Core;

namespace PotDanmaku.App;

public partial class MainWindow
{
    private readonly VideoAssociationStore associations = new(AppPaths.DataDirectory);
    private PlayerMediaProbe? mediaProbe;
    private string? activeVideo;
    private long mediaRevision, lastMediaSequence;
    private double lastMediaConfirmedAt = double.NegativeInfinity;
    private bool mediaReady, switchingMedia, hasFile, manualPending, connecting, discoveryAllowed, autoConnectSuspended;
    private double nextDiscovery, profileChangedAt;
    private VideoProfile? trackedProfile, savedProfile;
    private string? importedEncoding;
    private bool importedSubtitles;
    private string[]? importedStyles;
    private UserSettings videoDefaults = new();
    internal (string? Video, string Danmaku, bool Ready, bool Loading, double Offset, UserSettings Settings) VideoSession =>
        (activeVideo, document.FilePath, mediaReady, switchingMedia, ParseDouble(OffsetText.Text, 0, -86400, 86400), CurrentSettings());

    private async void DiscoverPlayer()
    {
        if (!discoveryAllowed || autoConnectSuspended || AutoConnect.IsChecked != true || running || connecting ||
            preview != null || importing != null || closed || PotPlayerClient.Now < nextDiscovery) return;
        nextDiscovery = PotPlayerClient.Now + 1;
        var targets = PotPlayerClient.FindPlayers();
        if (targets.Count == 1)
        {
            Players.ItemsSource = targets; Players.SelectedIndex = 0;
            try { await Connect(targets[0]); }
            catch (Exception ex) { StopSession(); StatusText.Text = "自动连接失败：" + ex.Message; nextDiscovery = PotPlayerClient.Now + 5; }
        }
        else if (targets.Count > 1) VideoText.Text = "检测到多个播放器，请选择要连接的窗口。";
    }

    private void ObserveMedia()
    {
        if (player == null || mediaProbe?.Latest is not { } identity || identity.Sequence == lastMediaSequence) return;
        lastMediaSequence = identity.Sequence;
        lastMediaConfirmedAt = identity.ObservedSeconds;
        // A fresh filename reply also confirms harmless title/metadata changes for the same video.
        connectedTitle = Volatile.Read(ref reading)?.Title ?? connectedTitle;
        if (!string.Equals(activeVideo, identity.Path, StringComparison.OrdinalIgnoreCase))
            SwitchVideo(identity.Path);
        else if (identity.Path == null) VideoText.Text = "尚未识别本地视频路径；可手动导入弹幕。";
    }

    private void ClearVideoDocument()
    {
        importing?.Cancel(); building?.Cancel();
        document = new("", Array.Empty<DanmakuItem>(), new("未加载", 0, 0, 0, 0, [], new Dictionary<string, int>(), "unicode"));
        hasFile = false; mediaReady = false; layout = null; desiredLayout = null; completedLayout = null;
        lastImportReport = null; AssStylesButton.IsEnabled = false;
        FileText.Text = "当前视频尚无弹幕"; ImportText.Text = "可打开或拖入对应弹幕文件";
        LayoutText.Text = ""; OffsetText.Text = "0.0"; HideScene();
    }

    private async void SwitchVideo(string? path)
    {
        SaveVideoProfile();
        bool usePending = manualPending && hasFile && activeVideo == null;
        manualPending = false;
        long revision = ++mediaRevision;
        activeVideo = path; savedProfile = null; trackedProfile = null; profileChangedAt = 0;
        VideoText.ToolTip = path;
        switchingMedia = true; mediaReady = false; aspect = 0;
        if (!usePending) ClearVideoDocument();
        try
        {
            if (path == null) { VideoText.Text = "视频已关闭或当前来源不支持本地文件匹配。"; return; }
            VideoText.Text = "正在匹配：" + Path.GetFileName(path);
            if (usePending) { mediaReady = true; SaveVideoProfile(); VideoText.Text = "已关联手动选择的弹幕：" + Path.GetFileName(path); return; }
            var profile = associations.Find(path);
            var candidate = await Task.Run(() => VideoFiles.Resolve(path, profile));
            if (closed || revision != mediaRevision) return;
            ApplySettings(profile?.Settings ?? videoDefaults);
            if (candidate == null) { VideoText.Text = "未找到同名弹幕：" + Path.GetFileName(path) + "；手动导入后会记住关联。"; return; }
            bool restoring = profile != null && string.Equals(candidate, profile.DanmakuPath, StringComparison.OrdinalIgnoreCase);
            EncodingChoice.SelectedIndex = restoring && profile!.Encoding == "gb18030" ? 1 : 0;
            IncludeAss.IsChecked = restoring && profile!.IncludeAssSubtitles;
            var styles = restoring ? profile!.AssStyles?.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            if (await ImportFile(candidate, styles, automatic: true))
            {
                if (closed || revision != mediaRevision) return;
                OffsetText.Text = (profile?.OffsetSeconds ?? 0).ToString("0.########", CultureInfo.InvariantCulture);
                mediaReady = true; SaveVideoProfile();
                VideoText.Text = (profile != null ? "已恢复视频设置：" : "已自动匹配：") + Path.GetFileName(path);
            }
            else if (revision == mediaRevision) VideoText.Text = "自动导入未完成，请手动选择弹幕：" + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (revision == mediaRevision) VideoText.Text = "视频匹配失败：" + ex.Message; }
        finally { if (revision == mediaRevision) switchingMedia = false; }
    }

    private VideoProfile? CurrentVideoProfile() => activeVideo != null && mediaReady && hasFile
        ? new(activeVideo, Path.GetFullPath(document.FilePath), ParseDouble(OffsetText.Text, 0, -86400, 86400), CurrentSettings(),
            importedEncoding, importedSubtitles, importedStyles) : null;

    private void TrackVideoProfile()
    {
        if (switchingMedia) return;
        var profile = CurrentVideoProfile();
        if (profile != trackedProfile) { trackedProfile = profile; profileChangedAt = PotPlayerClient.Now; }
        else if (profileChangedAt > 0 && PotPlayerClient.Now - profileChangedAt > 0.6) { SaveVideoProfile(); profileChangedAt = 0; }
    }
    private void SaveVideoProfile()
    {
        var profile = CurrentVideoProfile();
        if (profile == null || profile == savedProfile) return;
        try { associations.Save(profile); savedProfile = profile; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { VideoText.Text = "视频关联保存失败：" + ex.Message; AppLog.Write(VideoText.Text); }
    }
}
