using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using PotDanmaku.Core;

namespace PotDanmaku.Formats;

public static class DanmakuImporter
{
    public const int MaxRecords = 500_000;
    public const long MaxBytes = 100 * 1024 * 1024;
    public static DanmakuDocument Load(string path, ImportOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new(); cancellation.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException("文件超过 100 MB 导入上限。");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        cancellation.ThrowIfCancellationRequested();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = new UTF8Encoding(false, true); int bom = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); bom = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); bom = 2; }
        else if (options.EncodingName != null) encoding = Encoding.GetEncoding(options.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        string text;
        try { text = encoding.GetString(bytes, bom, bytes.Length - bom); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("解码失败。请在编码选项中选择文件的实际编码（例如 GB18030）后重试。", ex); }
        return Parse(text, Path.GetExtension(path), Path.GetFullPath(path), options, encoding.WebName, cancellation);
    }

    public static DanmakuDocument Parse(string text, string extension, string path = "memory", ImportOptions? options = null,
        string encoding = "utf-8", CancellationToken cancellation = default)
    {
        options ??= new();
        string format = extension.ToLowerInvariant() switch { ".xml" => "Bilibili XML", ".json" => "PotDanmaku JSON v1", ".ass" => "ASS 弹幕重排", _ => throw new InvalidDataException("请选择 .xml、.json 或 .ass 文件。") };
        var report = new ReportBuilder(format, encoding); var items = new List<DanmakuItem>();
        switch (extension.ToLowerInvariant())
        {
            case ".xml": ParseXml(text, items, report, cancellation); break;
            case ".json": ParseJson(text, items, report, cancellation); break;
            case ".ass": AssParser.Parse(text, items, report, options, cancellation); break;
        }
        cancellation.ThrowIfCancellationRequested();
        var sorted = items.OrderBy(x => x.StartMs).ThenBy(x => x.SourceIndex).ToArray();
        return new(path, Array.AsReadOnly(sorted), report.Finish(sorted.Length));
    }
    internal static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    internal static void CheckCount(int total)
    {
        if (total > MaxRecords) throw new InvalidDataException("记录数超过 50 万条导入上限。");
    }
    internal static string Normalize(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("正文为空。");
        if (text.EnumerateRunes().Take(501).Count() > 500) throw new FormatException("正文超过 500 个 Unicode 字符。");
        if (text.Any(char.IsControl)) throw new FormatException("正文包含控制字符。");
        return text;
    }
    internal static void Validate(double time, double font, double opacity = 1)
    {
        if (!double.IsFinite(time) || time < 0 || time > 604800000) throw new FormatException("时间须为非负数且不超过 7 天。");
        if (!double.IsFinite(font) || font is <= 0 or > 1000) throw new FormatException("字号无效。");
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new FormatException("透明度须在 0～1 范围内。");
    }
    private static void ParseXml(string text, List<DanmakuItem> items, ReportBuilder report, CancellationToken token)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
        reader.MoveToContent();
        if (reader.Name != "i") throw new InvalidDataException("仅支持 B 站 <i><d p=\"...\"> 格式的弹幕 XML。");
        reader.Read();
        while (!reader.EOF)
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "d") { reader.Read(); continue; }
            int line = ((IXmlLineInfo)reader).LineNumber; int index = report.Total++; CheckCount(report.Total);
            string[] p = (reader.GetAttribute("p") ?? "").Split(',');
            string body = reader.ReadElementContentAsString();
            try
            {
                if (p.Length < 4) throw new FormatException("p 字段至少需要时间、类型、字号、颜色。");
                double time = Number(p[0]) * 1000, font = Number(p[2]); Validate(time, font);
                int mode = int.Parse(p[1], CultureInfo.InvariantCulture);
                DanmakuMode? mapped = mode switch { 1 or 2 or 3 => DanmakuMode.Scroll, 4 => DanmakuMode.Bottom, 5 => DanmakuMode.Top, 6 => DanmakuMode.Reverse, _ => null };
                if (mapped == null) { report.Skip(line, $"未支持 XML 类型 {mode}。"); continue; }
                uint rgb = uint.Parse(p[3], CultureInfo.InvariantCulture);
                if (rgb > 0xffffff) throw new FormatException("颜色超出 RGB 范围。");
                items.Add(new(p.Length > 7 ? p[7] : $"xml-{index}", index, time, Normalize(body), mapped.Value,
                    new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), (float)font));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { report.Bad(line, ex.Message); }
        }
    }

    private static void ParseJson(string text, List<DanmakuItem> items, ReportBuilder report, CancellationToken token)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != "potdanmaku/v1" ||
            !root.TryGetProperty("timeUnit", out var unit) || unit.ValueKind != JsonValueKind.String || unit.GetString() != "ms")
            throw new InvalidDataException("JSON 必须使用 schema=potdanmaku/v1、timeUnit=ms。其他导出器的 JSON 需要单独适配。");
        if (!root.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) throw new InvalidDataException("JSON 缺少 comments 数组。");
        foreach (var item in comments.EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); int index = report.Total++; CheckCount(report.Total);
            try
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException("记录不是对象。");
                if (!item.TryGetProperty("time", out var timeValue) || !timeValue.TryGetInt64(out long time)) throw new FormatException("time 须为整数毫秒。");
                double font = item.TryGetProperty("fontSize", out var f) ? f.GetDouble() : 16;
                double opacity = item.TryGetProperty("opacity", out var a) ? a.GetDouble() : 1;
                Validate(time, font, opacity);
                string mode = item.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "scroll";
                DanmakuMode? mapped = mode switch { "scroll" => DanmakuMode.Scroll, "reverse" => DanmakuMode.Reverse, "top" => DanmakuMode.Top, "bottom" => DanmakuMode.Bottom, _ => null };
                if (mapped == null) { report.Skip(index + 1, $"未支持 JSON mode={mode}。"); continue; }
                string color = item.TryGetProperty("color", out var c) ? c.GetString() ?? "" : "#FFFFFF";
                if (color.Length != 7 || color[0] != '#' || !uint.TryParse(color[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) throw new FormatException("color 须为 #RRGGBB。");
                items.Add(new(item.TryGetProperty("id", out var id) ? id.GetString() ?? $"json-{index}" : $"json-{index}", index,
                    time, Normalize(item.GetProperty("text").GetString() ?? ""), mapped.Value,
                    new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)Math.Round(opacity * 255)), (float)font));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or KeyNotFoundException) { report.Bad(index + 1, ex.Message); }
        }
    }
}
