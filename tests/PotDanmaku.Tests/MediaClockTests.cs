using PotDanmaku.Core;
using System.Text.Json;

namespace PotDanmaku.Tests;

public class MediaClockTests
{
    private sealed record RecordedPosition(double ObservedSeconds, double PositionMs);
    [Theory]
    [InlineData("potplayer-cached-progress.json", 1.0)]
    [InlineData("potplayer-3x-progress.json", 3.0)]
    public void ActualPotPlayerCachedReadingsReplayWithoutFreezesOrFalseSeeks(string fixture, double rate)
    {
        var readings = JsonSerializer.Deserialize<RecordedPosition[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)))!;
        Assert.True(readings.Length > 50);
        int next = 0, checkedFrames = 0; double previous = 0;
        var clock = new MediaClock();
        int frameCount = (int)((readings[^1].ObservedSeconds + 0.08) * 160) + 1;
        for (int frame = 0; frame < frameCount; frame++)
        {
            double now = frame / 160.0; bool started = next > 0;
            while (next < readings.Length && readings[next].ObservedSeconds + 0.08 <= now)
            {
                var reading = readings[next++];
                clock.Update(new(reading.PositionMs, reading.ObservedSeconds, PlaybackState.Playing, rate));
            }
            double current = clock.Read(now);
            if (started) { Assert.InRange(current - previous, (rate - 0.101) * 6.25, (rate + 0.101) * 6.25); checkedFrames++; }
            Assert.False(clock.IsStalled);
            previous = current;
        }
        Assert.True(checkedFrames > 1000); Assert.Equal(0, clock.Discontinuities);
    }
    [Theory]
    [InlineData(0.5)] [InlineData(1.0)] [InlineData(1.5)] [InlineData(2.0)] [InlineData(3.0)]
    public void DelayedPlayerSamplesDoNotBurstOrCreateFalseSeeks(double rate)
    {
        var clock = new MediaClock(); double previous = 0;
        const double deliveryDelay = 0.08; // Poll + UI delivery is later than the measurement timestamp.
        for (int frame = 0; frame < 1600; frame++)
        {
            double observed = frame / 160.0, now = observed + deliveryDelay;
            if (frame % 8 == 0)
                clock.Update(new(Math.Floor(observed * rate * 25) * 40, observed, PlaybackState.Playing, rate));
            double current = clock.Read(now);
            if (frame > 0)
                Assert.InRange(current - previous, (rate - 0.101) * 6.25, (rate + 0.101) * 6.25);
            Assert.InRange(Math.Abs(current - now * rate * 1000), 0, 80);
            previous = current;
        }
        Assert.Equal(0, clock.Discontinuities);
    }
    [Fact]
    public void JitteredAndOutOfOrderDeliveryKeepsContinuousMotion()
    {
        int[] delay = [5, 21, 11, 16];
        var deliveries = Enumerable.Range(0, 200).Select(i => new {
            Frame = i * 8 + delay[i % delay.Length],
            Sample = new PlaybackSample(Math.Floor(i * 0.05 * 25) * 40, i * 0.05, PlaybackState.Playing, 1)
        }).GroupBy(x => x.Frame).ToDictionary(x => x.Key, x => x.Select(y => y.Sample).ToArray());
        var clock = new MediaClock(); double previous = 0; bool started = false;
        for (int frame = 0; frame < 1600; frame++)
        {
            bool wasStarted = started;
            if (deliveries.TryGetValue(frame, out var samples)) { foreach (var sample in samples) clock.Update(sample); started = true; }
            double current = clock.Read(frame / 160.0);
            if (wasStarted) Assert.InRange(current - previous, 5.61, 6.89);
            if (started) Assert.InRange(Math.Abs(current - frame / 160.0 * 1000), 0, 80);
            previous = current;
        }
        Assert.Equal(0, clock.Discontinuities);
    }
    [Fact]
    public void DelayedRateChangeChangesSlopeWithoutRebasingThePosition()
    {
        var clock = new MediaClock(); clock.Update(new(1000, 1, PlaybackState.Playing, 1));
        double previous = clock.Read(1.18);
        clock.Update(new(1100, 1.1, PlaybackState.Playing, 2));
        Assert.Equal(previous + 12.5, clock.Read(1.18625), 5);
        Assert.Equal(0, clock.Discontinuities);
    }
    [Theory]
    [InlineData(0.5)] [InlineData(1.0)] [InlineData(1.5)] [InlineData(2.0)] [InlineData(3.0)]
    public void HalfSecondCachedPositionsDoNotFreezeOrCauseFalseSeeks(double rate)
    {
        var clock = new MediaClock(); double previous = 0;
        for (int frame = 0; frame < 3200; frame++)
        {
            double observed = frame / 160.0, now = observed + 0.08;
            if (frame % 8 == 0)
                clock.Update(new(Math.Floor(observed / 0.5) * 500 * rate, observed, PlaybackState.Playing, rate));
            double current = clock.Read(now);
            if (frame > 0) Assert.InRange(current - previous, (rate - 0.101) * 6.25, (rate + 0.101) * 6.25);
            Assert.False(clock.IsStalled);
            previous = current;
        }
        Assert.Equal(0, clock.Discontinuities);
    }
    [Theory]
    [InlineData(1.0, 0.3)] [InlineData(3.0, 0.1)] [InlineData(3.0, 0.3)] [InlineData(3.0, 0.49)]
    public void ConnectionHalfwayThroughCachedIntervalDoesNotDeclareSeek(double rate, double phase)
    {
        var clock = new MediaClock(); double previous = 0;
        for (int frame = 0; frame < 1600; frame++)
        {
            double observed = phase + frame / 160.0;
            if (frame % 8 == 0) clock.Update(new(Math.Floor(observed / 0.5) * 500 * rate, observed, PlaybackState.Playing, rate));
            double current = clock.Read(observed + 0.06);
            if (frame > 0) Assert.InRange(current - previous, (rate - 0.101) * 6.25, (rate + 0.101) * 6.25);
            previous = current;
        }
        Assert.Equal(0, clock.Discontinuities);
    }
    [Theory]
    [InlineData(1, 3)] [InlineData(3, 1)] [InlineData(0.5, 3)] [InlineData(3, 0.5)]
    public void RateSwitchBetweenCachedMeasurementsRemainsContinuous(double before, double after)
    {
        var clock = new MediaClock(); double previous = 0;
        const double switchTime = 2.3;
        for (int frame = 0; frame < 1600; frame++)
        {
            double observed = frame / 160.0, cachedTime = Math.Floor(observed / 0.5) * 0.5;
            double position = Math.Min(cachedTime, switchTime) * before * 1000 + Math.Max(0, cachedTime - switchTime) * after * 1000;
            if (frame % 8 == 0) clock.Update(new(position, observed, PlaybackState.Playing, observed < switchTime ? before : after));
            double current = clock.Read(observed + 0.08);
            if (frame > 0)
                Assert.InRange(current - previous, (Math.Min(before, after) - 0.101) * 6.25, (Math.Max(before, after) + 0.101) * 6.25);
            previous = current; Assert.False(clock.IsStalled);
        }
        Assert.Equal(0, clock.Discontinuities);
    }
    [Fact]
    public void TripleSpeedPauseSeekAndResumeApplyImmediately()
    {
        var clock = new MediaClock();
        clock.Update(new(3000, 1, PlaybackState.Playing, 3));
        Assert.Equal(3300, clock.Read(1.1), 5);
        clock.Update(new(3300, 1.1, PlaybackState.Paused, 3));
        Assert.Equal(3300, clock.Read(5), 5);
        clock.Update(new(90000, 5, PlaybackState.Paused, 3));
        Assert.Equal(90000, clock.Read(5), 5);
        clock.Update(new(90000, 5.1, PlaybackState.Playing, 3));
        Assert.Equal(90300, clock.Read(5.2), 5);
        clock.Update(new(2000, 5.3, PlaybackState.Playing, 3));
        Assert.Equal(2000, clock.Read(5.3), 5);
        clock.Update(new(60000, 5.4, PlaybackState.Playing, 3));
        Assert.Equal(60000, clock.Read(5.4), 5);
    }
    [Fact]
    public void VideoFrameQuantizationDoesNotReverseClock()
    {
        var clock = new MediaClock(); double previous = 0;
        for (int frame = 0; frame < 1600; frame++)
        {
            double t = frame / 160.0;
            if (frame % 8 == 0)
                clock.Update(new(Math.Floor(t * 25) * 40, t, PlaybackState.Playing, 1));
            double current = clock.Read(t);
            Assert.True(current >= previous, $"Clock reversed at frame {frame}");
            Assert.InRange(Math.Abs(current - t * 1000), 0, 80);
            previous = current;
        }
        Assert.Equal(0, clock.Discontinuities);
    }

    [Fact]
    public void PauseFreezesAndResumeContinues()
    {
        var clock = new MediaClock();
        clock.Update(new(1000, 10, PlaybackState.Playing, 1));
        clock.Update(new(1100, 10.1, PlaybackState.Paused, 1));
        Assert.Equal(1100, clock.Read(20), 5);
        clock.Update(new(1100, 20, PlaybackState.Playing, 1));
        Assert.Equal(1200, clock.Read(20.1), 5);
    }

    [Theory]
    [InlineData(0.5, false)] [InlineData(1.0, false)] [InlineData(3.0, false)]
    [InlineData(0.5, true)] [InlineData(1.0, true)] [InlineData(3.0, true)]
    public void PauseKeepsLastRenderedPositionWhenPlayerPositionIsCached(double rate, bool refreshedPosition)
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Playing, rate));
        clock.Read(1.35);
        clock.Update(new(10000, 1.4, PlaybackState.Playing, rate));
        double lastFrame = clock.Read(1.48);
        // The pause reply arrives after this frame, with an older measurement timestamp.
        double rawPause = 10000 + (refreshedPosition ? 420 * rate : 0);
        clock.Update(new(rawPause, 1.45, PlaybackState.Paused, rate));
        Assert.Equal(lastFrame, clock.Read(1.49), 5);
        for (int i = 1; i <= 30; i++)
        {
            clock.Update(new(rawPause, 1.45 + i * 0.05, PlaybackState.Paused, rate));
            Assert.Equal(lastFrame, clock.Read(1.49 + i * 0.05), 5);
        }
        Assert.Equal(0, clock.Discontinuities);
    }

    [Theory]
    [InlineData(1.0)] [InlineData(3.0)]
    public void DelayedResumeDoesNotReplayDeliveryDelay(double rate)
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Paused, rate));
        double frozen = clock.Read(5.08);
        clock.Update(new(10000, 5, PlaybackState.Playing, rate));
        Assert.Equal(frozen + 6.25 * rate, clock.Read(5.08625), 5);
        Assert.Equal(0, clock.Discontinuities);
    }

    [Theory]
    [InlineData(1.0)] [InlineData(3.0)]
    public void LateFinalPausePositionKeepsFrameThenSmallSeeksWorkInBothDirections(double rate)
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Playing, rate));
        clock.Update(new(10000, 1.4, PlaybackState.Playing, rate));
        double frozen = clock.Read(1.48);
        clock.Update(new(10000, 1.5, PlaybackState.Paused, rate));
        Assert.Equal(frozen, clock.Read(1.58));
        double finalPause = 10000 + 430 * rate;
        Assert.False(clock.Update(new(finalPause, 1.9, PlaybackState.Paused, rate)));
        Assert.Equal(frozen, clock.Read(1.98));
        Assert.Equal(0, clock.Discontinuities);
        clock.Update(new(finalPause + 40, 2, PlaybackState.Paused, rate));
        Assert.Equal(finalPause + 40, clock.Read(2.08));
        clock.Update(new(finalPause, 2.1, PlaybackState.Paused, rate));
        Assert.Equal(finalPause, clock.Read(2.18));
        Assert.Equal(2, clock.Discontinuities);
    }

    [Theory]
    [InlineData(-40, 1.6)] [InlineData(5000, 1.6)] [InlineData(40, 2.3)]
    public void PendingPauseRefreshDoesNotSuppressBackwardLargeOrLaterSeeks(double delta, double observed)
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Playing, 3));
        clock.Update(new(10000, 1.4, PlaybackState.Playing, 3)); clock.Read(1.48);
        clock.Update(new(10000, 1.5, PlaybackState.Paused, 3)); clock.Read(1.58);
        Assert.True(clock.Update(new(10000 + delta, observed, PlaybackState.Paused, 3)));
        Assert.Equal(10000 + delta, clock.Read(observed + 0.08));
        Assert.Equal(1, clock.Discontinuities);
    }

    [Fact]
    public void ResumeCacheRefreshDoesNotMisinterpretPreservedPositionAsASeek()
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Playing, 3));
        clock.Update(new(10000, 1.4, PlaybackState.Playing, 3));
        double frozen = clock.Read(1.48);
        clock.Update(new(10000, 1.5, PlaybackState.Paused, 3)); clock.Read(1.58);
        clock.Update(new(10078, 1.9, PlaybackState.Paused, 3));
        Assert.Equal(frozen, clock.Read(2.31));
        clock.Update(new(10078, 2.32, PlaybackState.Playing, 3));
        double previous = clock.Read(2.48);
        Assert.False(clock.Update(new(10160, 2.45, PlaybackState.Playing, 3)));
        Assert.InRange(clock.Read(2.48625) - previous, 18.125 - 0.001, 18.75 + 0.001);
        Assert.Equal(0, clock.Discontinuities);
    }

    [Theory]
    [InlineData(1.0)] [InlineData(3.0)]
    public void PauseRefreshStillAllowsHalfSecondCacheAfterShortProgressIntervals(double rate)
    {
        var clock = new MediaClock();
        for (int i = 0; i <= 20; i++)
        {
            clock.Update(new(10000 + i * 50 * rate, 1 + i * 0.05, PlaybackState.Playing, rate));
            clock.Read(1.08 + i * 0.05);
        }
        double cached = 10000 + 1000 * rate;
        clock.Update(new(cached, 2.4, PlaybackState.Playing, rate));
        double frozen = clock.Read(2.48);
        clock.Update(new(cached, 2.5, PlaybackState.Paused, rate)); clock.Read(2.58);
        clock.Update(new(cached + 480 * rate, 2.7, PlaybackState.Paused, rate));
        Assert.Equal(frozen, clock.Read(2.78));
        Assert.Equal(0, clock.Discontinuities);
    }

    [Fact]
    public void FinalPauseRefreshCanFollowAnAlreadyChangedInitialPauseReply()
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Playing, 1));
        clock.Update(new(10000, 1.4, PlaybackState.Playing, 1));
        double frozen = clock.Read(1.48);
        clock.Update(new(10500, 1.5, PlaybackState.Paused, 1)); clock.Read(1.58);
        clock.Update(new(10516, 2, PlaybackState.Paused, 1));
        Assert.Equal(frozen, clock.Read(2.08));
        clock.Update(new(10556, 2.1, PlaybackState.Paused, 1));
        Assert.Equal(10556, clock.Read(2.18));
        Assert.Equal(1, clock.Discontinuities);
    }

    [Fact]
    public void PauseNearCommentBoundaryKeepsTheSameCommentsAndCoordinates()
    {
        var clock = new MediaClock();
        var old = new DanmakuItem("expired", 0, 0, "old", DanmakuMode.Scroll, DanmakuColor.White);
        var current = new DanmakuItem("visible", 1, 10050, "new", DanmakuMode.Scroll, DanmakuColor.White);
        var layout = new DanmakuLayout([
            new(old, 0, 10030, 0, 40, 20, 0, 1920),
            new(current, 10050, 18000, 1, 40, 20, 24, 1920)
        ], new(2, 0, 0, 0, 2), new(), 1920);
        var active = new List<ScheduledItem>();
        clock.Update(new(10000, 1, PlaybackState.Playing, 1));
        clock.Update(new(10000, 1.05, PlaybackState.Playing, 1));
        double before = clock.Read(1.08);
        layout.GetActive(before, active);
        Assert.Equal("visible", Assert.Single(active).Item.Id);
        float x = active[0].X(before);
        foreach (var sample in new PlaybackSample[] {
            new(10000, 1.1, PlaybackState.Paused, 1), new(10070, 1.5, PlaybackState.Paused, 1)
        })
        {
            clock.Update(sample);
            double paused = clock.Read(sample.ObservedSeconds + 0.08);
            layout.GetActive(paused, active);
            Assert.Equal("visible", Assert.Single(active).Item.Id);
            Assert.Equal(x, active[0].X(paused));
        }
    }

    [Theory]
    [InlineData(2000)] [InlineData(90000)]
    public void SeekTogetherWithPauseStillAppliesImmediately(double target)
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Playing, 3)); clock.Read(1.08);
        clock.Update(new(target, 1.1, PlaybackState.Paused, 3));
        Assert.Equal(target, clock.Read(1.18));
        Assert.Equal(1, clock.Discontinuities);
    }

    [Fact]
    public void SeekBackwardStartsNewTimeSegment()
    {
        var clock = new MediaClock();
        clock.Update(new(10000, 1, PlaybackState.Playing, 1)); clock.Read(1.05);
        clock.Update(new(2000, 1.1, PlaybackState.Playing, 1));
        Assert.Equal(2000, clock.Read(1.1)); Assert.Equal(1, clock.Discontinuities);
    }

    [Fact]
    public void SmallSeekWhilePausedIsAppliedImmediately()
    {
        var clock = new MediaClock();
        clock.Update(new(1000, 1, PlaybackState.Paused, 1));
        clock.Update(new(1040, 1.05, PlaybackState.Paused, 1));
        Assert.Equal(1040, clock.Read(1.1));
    }

    [Fact]
    public void SeekForwardDoesNotSlowlyCatchUp()
    {
        var clock = new MediaClock();
        clock.Update(new(1000, 1, PlaybackState.Playing, 1));
        clock.Update(new(90000, 1.05, PlaybackState.Playing, 1));
        Assert.Equal(90000, clock.Read(1.05));
    }

    [Fact]
    public void RateChangeUsesContinuousAnchor()
    {
        var clock = new MediaClock();
        clock.Update(new(1000, 1, PlaybackState.Playing, 1));
        clock.Update(new(1100, 1.1, PlaybackState.Playing, 2));
        Assert.Equal(1300, clock.Read(1.2), 5);
    }

    [Fact]
    public void ConnectionLossBoundsExtrapolation()
    {
        var clock = new MediaClock(); clock.Update(new(1000, 1, PlaybackState.Playing, 1));
        Assert.Equal(1250, clock.Read(100));
    }

    [Fact]
    public void UnchangedProgressEventuallyFreezes()
    {
        var clock = new MediaClock();
        for (int i = 0; i < 40; i++) clock.Update(new(1000, 1 + i * 0.05, PlaybackState.Playing, 1));
        Assert.True(clock.IsStalled); Assert.Equal(1000, clock.Read(2), 5);
    }

    [Fact]
    public void OldSamplesAreIgnored()
    {
        var clock = new MediaClock(); clock.Update(new(1000, 1, PlaybackState.Playing, 1));
        Assert.False(clock.Update(new(0, 0.5, PlaybackState.Paused, 1)));
        Assert.Equal(1100, clock.Read(1.1), 5);
    }

    [Fact]
    public void InvalidSampleIsRejected()
    {
        var clock = new MediaClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Update(new(double.NaN, 1, PlaybackState.Playing, 1)));
    }

    [Fact]
    public void FrameSummaryIncludesTailLatency()
    {
        var result = FrameStatistics.Summarize(Enumerable.Repeat(6.25, 94).Concat(Enumerable.Repeat(40.0, 6)));
        Assert.Equal(40, result.P95IntervalMs); Assert.Equal(6, result.Over33MsPercent);
    }
}
