using System;

namespace WorkflowDashboard;

/// <summary>
/// Pure helpers for media-panel ±10s seek. No WinRT, no focus, no keys.
/// Targeting policy: only the currently selected/pinned SMTC session; never fall back to another source.
/// Position correction: SMTC Position is documented as "current as of LastUpdatedTime" — extrapolate
/// while Playing using elapsed wall time × PlaybackRate; fail closed when the snapshot is unsafe.
/// </summary>
internal static class MediaSeek
{
    public const double StepSeconds = 10;

    /// <summary>Refuse extrapolation when the timeline snapshot is older than this (seconds).</summary>
    public const double MaxSnapshotAgeSec = 600; // 10 min — enough for Chromium-stale cases; beyond → fail closed

    /// <summary>Practical VOD timeline: reject unknown/zero and live-style placeholders.</summary>
    public static bool IsSeekableDuration(double durationSec)
        => durationSec > 0.5 && durationSec < 12 * 3600;

    /// <summary>
    /// Selected source must still be present. Returns false when missing/stale — callers must not
    /// fall back to the system "current" session (that can be a different audio source).
    /// </summary>
    public static bool TryResolveSelected(int selectedIndex, int sessionCount, out string reason)
    {
        reason = "";
        if (sessionCount <= 0) { reason = "No media session"; return false; }
        if (selectedIndex < 0 || selectedIndex >= sessionCount)
        { reason = "Selected media source is gone"; return false; }
        return true;
    }

    /// <summary>
    /// Normalize PlaybackRate for extrapolation. Null/non-finite → 1 while playing is assumed only when
    /// <paramref name="playing"/>; zero or negative while playing is treated as not advancing.
    /// </summary>
    public static double EffectiveRate(bool playing, double? playbackRate)
    {
        if (!playing) return 0;
        if (playbackRate is null) return 1;
        double r = playbackRate.Value;
        if (!double.IsFinite(r) || r <= 0) return 0;
        // Cap absurd rates (bad provider data) — fail later via age/position checks, not silent runaway.
        if (r > 16) return 16;
        return r;
    }

    /// <summary>
    /// Estimate the live position from an SMTC snapshot.
    /// Docs: Position is current as of LastUpdatedTime. While playing, add elapsed × rate.
    /// Fail closed on future timestamps, missing update time, or excessive age.
    /// </summary>
    public static bool TryEstimateLivePosition(
        double snapshotPosSec,
        double durationSec,
        DateTime lastUpdatedUtc,
        DateTime nowUtc,
        bool playing,
        double? playbackRate,
        out double livePosSec,
        out string reason)
    {
        livePosSec = 0;
        reason = "";
        if (!IsSeekableDuration(durationSec))
        {
            reason = durationSec <= 0.5
                ? "Seek not available (no timeline)"
                : "Seek not available (live or unseekable)";
            return false;
        }
        if (lastUpdatedUtc == DateTime.MinValue)
        {
            reason = "Seek not available (timeline time unknown)";
            return false;
        }

        if (!double.IsFinite(snapshotPosSec) || snapshotPosSec < 0) snapshotPosSec = 0;
        if (snapshotPosSec > durationSec) snapshotPosSec = durationSec;

        double ageSec = (nowUtc - lastUpdatedUtc).TotalSeconds;
        if (ageSec < -1.0)
        {
            reason = "Seek not available (timeline timestamp in the future)";
            return false;
        }
        if (ageSec < 0) ageSec = 0; // tiny clock skew

        if (ageSec > MaxSnapshotAgeSec)
        {
            reason = "Seek not available (stale timeline)";
            return false;
        }

        double rate = EffectiveRate(playing, playbackRate);
        livePosSec = snapshotPosSec + ageSec * rate;
        if (livePosSec < 0) livePosSec = 0;
        if (livePosSec > durationSec) livePosSec = durationSec;
        return true;
    }

    /// <summary>
    /// After we issue a seek, providers often keep reporting the pre-seek snapshot until LastUpdatedTime
    /// advances. Prefer the pending seek base when the snapshot is still older than the seek.
    /// </summary>
    public static bool TryResolvePositionWithPending(
        double snapshotLivePosSec,
        DateTime snapshotLastUpdatedUtc,
        DateTime nowUtc,
        bool playing,
        double? playbackRate,
        bool hasPending,
        DateTime pendingIssuedUtc,
        double pendingTargetSec,
        double durationSec,
        out double posSec,
        out string source)
    {
        posSec = snapshotLivePosSec;
        source = "smtc";
        if (!hasPending) return true;

        // Provider refreshed at/after our seek → trust SMTC estimate.
        if (snapshotLastUpdatedUtc >= pendingIssuedUtc)
            return true;

        // Snapshot still predates our seek: continue from the pending target.
        double rate = EffectiveRate(playing, playbackRate);
        double age = Math.Max(0, (nowUtc - pendingIssuedUtc).TotalSeconds);
        posSec = pendingTargetSec + age * rate;
        if (posSec < 0) posSec = 0;
        if (IsSeekableDuration(durationSec) && posSec > durationSec) posSec = durationSec;
        source = "pending";
        return true;
    }

    public static bool TryComputeTarget(double positionSec, double durationSec, double deltaSec,
        out double targetSec, out string reason)
    {
        targetSec = 0;
        reason = "";
        if (!IsSeekableDuration(durationSec))
        {
            reason = durationSec <= 0.5
                ? "Seek not available (no timeline)"
                : "Seek not available (live or unseekable)";
            return false;
        }
        if (!double.IsFinite(positionSec) || positionSec < 0) positionSec = 0;
        targetSec = positionSec + deltaSec;
        if (targetSec < 0) targetSec = 0;
        if (targetSec > durationSec) targetSec = durationSec;
        return true;
    }
}
