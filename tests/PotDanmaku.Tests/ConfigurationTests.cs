using PotDanmaku.Core;

namespace PotDanmaku.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PotDanmakuTests-" + Guid.NewGuid().ToString("N"));
    private ConfigurationStore Store => new(folder);
    [Fact]
    public void MissingConfigurationUsesDefaultsWithoutWriting()
    {
        Assert.Equal(new UserSettings(), Store.Load(out var warning)); Assert.Null(warning); Assert.False(Directory.Exists(folder));
    }
    [Fact]
    public void VersionOneMigrationPreservesSettingsAndOriginalBackup()
    {
        Directory.CreateDirectory(folder); File.WriteAllText(Store.FilePath, "{\"Version\":1,\"FontSize\":24,\"Density\":75}");
        var result = Store.Load(out var warning);
        Assert.Null(warning); Assert.Equal(2, result.Version); Assert.Equal(24, result.FontSize); Assert.Equal(75, result.Density);
        Assert.Equal(0, result.FrameLimit); Assert.Contains("\"Version\":1", File.ReadAllText(Store.FilePath + ".v1.backup"));
    }
    [Theory]
    [InlineData("{broken")] [InlineData("{\"Version\":99}")]
    public void CorruptOrFutureConfigurationIsBackedUpBeforeUsingDefaults(string text)
    {
        Directory.CreateDirectory(folder); File.WriteAllText(Store.FilePath, text);
        Assert.Equal(new UserSettings(), Store.Load(out var warning)); Assert.NotNull(warning);
        Assert.Equal(text, File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "*.invalid-*.backup"))));
    }
    [Fact]
    public void AtomicSaveKeepsPreviousConfigurationAndLeavesNoTemporaryFiles()
    {
        Store.Save(new(FontSize: 20, FrameLimit: 60, ManualRate: 3));
        Store.Save(new(FontSize: 25, FrameLimit: 120));
        Assert.Equal(25, Store.Load(out _).FontSize); Assert.Equal(120, Store.Load(out _).FrameLimit);
        Assert.Contains("\"FrameLimit\": 60", File.ReadAllText(Store.FilePath + ".backup"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
