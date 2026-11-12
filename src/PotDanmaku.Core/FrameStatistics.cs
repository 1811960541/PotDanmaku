namespace PotDanmaku.Core;

public sealed record FrameSummary(int Count, double MeanIntervalMs, double P95IntervalMs,
    double MaxIntervalMs, double Over33MsPercent);

public static class FrameStatistics
{
    public static FrameSummary Summarize(IEnumerable<double> intervals)
    {
        var data = intervals.Where(x => double.IsFinite(x) && x >= 0).Order().ToArray();
        if (data.Length == 0) return new(0, 0, 0, 0, 0);
        return new(data.Length, data.Average(), data[(int)Math.Ceiling(data.Length * 0.95) - 1],
            data[^1], data.Count(x => x > 1000.0 / 30) * 100.0 / data.Length);
    }
}
