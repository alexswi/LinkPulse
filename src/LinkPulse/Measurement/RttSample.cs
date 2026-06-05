namespace LinkPulse.Measurement;

/// <summary>
/// A single resolved entry in the measurement window: one ping that has been paired with its
/// echo (a clean round-trip) or finalised as lost. The <see cref="MeasurementEngine"/> retains
/// these as the raw ring buffer so the client component can later render an RTT sparkline with
/// gaps for losses (v1 spec, &#167;3.4).
/// </summary>
/// <param name="Seq">The monotonic sequence number of the originating ping.</param>
/// <param name="RttMs">
/// The clean round-trip time in milliseconds, or <see langword="null"/> if the ping was lost
/// (timed out) or discarded by a sanity guard (&#167;3.2).
/// </param>
public readonly record struct RttSample(long Seq, double? RttMs)
{
    /// <summary>Whether this slot represents a lost or discarded ping (no usable RTT).</summary>
    public bool IsLost => RttMs is null;
}
