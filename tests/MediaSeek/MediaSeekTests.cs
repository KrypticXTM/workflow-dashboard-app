using System;
using WorkflowDashboard;
using Xunit;

public class MediaSeekTests
{
    [Fact]
    public void Step_is_10_seconds() => Assert.Equal(10, MediaSeek.StepSeconds);

    [Theory]
    [InlineData(0, false)]
    [InlineData(0.4, false)]
    [InlineData(60, true)]
    [InlineData(3600, true)]
    [InlineData(12 * 3600, false)]
    [InlineData(1e8, false)]
    public void Seekable_duration(double dur, bool ok)
        => Assert.Equal(ok, MediaSeek.IsSeekableDuration(dur));

    [Fact]
    public void Clamp_start()
    {
        Assert.True(MediaSeek.TryComputeTarget(3, 100, -10, out var t, out _));
        Assert.Equal(0, t);
    }

    [Fact]
    public void Clamp_end()
    {
        Assert.True(MediaSeek.TryComputeTarget(95, 100, 10, out var t, out _));
        Assert.Equal(100, t);
    }

    [Fact]
    public void Mid_seek_ok()
    {
        Assert.True(MediaSeek.TryComputeTarget(40, 100, 10, out var t, out _));
        Assert.Equal(50, t);
        Assert.True(MediaSeek.TryComputeTarget(40, 100, -10, out t, out _));
        Assert.Equal(30, t);
    }

    [Fact]
    public void Live_or_missing_timeline_rejected()
    {
        Assert.False(MediaSeek.TryComputeTarget(10, 0, 10, out _, out var r1));
        Assert.Contains("timeline", r1);
        Assert.False(MediaSeek.TryComputeTarget(10, 1e8, -10, out _, out var r2));
        Assert.Contains("live", r2);
    }

    [Fact]
    public void Two_sources_selected_index_0_ok_index_1_ok_stale_fails()
    {
        Assert.True(MediaSeek.TryResolveSelected(0, 2, out _));
        Assert.True(MediaSeek.TryResolveSelected(1, 2, out _));
        Assert.False(MediaSeek.TryResolveSelected(0, 0, out var none));
        Assert.Contains("No media", none);
        Assert.False(MediaSeek.TryResolveSelected(1, 1, out var stale));
        Assert.Contains("gone", stale);
        Assert.False(MediaSeek.TryResolveSelected(-1, 2, out var nosel));
        Assert.Contains("gone", nosel);
    }

    [Fact]
    public void No_fallback_when_selected_missing_even_if_other_sessions_exist()
    {
        Assert.False(MediaSeek.TryResolveSelected(5, 2, out var reason));
        Assert.Equal("Selected media source is gone", reason);
    }

    // ---- Stale SMTC Position (docs: current as of LastUpdatedTime) ----
    // Tim observed: −10s jumped ~3–4 minutes backward. Reproduce that negative-delta case.

    [Theory]
    [InlineData(180)] // 3 min stale
    [InlineData(210)] // 3.5 min
    [InlineData(240)] // 4 min
    public void Negative_delta_with_stale_snapshot_without_correction_would_jump_minutes(double staleSec)
    {
        // Naive (pre-fix) path: target = snapshotPos + (-10) ignores elapsed playback.
        double snapshotPos = 600; // 10:00 into a VOD
        double naiveTarget = snapshotPos - 10; // 590
        double actualLive = snapshotPos + staleSec; // still playing at 1x
        double naiveJumpBack = actualLive - naiveTarget; // ~190–250s ≈ 3–4+ min
        Assert.InRange(naiveJumpBack, staleSec, staleSec + 10);

        // Fixed path: estimate live, then −10s.
        var lastUp = DateTime.UtcNow.AddSeconds(-staleSec);
        var now = DateTime.UtcNow;
        Assert.True(MediaSeek.TryEstimateLivePosition(snapshotPos, 3600, lastUp, now,
            playing: true, playbackRate: 1.0, out double live, out _));
        Assert.InRange(live, actualLive - 1.5, actualLive + 1.5);
        Assert.True(MediaSeek.TryComputeTarget(live, 3600, -MediaSeek.StepSeconds, out double target, out _));
        // Target should be ~10s behind live, not ~3–4 min behind.
        Assert.InRange(live - target, 9.5, 10.5);
        Assert.True(Math.Abs(target - (snapshotPos + staleSec - 10)) < 2,
            $"expected ~live-10, got target={target} live={live}");
    }

    [Fact]
    public void Negative_delta_paused_uses_snapshot_as_is()
    {
        var lastUp = DateTime.UtcNow.AddMinutes(-4);
        var now = DateTime.UtcNow;
        Assert.True(MediaSeek.TryEstimateLivePosition(600, 3600, lastUp, now,
            playing: false, playbackRate: 1.0, out double live, out _));
        Assert.Equal(600, live);
        Assert.True(MediaSeek.TryComputeTarget(live, 3600, -10, out double t, out _));
        Assert.Equal(590, t);
    }

    [Fact]
    public void Negative_delta_playing_at_2x_extrapolates_rate()
    {
        // 2 minutes wall clock at 2x ⇒ +240s media time
        var lastUp = DateTime.UtcNow.AddSeconds(-120);
        var now = DateTime.UtcNow;
        Assert.True(MediaSeek.TryEstimateLivePosition(600, 3600, lastUp, now,
            playing: true, playbackRate: 2.0, out double live, out _));
        Assert.InRange(live, 600 + 240 - 2, 600 + 240 + 2);
        Assert.True(MediaSeek.TryComputeTarget(live, 3600, -10, out double t, out _));
        Assert.InRange(live - t, 9.5, 10.5);
    }

    [Fact]
    public void Null_rate_while_playing_assumes_1x()
    {
        var lastUp = DateTime.UtcNow.AddSeconds(-60);
        Assert.True(MediaSeek.TryEstimateLivePosition(100, 1000, lastUp, DateTime.UtcNow,
            true, null, out double live, out _));
        Assert.InRange(live, 158, 162);
    }

    [Fact]
    public void Future_LastUpdatedTime_fail_closed()
    {
        var lastUp = DateTime.UtcNow.AddMinutes(5);
        Assert.False(MediaSeek.TryEstimateLivePosition(100, 1000, lastUp, DateTime.UtcNow,
            true, 1.0, out _, out var reason));
        Assert.Contains("future", reason);
    }

    [Fact]
    public void Excessively_stale_snapshot_fail_closed()
    {
        var lastUp = DateTime.UtcNow.AddSeconds(-(MediaSeek.MaxSnapshotAgeSec + 30));
        Assert.False(MediaSeek.TryEstimateLivePosition(100, 1000, lastUp, DateTime.UtcNow,
            true, 1.0, out _, out var reason));
        Assert.Contains("stale", reason);
    }

    [Fact]
    public void Missing_LastUpdatedTime_fail_closed()
    {
        Assert.False(MediaSeek.TryEstimateLivePosition(100, 1000, DateTime.MinValue, DateTime.UtcNow,
            true, 1.0, out _, out var reason));
        Assert.Contains("unknown", reason);
    }

    [Fact]
    public void Estimate_clamps_to_duration_end()
    {
        var lastUp = DateTime.UtcNow.AddSeconds(-120);
        Assert.True(MediaSeek.TryEstimateLivePosition(95, 100, lastUp, DateTime.UtcNow,
            true, 1.0, out double live, out _));
        Assert.Equal(100, live);
    }

    [Fact]
    public void Rapid_negative_seeks_use_pending_when_snapshot_not_refreshed()
    {
        // After first −10s seek to 800, provider still reports LastUpdatedTime before the seek.
        double snapLive = 810; // extrapolated from old snap — would ignore our seek if reused alone
        var snapUpdated = DateTime.UtcNow.AddSeconds(-30);
        var issued = DateTime.UtcNow.AddSeconds(-1);
        var now = DateTime.UtcNow;
        Assert.True(MediaSeek.TryResolvePositionWithPending(
            snapLive, snapUpdated, now, playing: true, playbackRate: 1.0,
            hasPending: true, pendingIssuedUtc: issued, pendingTargetSec: 800, durationSec: 3600,
            out double pos, out string src));
        Assert.Equal("pending", src);
        // ~800 + ~1s elapsed
        Assert.InRange(pos, 800, 803);

        // Second −10s from pending base
        Assert.True(MediaSeek.TryComputeTarget(pos, 3600, -10, out double t2, out _));
        Assert.InRange(t2, 790, 793);
    }

    [Fact]
    public void Pending_ignored_once_snapshot_refreshes_after_seek()
    {
        var issued = DateTime.UtcNow.AddSeconds(-5);
        var snapUpdated = DateTime.UtcNow.AddSeconds(-1); // provider refreshed after seek
        Assert.True(MediaSeek.TryResolvePositionWithPending(
            790, snapUpdated, DateTime.UtcNow, true, 1.0,
            true, issued, 800, 3600, out double pos, out string src));
        Assert.Equal("smtc", src);
        Assert.Equal(790, pos);
    }

    [Fact]
    public void Unavailable_duration_still_rejected_via_estimate()
    {
        Assert.False(MediaSeek.TryEstimateLivePosition(10, 0, DateTime.UtcNow, DateTime.UtcNow,
            true, 1.0, out _, out var r));
        Assert.Contains("timeline", r);
    }
}
