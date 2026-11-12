using PotDanmaku.Core;

namespace PotDanmaku.App;

internal static class SettingsStore
{
    private static ConfigurationStore Store => new(AppPaths.DataDirectory);
    public static UserSettings Load(out string? warning) => Store.Load(out warning);
    public static void Save(UserSettings value) => Store.Save(value);
}
