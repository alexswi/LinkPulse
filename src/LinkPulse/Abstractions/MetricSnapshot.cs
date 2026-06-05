namespace LinkPulse.Abstractions;

/// <summary>
/// An immutable point-in-time summary of a client's connection metrics, computed
/// client-side over the rolling measurement window. This is the domain representation
/// stored in the server registry and rendered on the dashboard; the wire form is
/// <see cref="SnapshotFrame"/>.
/// </summary>
/// <remarks>
/// The documented field ranges (e.g. non-negative RTTs, <c>RttMin &#8804; RttAvg &#8804; RttMax</c>,
/// <c>LossPct</c> in [0, 100], non-negative <c>SampleCount</c>) are intent, not enforced by
/// this type. Instances built from untrusted wire data are validated server-side (#4/#5).
/// </remarks>
public sealed record MetricSnapshot
{
    /// <summary>The render phase the measurements were taken in.</summary>
    public required ClientPhase Phase { get; init; }

    /// <summary>Minimum clean RTT over the window, in milliseconds.</summary>
    public double RttMin { get; init; }

    /// <summary>Average clean RTT over the window, in milliseconds.</summary>
    public double RttAvg { get; init; }

    /// <summary>Maximum clean RTT over the window, in milliseconds.</summary>
    public double RttMax { get; init; }

    /// <summary>RFC 3550 interarrival jitter estimate over the window, in milliseconds.</summary>
    public double Jitter { get; init; }

    /// <summary>Packet loss over the window, as a percentage in the range [0, 100].</summary>
    public double LossPct { get; init; }

    /// <summary>Number of samples the metrics were computed from.</summary>
    public int SampleCount { get; init; }
}
