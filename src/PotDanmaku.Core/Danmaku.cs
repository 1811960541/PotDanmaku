namespace PotDanmaku.Core;

public enum DanmakuMode { Scroll, Reverse, Top, Bottom }
public readonly record struct DanmakuColor(byte R, byte G, byte B, byte A = 255)
{
    public static DanmakuColor White => new(255, 255, 255);
}
public sealed record DanmakuItem(string Id, int SourceIndex, double StartMs, string Text,
    DanmakuMode Mode, DanmakuColor Color, float FontSizeRef = 16, double? OriginalEndMs = null);
public sealed record LayoutOptions(float FontSize = 16, float AreaFraction = 0.25f,
    double ScrollSeconds = 18, double FixedSeconds = 5, int MaxRows = 13,
    int MaxVisible = 200, int DensityPercent = 100, float Stroke = 0.6f)
{
    public void Validate()
    {
        if (!float.IsFinite(FontSize) || FontSize is < 8 or > 100 || !float.IsFinite(AreaFraction) || AreaFraction is < 0.1f or > 1 ||
            !double.IsFinite(ScrollSeconds) || ScrollSeconds is < 1 or > 60 || !double.IsFinite(FixedSeconds) || FixedSeconds is < 1 or > 60 ||
            MaxRows is < 1 or > 100 || MaxVisible is < 1 or > 1000 || DensityPercent is < 1 or > 100 || !float.IsFinite(Stroke) || Stroke is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(LayoutOptions));
    }
}
public readonly record struct TextSize(float Width, float Height);
public sealed record ScheduledItem(DanmakuItem Item, double StartMs, double EndMs, int Lane,
    float Width, float Height, float Y, float ViewportWidth)
{
    public float X(double timeMs)
    {
        double progress = (timeMs - StartMs) / (EndMs - StartMs);
        return Item.Mode switch {
            DanmakuMode.Scroll => (float)(ViewportWidth - (ViewportWidth + Width) * progress),
            DanmakuMode.Reverse => (float)(-Width + (ViewportWidth + Width) * progress),
            _ => (ViewportWidth - Width) / 2
        };
    }
}
public sealed record LayoutReport(int Accepted, int Crowded, int DensityFiltered, int TooWide, int Rows);

/// <summary>Immutable deterministic layout; seek queries never replay or burst old comments.</summary>
public sealed class DanmakuLayout
{
    private readonly ScheduledItem[] items;
    private readonly double maxDurationMs;
    public IReadOnlyList<ScheduledItem> Items { get; }
    public LayoutReport Report { get; }
    public LayoutOptions Options { get; }
    public float ViewportWidth { get; }
    public double EndMs { get; }
    public DanmakuLayout(IEnumerable<ScheduledItem> values, LayoutReport report, LayoutOptions options, float width)
    {
        items = values.ToArray(); Items = Array.AsReadOnly(items); Report = report; Options = options; ViewportWidth = width;
        maxDurationMs = items.Length == 0 ? 0 : items.Max(x => x.EndMs - x.StartMs);
        EndMs = items.Length == 0 ? 0 : items.Max(x => x.EndMs);
    }
    private int UpperBound(double time)
    {
        int lo = 0, hi = items.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (items[mid].StartMs <= time) lo = mid + 1; else hi = mid; }
        return lo;
    }
    public void GetActive(double timeMs, List<ScheduledItem> destination)
    {
        destination.Clear();
        int end = UpperBound(timeMs), start = UpperBound(timeMs - maxDurationMs);
        for (int i = start; i < end; i++) if (items[i].EndMs > timeMs) destination.Add(items[i]);
    }
}

public static class LayoutBuilder
{
    public static DanmakuLayout Build(IReadOnlyList<DanmakuItem> input, float viewportWidth,
        LayoutOptions options, Func<string, float, TextSize> measure, CancellationToken cancellation = default)
    {
        options.Validate();
        if (!float.IsFinite(viewportWidth) || viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        float top = 4, gap = Math.Max(4, options.FontSize * 0.35f) + 2 * options.Stroke;
        var metrics = new TextSize[input.Count];
        var cache = new Dictionary<string, TextSize>(StringComparer.Ordinal);
        float lineHeight = Math.Max(20, options.FontSize + options.Stroke * 2 + 4);
        for (int i = 0; i < input.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(input[i].Text, out var size))
            {
                size = measure(input[i].Text, options.FontSize);
                if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width < 0 || size.Height <= 0)
                    throw new InvalidOperationException("字体测量返回了无效尺寸。");
                cache[input[i].Text] = size;
            }
            metrics[i] = size; lineHeight = Math.Max(lineHeight, size.Height + options.Stroke * 2 + 4);
        }
        int rows = Math.Min(options.MaxRows, Math.Max(0, (int)((1080 * options.AreaFraction - top) / lineHeight)));
        var lanes = Enumerable.Range(0, rows).Select(_ => new List<ScheduledItem>()).ToArray();
        var scheduled = new List<ScheduledItem>();
        var activeEnds = new PriorityQueue<double, double>();
        int crowded = 0, filtered = 0, tooWide = 0;
        var order = Enumerable.Range(0, input.Count).OrderBy(i => input[i].StartMs).ThenBy(i => input[i].SourceIndex);
        foreach (int index in order)
        {
            cancellation.ThrowIfCancellationRequested();
            var item = input[index]; var size = metrics[index];
            // Stable filtering: changing seek direction does not change which comments are selected.
            if ((unchecked((uint)item.SourceIndex * 2654435761u) % 100) >= options.DensityPercent) { filtered++; continue; }
            if (size.Width > viewportWidth * 2 || (item.Mode is DanmakuMode.Top or DanmakuMode.Bottom && size.Width > viewportWidth - gap)) { tooWide++; continue; }
            while (activeEnds.TryPeek(out double end, out _) && end <= item.StartMs) activeEnds.Dequeue();
            if (activeEnds.Count >= options.MaxVisible) { crowded++; continue; }
            bool fixedMode = item.Mode is DanmakuMode.Top or DanmakuMode.Bottom;
            double duration = (fixedMode ? options.FixedSeconds : options.ScrollSeconds) * 1000;
            ScheduledItem? chosen = null;
            for (int attempt = 0; attempt < rows; attempt++)
            {
                int lane = item.Mode == DanmakuMode.Bottom ? rows - 1 - attempt : attempt;
                var occupants = lanes[lane]; occupants.RemoveAll(x => x.EndMs <= item.StartMs);
                var candidate = new ScheduledItem(item, item.StartMs, item.StartMs + duration, lane,
                    size.Width, size.Height, top + lane * lineHeight + options.Stroke, viewportWidth);
                if (occupants.All(x => CanShare(x, candidate, gap))) { chosen = candidate; occupants.Add(candidate); break; }
            }
            if (chosen == null) { crowded++; continue; }
            scheduled.Add(chosen); activeEnds.Enqueue(chosen.EndMs, chosen.EndMs);
        }
        return new(scheduled, new(scheduled.Count, crowded, filtered, tooWide, rows), options, viewportWidth);
    }

    public static bool CanShare(ScheduledItem a, ScheduledItem b, float gap)
    {
        double start = Math.Max(a.StartMs, b.StartMs), end = Math.Min(a.EndMs, b.EndMs);
        if (end <= start) return true;
        // Relative edge motion is linear, so its extrema lie at the overlap endpoints.
        // This covers faster long text, opposite directions, and fixed comments alike.
        double separationStart = b.X(start) - a.X(start), separationEnd = b.X(end) - a.X(end);
        return Math.Min(separationStart, separationEnd) >= a.Width + gap ||
               Math.Max(separationStart, separationEnd) <= -b.Width - gap;
    }
}
