using System.Text;
using System.Text.Json;
using System.Xml;
using PotDanmaku.Core;
using PotDanmaku.Formats;

namespace PotDanmaku.Tests;

public class FormatTests
{
    [Fact]
    public void XmlReadsAdjacentRecordsModesEscapesAndStableTimes()
    {
        var doc = DanmakuImporter.Parse("<i><d p=\"2.5,1,25,16711680\">A&amp;B</d><d p=\"0,5,16,255\">顶部</d><d p=\"2.5,6,16,16777215\">反向 🎉</d><d p=\"3,4,16,0\">底部</d></i>", ".xml");
        Assert.Equal(4, doc.Report.Total); Assert.Equal(4, doc.Items.Count);
        Assert.Equal([DanmakuMode.Top, DanmakuMode.Scroll, DanmakuMode.Reverse, DanmakuMode.Bottom], doc.Items.Select(x => x.Mode));
        Assert.Equal(2500, doc.Items[1].StartMs); Assert.Equal("A&B", doc.Items[1].Text);
        Assert.Equal(new DanmakuColor(255, 0, 0), doc.Items[1].Color);
    }
    [Fact]
    public void XmlReportsBadAndUnsupportedRecordsWithoutLosingGoodOnes()
    {
        var doc = DanmakuImporter.Parse("<i><d p=\"-1,1,16,0\">bad</d><d p=\"1,7,16,0\">special</d><d p=\"NaN,1,16,0\">bad</d><d p=\"2,1,16,0\">ok</d></i>", ".xml");
        Assert.Single(doc.Items); Assert.Equal(2, doc.Report.Invalid); Assert.Equal(1, doc.Report.Unsupported);
    }
    [Fact]
    public void XmlRejectsEntitiesAndWrongRoots()
    {
        Assert.Throws<XmlException>(() => DanmakuImporter.Parse("<!DOCTYPE i [<!ENTITY x SYSTEM 'file:///C:/secret'>]><i><d>&x;</d></i>", ".xml"));
        Assert.Throws<InvalidDataException>(() => DanmakuImporter.Parse("<other/>", ".xml"));
        Assert.Throws<XmlException>(() => DanmakuImporter.Parse("<i><d p=\"0,1,16,0\">bad", ".xml"));
    }
    [Fact]
    public void JsonUsesExplicitMillisecondsAndRetainsColorOpacity()
    {
        var doc = Json("""{"id":"a","time":1250,"text":"你好 🎉","mode":"reverse","color":"#123456","opacity":0.5} """);
        var item = Assert.Single(doc.Items); Assert.Equal(1250, item.StartMs); Assert.Equal("a", item.Id);
        Assert.Equal(DanmakuMode.Reverse, item.Mode); Assert.Equal(new DanmakuColor(0x12, 0x34, 0x56, 128), item.Color);
    }
    [Theory]
    [InlineData("{}")] [InlineData("[]")]
    [InlineData("{\"schema\":\"other\",\"timeUnit\":\"ms\",\"comments\":[]}")]
    [InlineData("{\"schema\":\"potdanmaku/v1\",\"timeUnit\":\"s\",\"comments\":[]}")]
    public void JsonRejectsAmbiguousSchemas(string text) => Assert.Throws<InvalidDataException>(() => DanmakuImporter.Parse(text, ".json"));
    [Fact]
    public void JsonReportsWrongTypesUnitsAndUnknownModes()
    {
        var doc = Json("""{"time":1.5,"text":"fraction"},{"time":"1000","text":"string"},{"time":1,"text":5},{"time":2,"text":"bad color","color":"red"},{"time":3,"text":"special","mode":"special"},{"time":4,"text":"ok"}""");
        Assert.Single(doc.Items); Assert.Equal(4, doc.Report.Invalid); Assert.Equal(1, doc.Report.Unsupported);
        Assert.ThrowsAny<JsonException>(() => DanmakuImporter.Parse("{", ".json"));
    }
    [Fact]
    public void TextLengthCountsUnicodeRunesAndNormalizesWhitespace()
    {
        var doc = Json(JsonSerializer.Serialize(new { time = 0, text = "\t" + string.Concat(Enumerable.Repeat("🎉", 500)) + "\n" }));
        Assert.Single(doc.Items); Assert.Equal(1000, doc.Items[0].Text.Length);
        Assert.Empty(Json(JsonSerializer.Serialize(new { time = 0, text = new string('中', 501) })).Items);
    }
    [Fact]
    public void AssReadsStyleColorAlphaMoveAndCommaText()
    {
        var doc = Ass("Dialogue: 0,0:01:02.34,0:01:08.34,S,,0,0,0,,{\\move(2000,30,-200,30,0,6000)}你好,世界\\N下一行");
        var item = Assert.Single(doc.Items); Assert.Equal(62340, item.StartMs, 5); Assert.Equal(68340, item.OriginalEndMs);
        Assert.Equal("你好,世界 下一行", item.Text); Assert.Equal(new DanmakuColor(0x11, 0x22, 0x33, 153), item.Color);
        Assert.Equal(DanmakuMode.Scroll, item.Mode); Assert.Equal(36, item.FontSizeRef);
    }
    [Fact]
    public void AssUsesLastInitialOverrideAndRecognizesFontNames()
    {
        var item = Assert.Single(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,S,,0,0,0,,{\\fnMicrosoft YaHei\\pos(960,10)\\c&HFF0000&\\c&H0000FF&\\alpha&H80&\\1a&H00&\\fs20\\fs25}文字").Items);
        Assert.Equal(new DanmakuColor(255, 0, 0, 255), item.Color); Assert.Equal(25, item.FontSizeRef); Assert.Equal(DanmakuMode.Top, item.Mode);
    }
    [Fact]
    public void AssSupportsReorderedFieldsAndTextNotLast()
    {
        var doc = Ass("Dialogue: 0:00:01.00,{\\pos(960,900)}甲,乙,0:00:05.00,S", "Start, Text, End, Style");
        Assert.Equal("甲,乙", Assert.Single(doc.Items).Text); Assert.Equal(DanmakuMode.Bottom, doc.Items[0].Mode);
    }
    [Fact]
    public void AssExcludesDialogueUnlessOptedIn()
    {
        const string dialogue = "Dialogue: 0,0:00:00.00,0:00:05.00,S,,0,0,0,,对白";
        Assert.Equal(1, Ass(dialogue).Report.Unsupported);
        Assert.Single(Ass(dialogue, options: new(IncludeUnpositionedAss: true)).Items);
    }
    [Theory]
    [InlineData("{\\move(1,2,5,8)}斜向")]
    [InlineData("{\\pos(1,2)\\t(0,1000,\\fs30)}特效")]
    [InlineData("{\\pos(1,2)\\p1}m 0 0 l 10 10")]
    [InlineData("{\\pos(1,2)}甲{\\c&HFF&}乙")]
    [InlineData("{\\pos(1,2)\\alpha&HFF&}透明")]
    public void AssReportsUnsupportedComplexEvents(string text)
    {
        var doc = Ass("Dialogue: 0,0:00:00.00,0:00:05.00,S,,0,0,0,," + text);
        Assert.Empty(doc.Items); Assert.Equal(1, doc.Report.Unsupported);
    }
    [Fact]
    public void AssTimerWarningDoesNotMultiplyEventTimeAndBadRecordsAreReported()
    {
        var doc = Ass("Dialogue: 0,0:00:02.00,0:00:08.00,S,,0,0,0,,{\\move(100,0,-100,0)}ok\nDialogue: 0,0:99:00.00,0:99:01.00,S,,0,0,0,,bad", timer: "10.0000");
        Assert.Equal(2000, Assert.Single(doc.Items).StartMs); Assert.Equal(1, doc.Report.Invalid);
        Assert.Equal(1, doc.Report.Counts["ass-timer"]);
        Assert.Throws<InvalidDataException>(() => DanmakuImporter.Parse("[Script Info]", ".ass"));
    }
    [Fact]
    public void LoadDecodesBomAndExplicitLegacyEncodingWithoutReplacementCharacters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        const string text = "<i><d p=\"0,1,16,0\">道友</d></i>";
        try
        {
            File.WriteAllText(path, text, Encoding.Unicode);
            Assert.Equal("道友", DanmakuImporter.Load(path).Items[0].Text);
            File.WriteAllBytes(path, Encoding.GetEncoding("gb18030").GetBytes(text));
            Assert.Throws<InvalidDataException>(() => DanmakuImporter.Load(path));
            Assert.Equal("道友", DanmakuImporter.Load(path, new("gb18030")).Items[0].Text);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void CancellationAndIssueLimitAreEnforced()
    {
        Assert.Throws<OperationCanceledException>(() => DanmakuImporter.Parse("<i/>", ".xml", cancellation: new CancellationToken(true)));
        var doc = DanmakuImporter.Parse("<i>" + string.Concat(Enumerable.Repeat("<d p=\"-1,1,16,0\">x</d>", 110)) + "</i>", ".xml");
        Assert.Equal(110, doc.Report.Invalid); Assert.Equal(100, doc.Report.Issues.Count);
    }
    private static DanmakuDocument Json(string records) => DanmakuImporter.Parse("{\"schema\":\"potdanmaku/v1\",\"timeUnit\":\"ms\",\"comments\":[" + records + "]}", ".json");
    private static DanmakuDocument Ass(string events, string format = "Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text", ImportOptions? options = null, string timer = "100.0000") => DanmakuImporter.Parse(
        "[Script Info]\nPlayResY: 1080\nTimer: " + timer + "\n[V4+ Styles]\nFormat: Name, Fontsize, PrimaryColour, Alignment\nStyle: S,36,&H66332211,8\n[Events]\nFormat: " + format + "\n" + events, ".ass", options: options);
}
