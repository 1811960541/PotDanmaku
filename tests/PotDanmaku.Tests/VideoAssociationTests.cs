using PotDanmaku.Core;

namespace PotDanmaku.Tests;

public sealed class VideoAssociationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PotDanmaku-associations-" + Guid.NewGuid().ToString("N"));
    public VideoAssociationTests() => Directory.CreateDirectory(root);
    private string FileAt(string name)
    {
        string path = Path.GetFullPath(Path.Combine(root, name)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "sample"); return path;
    }
    [Fact] public void ExactSiblingAndPriorityAreDeterministic()
    {
        string video = FileAt("剧集 01.mkv"); FileAt("剧集 010.xml"); FileAt("other/剧集 01.xml");
        Assert.Null(VideoFiles.Resolve(video, null));
        string ass = FileAt("剧集 01.ass"); Assert.Equal(ass, VideoFiles.Resolve(video, null));
        string json = FileAt("剧集 01.json"); Assert.Equal(json, VideoFiles.Resolve(video, null));
        string xml = FileAt("剧集 01.xml"); Assert.Equal(xml, VideoFiles.Resolve(video, null));
        var profile = new VideoProfile(video, ass, 0, new()); Assert.Equal(ass, VideoFiles.Resolve(video, profile));
        File.Delete(ass); Assert.Equal(xml, VideoFiles.Resolve(video, profile));
    }
    [Fact] public void VideoPathsSeparateSameNamedEpisodesAndSurviveRestart()
    {
        string first = FileAt("Season 1/01.mkv"), second = FileAt("Season 2/01.mkv"), danmaku = FileAt("自选弹幕.xml");
        var store = new VideoAssociationStore(root);
        store.Save(new(first, danmaku, -1.75, new(FontSize: 32, Opacity: 55, Duration: 25), "gb18030", true, ["Danmaku"]));
        store.Save(new(second, danmaku, 2.5, new(FontSize: 18)));
        var loaded = new VideoAssociationStore(root);
        var profile = Assert.IsType<VideoProfile>(loaded.Find(first.ToUpperInvariant()));
        Assert.Equal(-1.75, profile.OffsetSeconds); Assert.Equal(32, profile.Settings.FontSize);
        Assert.Equal(55, profile.Settings.Opacity); Assert.Equal(25, profile.Settings.Duration);
        Assert.Equal("gb18030", profile.Encoding); Assert.True(profile.IncludeAssSubtitles);
        Assert.Equal(new[] { "Danmaku" }, profile.AssStyles);
        Assert.Equal(2.5, loaded.Find(second)!.OffsetSeconds);
        Assert.True(File.Exists(Path.Combine(root, "video-associations.json.backup")));
    }
    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("episode.mkv")]
    [InlineData("")]
    public void UnsupportedIdentitiesNeverBecomeLocalPaths(string path) => Assert.Null(VideoFiles.Normalize(path));
    [Fact] public void FileUriAndUnicodeNormalizeToSameIdentity()
    {
        string path = FileAt("中文 空格/01.mkv");
        Assert.Equal(path, VideoFiles.Normalize(new Uri(path).AbsoluteUri));
        Assert.Equal(path, VideoFiles.Normalize(path + "\0"));
    }
    [Theory]
    [InlineData("{")]
    [InlineData("{\"Version\":99,\"Videos\":[]}")]
    [InlineData("{\"Version\":1,\"Videos\":[null]}")]
    public void DamagedCatalogIsBackedUpBeforeRecovery(string content)
    {
        string path = Path.Combine(root, "video-associations.json"); File.WriteAllText(path, content);
        var store = new VideoAssociationStore(root); Assert.NotNull(store.Warning);
        Assert.Equal(content, File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.invalid-*.backup"))));
        string video = FileAt("1.mkv"), danmaku = FileAt("1.xml");
        store.Save(new(video, danmaku, 0, new()));
        Assert.NotNull(new VideoAssociationStore(root).Find(video));
    }
    [Fact] public void InvalidOffsetDoesNotOverwriteStoredProfile()
    {
        string video = FileAt("1.mkv"), danmaku = FileAt("1.xml"); var store = new VideoAssociationStore(root);
        store.Save(new(video, danmaku, 2, new()));
        Assert.Throws<ArgumentException>(() => store.Save(new(video, danmaku, double.NaN, new())));
        Assert.Equal(2, new VideoAssociationStore(root).Find(video)!.OffsetSeconds);
    }
    public void Dispose() => Directory.Delete(root, true);
}
