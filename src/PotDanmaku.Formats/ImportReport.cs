using PotDanmaku.Core;

namespace PotDanmaku.Formats;

public sealed record ImportIssue(int Location, string Code, string Message);
public sealed record AssStyleSummary(string Name, int Total, int Accepted);
public sealed record ImportReport(string Format, int Total, int Accepted, int Invalid, int Unsupported,
    IReadOnlyList<ImportIssue> Issues, IReadOnlyDictionary<string, int> Counts, string Encoding,
    int Filtered = 0, IReadOnlyList<AssStyleSummary>? AssStyles = null);
public sealed record DanmakuDocument(string FilePath, IReadOnlyList<DanmakuItem> Items, ImportReport Report);
public sealed record ImportOptions(string? EncodingName = null, bool IncludeUnpositionedAss = false,
    IReadOnlySet<string>? SelectedAssStyles = null);

internal sealed class ReportBuilder(string format, string encoding)
{
    public int Total, Invalid, Unsupported, Filtered;
    private readonly Dictionary<string, int> styleTotals = new(StringComparer.OrdinalIgnoreCase), styleAccepted = new(StringComparer.OrdinalIgnoreCase);
    public void ObserveAssStyle(string name) => styleTotals[name] = styleTotals.GetValueOrDefault(name) + 1;
    public void AcceptAssStyle(string name) => styleAccepted[name] = styleAccepted.GetValueOrDefault(name) + 1;
    private readonly List<ImportIssue> issues = [];
    private readonly Dictionary<string, int> counts = [];
    public void Warn(int line, string code, string message)
    {
        counts[code] = counts.GetValueOrDefault(code) + 1;
        if (issues.Count < 100) issues.Add(new(line, code, message));
    }
    public void Bad(int line, string message) { Invalid++; Warn(line, "invalid", message); }
    public void Skip(int line, string message) { Unsupported++; Warn(line, "unsupported", message); }
    public ImportReport Finish(int accepted) => new(format, Total, accepted, Invalid, Unsupported,
        issues.AsReadOnly(), new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(counts), encoding, Filtered,
        styleTotals.Count == 0 ? null : Array.AsReadOnly(styleTotals.Select(s => new AssStyleSummary(s.Key, s.Value, styleAccepted.GetValueOrDefault(s.Key))).ToArray()));
}
