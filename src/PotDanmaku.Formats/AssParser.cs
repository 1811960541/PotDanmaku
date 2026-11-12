using System.Globalization;
using System.Text.RegularExpressions;
using PotDanmaku.Core;

namespace PotDanmaku.Formats;

internal static partial class AssParser
{
    private sealed record Style(DanmakuColor Color, float Size, int Alignment);
    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex Blocks();
    [GeneratedRegex(@"\\move\(([^)]*)\)", RegexOptions.IgnoreCase)] private static partial Regex Move();
    [GeneratedRegex(@"\\pos\(([^)]*)\)", RegexOptions.IgnoreCase)] private static partial Regex Position();
    [GeneratedRegex(@"\\an([1-9])")] private static partial Regex Align();
    [GeneratedRegex(@"\\(?:1?c)&H([0-9a-fA-F]{1,6})&?")] private static partial Regex Color();
    [GeneratedRegex(@"\\(?:alpha|1a)&H([0-9a-fA-F]{1,2})&?")] private static partial Regex Alpha();
    [GeneratedRegex(@"\\fs([0-9.]+)")] private static partial Regex FontSize();
    [GeneratedRegex(@"\\(?:(?:[1-4][ca])|(?:fn)|(?:[a-zA-Z]+))")] private static partial Regex Tags();
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    { "move", "pos", "an", "c", "1c", "alpha", "1a", "fs", "fn", "bord", "shad", "b", "i", "u", "s", "q", "2c", "3c", "4c", "2a", "3a", "4a" };

    public static void Parse(string text, List<DanmakuItem> items, ReportBuilder report, ImportOptions options, CancellationToken token)
    {
        var styles = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        string section = ""; string[] styleFields = [], eventFields = [];
        double playResY = 1080; bool generated = text.Contains("Bilibili Evolved Danmaku Converter", StringComparison.OrdinalIgnoreCase);
        bool foundEvents = false;
        using var reader = new StringReader(text); int lineNo = 0;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested(); lineNo++; line = line.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[')) { section = line; continue; }
            int colon = line.IndexOf(':'); if (colon < 0) continue;
            string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (section.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double res) && res > 0) playResY = res;
                if (key.Equals("Timer", StringComparison.OrdinalIgnoreCase) && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double timer) || timer != 100))
                    report.Warn(lineNo, "ass-timer", $"Timer={value}。本工具按 Events 时间戳导入，不应用 Timer 缩放，请核对视频时间轴。");
                continue;
            }
            if (section.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Format", StringComparison.OrdinalIgnoreCase)) { styleFields = Fields(value); continue; }
                if (!key.Equals("Style", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var fields = Map(styleFields, value);
                    double font = DanmakuImporter.Number(Get(fields, "Fontsize"));
                    if (!double.IsFinite(font) || font is <= 0 or > 1000) throw new FormatException("字号无效。");
                    styles[Get(fields, "Name")] = new(ParseColor(Get(fields, "PrimaryColour")), (float)font,
                        int.Parse(Get(fields, "Alignment"), CultureInfo.InvariantCulture));
                }
                catch (Exception ex) when (ex is FormatException or OverflowException) { report.Warn(lineNo, "ass-style", "样式无效：" + ex.Message); }
                continue;
            }
            if (!section.Equals("[Events]", StringComparison.OrdinalIgnoreCase)) continue;
            foundEvents = true;
            if (key.Equals("Format", StringComparison.OrdinalIgnoreCase)) { eventFields = Fields(value); continue; }
            if (!key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase)) continue;
            int index = report.Total++; DanmakuImporter.CheckCount(report.Total);
            try
            {
                var fields = Map(eventFields, value, true);
                string styleName = Get(fields, "Style"); report.ObserveAssStyle(styleName);
                if (options.SelectedAssStyles != null && !options.SelectedAssStyles.Contains(styleName)) { report.Filtered++; continue; }
                double start = ParseTime(Get(fields, "Start")), end = ParseTime(Get(fields, "End"));
                if (end <= start) throw new FormatException("结束时间必须晚于开始时间。");
                string body = Get(fields, "Text");
                if (body.Length > 16000) throw new FormatException("事件正文过长。");
                var style = styles.GetValueOrDefault(styleName, new(DanmakuColor.White, 16, 2));
                string effect = fields.GetValueOrDefault("Effect", "");
                if (!string.IsNullOrWhiteSpace(effect)) { report.Skip(lineNo, "未支持 Effect 字段。"); continue; }
                var blocks = Blocks().Matches(body);
                bool complex = blocks.Any(block => Tags().Matches(block.Value).Any(tag => !AllowedTags.Contains(tag.Value[1..])));
                // Mid-text overrides would require per-run styling; don't silently flatten their colors.
                if (complex || blocks.Any(b => b.Value.Contains('\\') && !string.IsNullOrWhiteSpace(Blocks().Replace(body[..b.Index], ""))))
                { report.Skip(lineNo, "复杂 ASS 标签或内联分段样式未支持。"); continue; }
                string tags = string.Join("", blocks.Select(x => x.Value));
                int alignment = Align().Matches(tags).LastOrDefault() is { Success: true } align ? int.Parse(align.Groups[1].Value, CultureInfo.InvariantCulture) : style.Alignment;
                DanmakuMode mode;
                var move = Move().Match(tags); var position = Position().Match(tags);
                if (move.Success)
                {
                    var coords = move.Groups[1].Value.Split(',').Select(DanmakuImporter.Number).ToArray();
                    if (coords.Length is not (4 or 6) || coords.Any(x => !double.IsFinite(x))) throw new FormatException("move 参数无效。");
                    if (Math.Abs(coords[1] - coords[3]) > 0.1 || Math.Abs(coords[0] - coords[2]) < 0.1)
                    { report.Skip(lineNo, "只支持水平滚动 move。"); continue; }
                    if (coords.Length == 6 && (coords[4] < 0 || coords[5] < coords[4])) throw new FormatException("move 运动时间无效。");
                    mode = coords[0] > coords[2] ? DanmakuMode.Scroll : DanmakuMode.Reverse;
                }
                else if (position.Success)
                {
                    var coords = position.Groups[1].Value.Split(',').Select(DanmakuImporter.Number).ToArray();
                    if (coords.Length != 2 || coords.Any(x => !double.IsFinite(x))) throw new FormatException("pos 参数无效。");
                    mode = coords[1] < playResY / 2 ? DanmakuMode.Top : DanmakuMode.Bottom;
                }
                else if (generated || options.IncludeUnpositionedAss || options.SelectedAssStyles != null)
                    mode = alignment >= 7 ? DanmakuMode.Top : DanmakuMode.Bottom;
                else { report.Skip(lineNo, "没有 move/pos 的事件默认作为普通对白排除；可开启导入普通 ASS 字幕。"); continue; }

                var color = style.Color;
                var colorMatch = Color().Matches(tags).LastOrDefault();
                if (colorMatch is { Success: true }) color = ParseColor("&H" + colorMatch.Groups[1].Value) with { A = color.A };
                var alphaMatch = Alpha().Matches(tags).LastOrDefault();
                if (alphaMatch is { Success: true }) color = color with { A = (byte)(255 - byte.Parse(alphaMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)) };
                float font = style.Size;
                var fontMatch = FontSize().Matches(tags).LastOrDefault(); if (fontMatch is { Success: true }) font = (float)DanmakuImporter.Number(fontMatch.Groups[1].Value);
                DanmakuImporter.Validate(start, font);
                string plain = Blocks().Replace(body, "").Replace(@"\N", " ").Replace(@"\n", " ").Replace(@"\h", " ");
                if (plain.Contains('\\')) { report.Skip(lineNo, "正文中有未支持的 ASS 转义。"); continue; }
                if (color.A == 0) { report.Skip(lineNo, "完全透明事件已跳过。"); continue; }
                items.Add(new($"ass-{index}", index, start, DanmakuImporter.Normalize(plain), mode, color, font, end));
                report.AcceptAssStyle(styleName);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { report.Bad(lineNo, ex.Message); }
        }
        if (!foundEvents) throw new InvalidDataException("ASS 缺少 [Events] 部分。");
        report.Warn(0, "ass-reflow", "ASS 按本工具的区域、字号和时长重新排布。原坐标、结束时间、字体与特效不会原样复现。");
    }

    private static string[] Fields(string line) => line.Split(',').Select(x => x.Trim()).ToArray();
    private static Dictionary<string, string> Map(string[] names, string value, bool hasText = false)
    {
        if (names.Length == 0) throw new FormatException("缺少 Format 行。");
        int textIndex = Array.FindIndex(names, x => x.Equals("Text", StringComparison.OrdinalIgnoreCase));
        string[] values;
        if (hasText)
        {
            if (textIndex < 0) throw new FormatException("Format 缺少 Text 字段。");
            // Split known fields from both ends, leaving all literal commas inside Text.
            values = new string[names.Length]; string remainder = value;
            for (int i = 0; i < textIndex; i++) { int comma = remainder.IndexOf(','); if (comma < 0) throw new FormatException("字段不足。"); values[i] = remainder[..comma].Trim(); remainder = remainder[(comma + 1)..]; }
            for (int i = names.Length - 1; i > textIndex; i--) { int comma = remainder.LastIndexOf(','); if (comma < 0) throw new FormatException("字段不足。"); values[i] = remainder[(comma + 1)..].Trim(); remainder = remainder[..comma]; }
            values[textIndex] = remainder;
        }
        else { values = value.Split(','); if (values.Length != names.Length) throw new FormatException("字段数量不一致。"); }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++) if (!result.TryAdd(names[i], values[i])) throw new FormatException("Format 存在重复字段。");
        return result;
    }
    private static string Get(Dictionary<string, string> values, string key) => values.TryGetValue(key, out string? value) ? value : throw new FormatException($"缺少 {key} 字段。");
    private static double ParseTime(string value)
    {
        var p = value.Split(':');
        if (p.Length != 3) throw new FormatException("时间须为 h:mm:ss.cc。");
        int hours = int.Parse(p[0], CultureInfo.InvariantCulture), minutes = int.Parse(p[1], CultureInfo.InvariantCulture);
        double seconds = DanmakuImporter.Number(p[2]);
        if (hours < 0 || minutes is < 0 or > 59 || !double.IsFinite(seconds) || seconds is < 0 or >= 60) throw new FormatException("时间无效。");
        double time = (hours * 3600.0 + minutes * 60 + seconds) * 1000; DanmakuImporter.Validate(time, 16); return time;
    }
    private static DanmakuColor ParseColor(string text)
    {
        string hex = text.Trim().Replace("&H", "", StringComparison.OrdinalIgnoreCase).TrimEnd('&');
        if (hex.Length > 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) throw new FormatException("ASS 颜色无效。");
        return new((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(255 - (value >> 24)));
    }
}
