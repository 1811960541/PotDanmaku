using PotDanmaku.Formats;

namespace PotDanmaku.Tests;

public class AssStyleTests
{
    private const string File = """
        [Script Info]
        PlayResY: 1080
        [V4+ Styles]
        Format: Name, Fontsize, PrimaryColour, Alignment
        Style: Danmaku,16,&HFFFFFF,8
        Style: Dialogue,20,&HFFFFFF,2
        [Events]
        Format: Start, End, Style, Text
        Dialogue: 0:00:01.00,0:00:05.00,Danmaku,{\move(1900,30,-100,30)}弹幕
        Dialogue: 0:00:02.00,0:00:05.00,Dialogue,普通对白
        """;
    [Fact]
    public void CatalogContainsExcludedDialogueWithoutImportingItByDefault()
    {
        var doc = DanmakuImporter.Parse(File, ".ass");
        Assert.Single(doc.Items); Assert.Equal(2, doc.Report.AssStyles!.Count);
        Assert.Equal(0, doc.Report.AssStyles.Single(s => s.Name == "Dialogue").Accepted);
    }
    [Fact]
    public void ExplicitStyleSelectionIncludesChosenDialogueAndReportsFiltering()
    {
        var options = new ImportOptions(SelectedAssStyles: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dialogue" });
        var doc = DanmakuImporter.Parse(File, ".ass", options: options);
        Assert.Equal("普通对白", Assert.Single(doc.Items).Text);
        Assert.Equal(1, doc.Report.Filtered); Assert.Equal(0, doc.Report.Unsupported); Assert.Equal(0, doc.Report.Invalid);
        Assert.Equal(2, doc.Report.AssStyles!.Count); Assert.Equal(1, doc.Items[0].SourceIndex);
    }
    [Fact]
    public void EmptyStyleSelectionFiltersEverythingAndKeepsCatalog()
    {
        var doc = DanmakuImporter.Parse(File, ".ass", options: new(SelectedAssStyles: new HashSet<string>()));
        Assert.Empty(doc.Items); Assert.Equal(2, doc.Report.Filtered); Assert.Equal(2, doc.Report.AssStyles!.Count);
    }
}
