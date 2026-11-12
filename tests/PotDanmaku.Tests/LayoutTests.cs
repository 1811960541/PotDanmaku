using PotDanmaku.Core;

namespace PotDanmaku.Tests;

public class LayoutTests
{
    private static TextSize Measure(string text, float size) => new(text.Length * size, size * 1.3f);
    private static DanmakuItem Item(int id, double ms, string text = "甲乙", DanmakuMode mode = DanmakuMode.Scroll) => new($"id-{id}", id, ms, text, mode, DanmakuColor.White);
    private static DanmakuLayout Build(params DanmakuItem[] items) => LayoutBuilder.Build(items, 1920, new(MaxRows: 1), Measure);
    [Fact]
    public void LongFastFollowerCannotCatchShortLeader()
    {
        var layout = Build(Item(0, 0, "甲"), Item(1, 2000, new string('乙', 80)));
        Assert.Single(layout.Items); Assert.Equal(1, layout.Report.Crowded);
        var a = new ScheduledItem(Item(0, 0), 0, 18000, 0, 16, 20, 0, 1920);
        var b = new ScheduledItem(Item(1, 2000), 2000, 20000, 0, 1280, 20, 0, 1920);
        Assert.True(b.X(b.StartMs) > a.X(b.StartMs) + a.Width);
        Assert.False(LayoutBuilder.CanShare(a, b, 5));
    }
    [Fact]
    public void EquallyWideCommentsCanShareLaneWithSafeGap()
    {
        Assert.Equal(2, Build(Item(0, 0), Item(1, 1000)).Items.Count);
        Assert.Single(Build(Item(0, 0), Item(1, 100)).Items);
    }
    [Fact]
    public void OpposingAndFixedCommentsReserveSharedRows()
    {
        Assert.Single(Build(Item(0, 0), Item(1, 2000, mode: DanmakuMode.Reverse)).Items);
        Assert.Single(Build(Item(0, 0, mode: DanmakuMode.Top), Item(1, 0, mode: DanmakuMode.Bottom)).Items);
        Assert.Equal(2, Build(Item(0, 0, mode: DanmakuMode.Top), Item(1, 5000, mode: DanmakuMode.Bottom)).Items.Count);
    }
    [Fact]
    public void TopAndBottomOccupyOppositeEndsInsideArea()
    {
        var layout = LayoutBuilder.Build([Item(0, 0, mode: DanmakuMode.Top), Item(1, 0, mode: DanmakuMode.Bottom)], 1920, new(), Measure);
        Assert.Equal(0, layout.Items[0].Lane); Assert.Equal(layout.Report.Rows - 1, layout.Items[1].Lane);
        Assert.All(layout.Items, x => Assert.True(x.Y + x.Height + layout.Options.Stroke <= 270));
    }
    [Fact]
    public void SeekQueryMatchesIndependentFullScanInBothDirections()
    {
        var items = Enumerable.Range(0, 300).Select(i => Item(i, i * 450, mode: (DanmakuMode)(i % 4))).ToArray();
        var layout = LayoutBuilder.Build(items, 1920, new(), Measure); var active = new List<ScheduledItem>();
        foreach (double ms in new double[] { 0, 5000, 80000, 20000, -1, 18000, 134550, layout.EndMs, 0 })
        {
            layout.GetActive(ms, active);
            Assert.Equal(layout.Items.Where(x => x.StartMs <= ms && x.EndMs > ms).Select(x => x.Item.Id), active.Select(x => x.Item.Id));
        }
    }
    [Fact]
    public void DensityAndTieOrderingAreDeterministic()
    {
        var items = Enumerable.Range(0, 500).Select(i => Item(i, i / 4 * 300)).ToArray();
        var options = new LayoutOptions(DensityPercent: 35);
        var a = LayoutBuilder.Build(items, 1920, options, Measure);
        var b = LayoutBuilder.Build(items.Reverse().ToArray(), 1920, options, Measure);
        Assert.Equal(a.Report, b.Report); Assert.Equal(a.Items, b.Items); Assert.True(a.Report.DensityFiltered > 250);
    }
    [Fact]
    public void GlobalCapAndWideTextAreReported()
    {
        var options = new LayoutOptions(MaxVisible: 2);
        var layout = LayoutBuilder.Build([Item(0, 0), Item(1, 0), Item(2, 0), Item(3, 0, new string('甲', 300))], 1920, options, Measure);
        Assert.Equal(2, layout.Report.Accepted); Assert.Equal(1, layout.Report.Crowded); Assert.Equal(1, layout.Report.TooWide);
    }
    [Fact]
    public void IdenticalTextIsMeasuredOnceAndTallFallbackRespectsArea()
    {
        int calls = 0;
        var layout = LayoutBuilder.Build([Item(0, 0), Item(1, 0), Item(2, 0)], 1920, new(), (_, _) => { calls++; return new(32, 100); });
        Assert.Equal(1, calls); Assert.Equal(2, layout.Report.Rows); Assert.Equal(2, layout.Report.Accepted);
    }
    [Fact]
    public void DenseMixedLayoutHasNoIntersectionsAndRebuildsIdentically()
    {
        var random = new Random(42);
        var input = Enumerable.Range(0, 2000).Select(i => Item(i, random.Next(0, 180000), new string('甲', random.Next(1, 90)), (DanmakuMode)random.Next(4))).ToArray();
        var layout = LayoutBuilder.Build(input, 1920, new(), Measure);
        foreach (var group in layout.Items.GroupBy(x => x.Lane))
        {
            var lane = group.ToArray();
            for (int i = 0; i < lane.Length; i++)
                for (int j = i + 1; j < lane.Length && lane[j].StartMs < lane[i].EndMs; j++)
                {
                    double start = Math.Max(lane[i].StartMs, lane[j].StartMs), end = Math.Min(lane[i].EndMs, lane[j].EndMs);
                    foreach (double time in new[] { start, (start + end) / 2, Math.BitDecrement(end) })
                    {
                        // Check actual rectangles independently of the placement predicate.
                        double leftA = lane[i].X(time), rightA = leftA + lane[i].Width;
                        double leftB = lane[j].X(time), rightB = leftB + lane[j].Width;
                        Assert.True(rightA + 6.79 <= leftB || rightB + 6.79 <= leftA, $"Overlap at {time}: {lane[i].Item.Id}, {lane[j].Item.Id}");
                    }
                }
        }
        Assert.Equal(input.Length, layout.Report.Accepted + layout.Report.Crowded + layout.Report.DensityFiltered + layout.Report.TooWide);
        Assert.Equal(layout.Items, LayoutBuilder.Build(input, 1920, new(), Measure).Items);
    }
    [Fact]
    public void InvalidMetricsOptionsAndCancellationFailExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutBuilder.Build([], float.NaN, new(), Measure));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutBuilder.Build([], 1920, new(DensityPercent: 0), Measure));
        Assert.Throws<InvalidOperationException>(() => LayoutBuilder.Build([Item(0, 0)], 1920, new(), (_, _) => new(float.NaN, 20)));
        Assert.Throws<OperationCanceledException>(() => LayoutBuilder.Build([Item(0, 0)], 1920, new(), Measure, new CancellationToken(true)));
    }
}
