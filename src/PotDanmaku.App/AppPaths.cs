using System.IO;

namespace PotDanmaku.App;

internal static class AppPaths
{
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PotDanmaku");
    public static string? Warning { get; private set; }
    public static void Configure(bool portable)
    {
        if (!portable && !File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))) return;
        string folder = Path.Combine(AppContext.BaseDirectory, "data");
        string probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(folder); File.WriteAllText(probe, ""); File.Delete(probe); DataDirectory = folder; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Warning = "便携目录不可写，已回退到本地用户配置：" + ex.Message; }
    }
}
