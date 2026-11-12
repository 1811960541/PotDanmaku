using System.IO;
using System.Text;

namespace PotDanmaku.App;

internal static class AppLog
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string folder = Path.Combine(AppPaths.DataDirectory, "logs"); Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_048_576)
                {
                    for (int i = 2; i >= 1; i--) if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                    File.Move(path, path + ".1", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
