using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// Validates and bounds an untrusted <see cref="SnapshotFrame"/> received over the probe socket
/// (v1 spec, &#167;11). Every field is attacker-controlled, so a frame is <em>rejected outright</em>
/// &#8212; not silently clamped &#8212; when any value is impossible (non-finite, negative, out of
/// range, mis-ordered RTTs, an empty identifier, or an undefined phase). Silent clamping is avoided
/// deliberately: it would let a malformed frame still land in the registry as plausible-looking data.
/// </summary>
/// <remarks>
/// Pure and side-effect free, so it is fully unit-testable in isolation. The ceilings are generous
/// absolute sanity bounds, independent of any client's configured window: a well-behaved client's
/// values sit far below them (RTT is capped at <c>PingTimeoutMs</c> client-side, §3.2), so they only
/// ever reject a hostile or corrupt frame.
/// </remarks>
internal static class SnapshotValidator
{
    /// <summary>Absolute upper bound for any RTT field, in milliseconds (10 minutes).</summary>
    internal const double MaxRttMs = 600_000;

    /// <summary>Absolute upper bound for the jitter estimate, in milliseconds.</summary>
    internal const double MaxJitterMs = 600_000;

    /// <summary>Absolute upper bound for the reported sample count.</summary>
    internal const int MaxSampleCount = 100_000;

    /// <summary>
    /// Attempts to turn an untrusted wire frame into a trusted identity plus
    /// <see cref="MetricSnapshot"/>. Returns <see langword="false"/> (with default out-parameters)
    /// when any field is out of bounds, in which case the caller discards the frame and keeps the
    /// connection open.
    /// </summary>
    /// <param name="frame">The deserialized, otherwise-unvalidated frame.</param>
    /// <param name="clientId">The validated stable client identifier on success.</param>
    /// <param name="sessionId">The validated per-connection identifier on success.</param>
    /// <param name="snapshot">The validated, in-range metrics on success.</param>
    /// <returns><see langword="true"/> if the frame is well-formed and in range; otherwise <see langword="false"/>.</returns>
    public static bool TryValidate(
        SnapshotFrame? frame,
        out Guid clientId,
        out Guid sessionId,
        out MetricSnapshot snapshot)
    {
        clientId = default;
        sessionId = default;
        snapshot = null!;

        if (frame is null ||
            frame.ClientId == Guid.Empty ||
            frame.SessionId == Guid.Empty ||
            !Enum.IsDefined(frame.Phase) ||
            !IsRtt(frame.RttMin) ||
            !IsRtt(frame.RttAvg) ||
            !IsRtt(frame.RttMax) ||
            frame.RttMin > frame.RttAvg ||
            frame.RttAvg > frame.RttMax ||
            !double.IsFinite(frame.Jitter) || frame.Jitter < 0 || frame.Jitter > MaxJitterMs ||
            !double.IsFinite(frame.LossPct) || frame.LossPct < 0 || frame.LossPct > 100 ||
            frame.SampleCount < 0 || frame.SampleCount > MaxSampleCount)
        {
            return false;
        }

        clientId = frame.ClientId;
        sessionId = frame.SessionId;
        snapshot = frame.ToSnapshot();
        return true;
    }

    private static bool IsRtt(double value) => double.IsFinite(value) && value >= 0 && value <= MaxRttMs;
}
