namespace PotDanmaku.Core;

public enum PlaybackState { Stopped, Paused, Playing }

public sealed record PlaybackSample(double PositionMs, double ObservedSeconds,
    PlaybackState State, double Rate, double DurationMs = 0);

/// <summary>Single-consumer clock. Samples and reads must use the same monotonic time origin.</summary>
public sealed class MediaClock
{
    private PlaybackSample? sample;
    private double anchorPosition, anchorSeconds, effectiveRate;
    private double expectedSamplePosition;
    private double lastRead = double.NegativeInfinity;
    private double lastReadSeconds = double.NegativeInfinity;
    private double lastChangedSeconds;
    private double progressCadenceSeconds = 0.5;
    private bool awaitingPausePosition;
    private double pausePositionDeadline, pausePositionLimit;
    public long Discontinuities { get; private set; }
    public bool IsStalled { get; private set; }

    public void Reset()
    {
        sample = null;
        lastRead = double.NegativeInfinity;
        lastReadSeconds = double.NegativeInfinity;
        Discontinuities = 0;
        IsStalled = false;
        progressCadenceSeconds = 0.5;
        awaitingPausePosition = false;
    }

    public bool Update(PlaybackSample next)
    {
        if (!double.IsFinite(next.PositionMs) || next.PositionMs < 0 ||
            !double.IsFinite(next.ObservedSeconds) || !double.IsFinite(next.Rate) || next.Rate <= 0)
            throw new ArgumentOutOfRangeException(nameof(next));
        if (sample != null && next.ObservedSeconds <= sample.ObservedSeconds) return false;
        double predicted = Predict(next.ObservedSeconds);
        bool transition = sample == null || next.State != sample.State;
        bool pauseOrResume = sample != null &&
            ((sample.State == PlaybackState.Playing && next.State == PlaybackState.Paused) ||
             (sample.State == PlaybackState.Paused && next.State == PlaybackState.Playing));
        bool progressed = sample != null && Math.Abs(next.PositionMs - sample.PositionMs) > 0.5;
        // Cached position queries can repeat for ~500 ms even though the player is running.
        // An unchanged reading is evidence of freshness, not a new position measurement.
        // Cadence is wall-clock time, while the position error is media time.
        // At 3x a cached half-second observation can be 1500 ms behind playback.
        double maxRate = Math.Max(next.Rate, sample?.Rate ?? next.Rate);
        double seekThresholdMs = Math.Max(250, progressCadenceSeconds * 1000 * maxRate + 100);
        // A pause can be reported before its final position reaches the same cached API.
        // Allow one bounded forward cache refresh, not an arbitrary paused-time change.
        // Backward seeks, large forward seeks and changes after this window remain seeks.
        bool pausePositionRefresh = awaitingPausePosition && sample?.State == PlaybackState.Paused &&
            next.State == PlaybackState.Paused && next.ObservedSeconds <= pausePositionDeadline &&
            next.PositionMs > sample.PositionMs && next.PositionMs <= pausePositionLimit;
        if (progressed || next.State != PlaybackState.Paused || next.ObservedSeconds > pausePositionDeadline)
            awaitingPausePosition = false;
        // Detect seeks against the raw player's segment, not the displayed anchor.
        // The latter deliberately preserves continuity across pause and can lead the
        // cached position; that visual difference must not become a delayed seek.
        double expectedPosition = sample == null ? next.PositionMs : expectedSamplePosition +
            (sample.State == PlaybackState.Playing ? (next.ObservedSeconds - sample.ObservedSeconds) * 1000 * sample.Rate : 0);
        bool seek = sample != null && !pausePositionRefresh && ((!transition && next.State != PlaybackState.Playing && next.PositionMs != sample.PositionMs) ||
            (progressed && Math.Abs(next.PositionMs - expectedPosition) > seekThresholdMs) ||
            next.PositionMs < sample.PositionMs - 100);
        if (progressed && !seek && !transition && next.State == PlaybackState.Playing)
        {
            double positionStepSeconds = (next.PositionMs - sample!.PositionMs) / (1000 * sample.Rate);
            double interval = Math.Max(next.ObservedSeconds - lastChangedSeconds, positionStepSeconds);
            progressCadenceSeconds = 0.8 * progressCadenceSeconds + 0.2 * Math.Clamp(interval, 0.02, 1);
        }
        if (transition || progressed)
            lastChangedSeconds = next.ObservedSeconds;
        bool stalled = next.State == PlaybackState.Playing && next.ObservedSeconds - lastChangedSeconds > Math.Max(1.5, progressCadenceSeconds * 3);
        bool stalledTransition = stalled != IsStalled;
        IsStalled = stalled;
        if (pauseOrResume && !seek)
        {
            // State replies can still carry the player's half-second cached position.
            // Freeze the last displayed frame rather than snapping back to that value.
            // Resume at the latest render time so delivery latency is not replayed.
            anchorPosition = double.IsFinite(lastRead) ? lastRead : predicted;
            anchorSeconds = Math.Max(next.ObservedSeconds, lastReadSeconds);
            effectiveRate = next.State == PlaybackState.Playing ? next.Rate : 0;
            // Even a changed reply may precede the final cached frame by a few ms.
            // Early post-resume readings can shorten the learned cadence, so retain
            // the observed half-second cache allowance for this one pause refresh.
            double pauseCadenceSeconds = Math.Max(0.5, progressCadenceSeconds);
            awaitingPausePosition = next.State == PlaybackState.Paused;
            pausePositionDeadline = next.ObservedSeconds + Math.Min(pauseCadenceSeconds + 0.15, 1.15);
            pausePositionLimit = next.PositionMs + pauseCadenceSeconds * 1000 * maxRate + 100;
        }
        else if (transition || seek || stalledTransition)
        {
            anchorPosition = next.PositionMs;
            anchorSeconds = next.ObservedSeconds;
            effectiveRate = next.State == PlaybackState.Playing && !stalled ? next.Rate : 0;
            lastRead = double.NegativeInfinity;
            lastReadSeconds = double.NegativeInfinity;
            if (seek) Discontinuities++;
        }
        else if (progressed || next.Rate != sample!.Rate)
        {
            // Change the slope at the latest rendered time, not at a backdated sample.
            // Otherwise we either extrapolate delivery latency twice, or retroactively
            // apply the new rate over that latency and produce a step every poll.
            double continuationTime = Math.Max(next.ObservedSeconds, lastReadSeconds);
            anchorPosition = Math.Max(Predict(continuationTime), double.IsFinite(lastRead) ? lastRead : 0);
            anchorSeconds = continuationTime;
            // Repeated cached values must not decelerate the clock on every poll.
            double correction = progressed ? Math.Clamp((next.PositionMs - predicted) / 1000, -0.10, 0.10) : 0;
            effectiveRate = next.State == PlaybackState.Playing && !stalled ? Math.Max(0, next.Rate + correction) : 0;
        }
        expectedSamplePosition = transition || progressed || seek ? next.PositionMs : expectedPosition;
        sample = next;
        return transition || seek || stalledTransition;
    }

    private double Predict(double now)
    {
        if (sample == null) return 0;
        // Never extrapolate indefinitely after connection loss.
        double bounded = Math.Min(now, sample.ObservedSeconds + 0.25);
        // Update may evaluate an observation earlier than the render-time anchor.
        return anchorPosition + (bounded - anchorSeconds) * 1000 * effectiveRate;
    }

    public double Read(double now)
    {
        double value = Math.Max(0, Predict(now));
        if (sample?.DurationMs > 0) value = Math.Min(value, sample.DurationMs);
        value = Math.Max(value, lastRead);
        lastRead = value;
        lastReadSeconds = now;
        return value;
    }
}
