using System.Text.Json.Serialization;

namespace LinkPulse.Abstractions;

/// <summary>
/// Base type for the two frame kinds carried over the <c>/connection-probe</c> WebSocket,
/// distinguished on the wire by a <c>"type"</c> discriminator. See the v1 spec, &#167;3.5.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingFrame), "ping")]
[JsonDerivedType(typeof(SnapshotFrame), "snapshot")]
public abstract record ProbeFrame;

/// <summary>
/// Client&#8594;server timing probe. The server echoes the frame back verbatim; it never
/// parses or modifies <see cref="Payload"/>, so the client send-stamp stays opaque and RTT
/// is computed entirely on the client clock.
/// </summary>
public sealed record PingFrame : ProbeFrame
{
    /// <summary>Monotonic sequence number used to pair echoes with their originating ping.</summary>
    public long Seq { get; init; }

    /// <summary>Opaque client send-stamp, echoed back untouched by the server.</summary>
    public string Payload { get; init; } = string.Empty;
}

/// <summary>
/// Client&#8594;server aggregate report, sent on the snapshot cadence. Carries client identity
/// plus the flattened metric fields that make up a <see cref="MetricSnapshot"/>.
/// </summary>
public sealed record SnapshotFrame : ProbeFrame
{
    /// <summary>Stable per-browser identifier; the dashboard's primary grouping key.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Per-connection identifier distinguishing concurrent tabs and reconnects.</summary>
    public Guid SessionId { get; init; }

    /// <summary>The render phase the measurements were taken in.</summary>
    public ClientPhase Phase { get; init; }

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

    /// <summary>Builds a wire frame from client identity and a computed snapshot.</summary>
    /// <param name="clientId">The stable per-browser identifier.</param>
    /// <param name="sessionId">The per-connection identifier.</param>
    /// <param name="snapshot">The metrics to carry.</param>
    public static SnapshotFrame FromSnapshot(Guid clientId, Guid sessionId, MetricSnapshot snapshot) => new()
    {
        ClientId = clientId,
        SessionId = sessionId,
        Phase = snapshot.Phase,
        RttMin = snapshot.RttMin,
        RttAvg = snapshot.RttAvg,
        RttMax = snapshot.RttMax,
        Jitter = snapshot.Jitter,
        LossPct = snapshot.LossPct,
        SampleCount = snapshot.SampleCount,
    };

    /// <summary>Extracts the domain <see cref="MetricSnapshot"/> from this wire frame.</summary>
    public MetricSnapshot ToSnapshot() => new()
    {
        Phase = Phase,
        RttMin = RttMin,
        RttAvg = RttAvg,
        RttMax = RttMax,
        Jitter = Jitter,
        LossPct = LossPct,
        SampleCount = SampleCount,
    };
}
