using System.Text.Json;

namespace PotDanmaku.Core;

public sealed record VideoProfile(string VideoPath, string DanmakuPath, double OffsetSeconds,
    UserSettings Settings, string? Encoding = null, bool IncludeAssSubtitles = false, string[]? AssStyles = null);

public static class VideoFiles
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.TrimEnd('\0');
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) return null;
        if (uri?.IsFile == true) path = uri.LocalPath;
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // Explicit associations take precedence; a missing association falls back to an exact sibling.
    public static string? Resolve(string videoPath, VideoProfile? profile)
    {
        if (profile != null && File.Exists(profile.DanmakuPath)) return profile.DanmakuPath;
        foreach (string extension in new[] { ".xml", ".json", ".ass" })
        {
            string path = Path.ChangeExtension(videoPath, extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}

public sealed class VideoAssociationStore
{
    private sealed record Catalog(int Version, List<VideoProfile> Videos);
    private readonly Dictionary<string, VideoProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path;
    public string? Warning { get; private set; }
    public VideoAssociationStore(string directory)
    {
        path = Path.Combine(directory, "video-associations.json");
        if (!File.Exists(path)) return;
        try
        {
            var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path));
            if (catalog is not { Version: 1, Videos: not null }) throw new InvalidDataException("不支持的视频关联版本。");
            foreach (var profile in catalog.Videos)
            {
                if (profile == null || VideoFiles.Normalize(profile.VideoPath) is not { } key ||
                    VideoFiles.Normalize(profile.DanmakuPath) == null || profile.Settings == null ||
                    !double.IsFinite(profile.OffsetSeconds) || Math.Abs(profile.OffsetSeconds) > 86400)
                    throw new InvalidDataException("视频关联数据无效。");
                profiles[key] = profile with { VideoPath = key };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            profiles.Clear(); Warning = "视频关联读取失败：" + ex.Message;
            // Preserve the damaged file before allowing subsequent writes.
            try { File.Copy(path, path + ".invalid-" + Guid.NewGuid().ToString("N") + ".backup"); }
            catch (Exception backup) when (backup is IOException or UnauthorizedAccessException) { writable = false; Warning += "；备份失败，关联保存已停用。"; }
        }
    }
    private bool writable = true;
    public VideoProfile? Find(string videoPath) => profiles.GetValueOrDefault(Path.GetFullPath(videoPath));
    public void Save(VideoProfile profile)
    {
        if (!writable) throw new IOException(Warning);
        string key = VideoFiles.Normalize(profile.VideoPath) ?? throw new ArgumentException("视频路径无效。");
        if (VideoFiles.Normalize(profile.DanmakuPath) == null || profile.Settings == null ||
            !double.IsFinite(profile.OffsetSeconds) || Math.Abs(profile.OffsetSeconds) > 86400) throw new ArgumentException("视频配置无效。");
        profile = profile with { VideoPath = key };
        var previous = profiles.GetValueOrDefault(key);
        profiles[key] = profile;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Catalog(1, profiles.Values.ToList()), new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".backup", true);
            else File.Move(temporary, path);
        }
        catch { if (previous == null) profiles.Remove(key); else profiles[key] = previous; throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
