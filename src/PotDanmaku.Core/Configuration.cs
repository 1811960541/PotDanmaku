using System.Text.Json;

namespace PotDanmaku.Core;

public sealed record UserSettings(int Version = 2, double FontSize = 16, double Area = 25,
    double Duration = 18, double FixedDuration = 5, double Opacity = 100, double Density = 100,
    double Stroke = 0.6, int Rows = 13, double Left = 0, double Top = 0, double Right = 0, double Bottom = 0,
    bool PreserveColor = true, int FrameLimit = 0, double ManualRate = 0);

public sealed class ConfigurationStore(string folder)
{
    public string FilePath { get; } = Path.Combine(folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public UserSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath));
            if (settings == null || settings.Version is < 1 or > 2) throw new InvalidDataException("未知配置版本。");
            if (settings.Version == 1)
            {
                string backup = FilePath + ".v1.backup";
                if (!File.Exists(backup)) File.Copy(FilePath, backup);
                settings = settings with { Version = 2 }; Save(settings);
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            warning = "配置读取失败，已使用默认设置：" + ex.Message;
            try { File.Copy(FilePath, FilePath + ".invalid-" + Guid.NewGuid().ToString("N") + ".backup"); }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            { warning += "；原配置备份失败：" + backupError.Message; }
            return new();
        }
    }
    public void Save(UserSettings value)
    {
        string path = FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value with { Version = 2 }, JsonOptions));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".backup", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
