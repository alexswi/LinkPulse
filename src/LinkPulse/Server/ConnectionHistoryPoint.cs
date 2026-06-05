using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// One point in a connection's short rolling history (v1 spec, &#167;9), used to draw the
/// server-side RTT sparkline and its outage markers (&#167;5.3). A point is either a reported
/// <see cref="Snapshot"/> or an <em>outage marker</em> &#8212; the visible gap recorded when a
/// stale connection reconnects under the same <c>ClientId</c>, so the dashboard shows the break
/// rather than implying continuity across it.
/// </summary>
/// <param name="TimestampUtc">The server-clock time the point was recorded.</param>
/// <param name="Snapshot">
/// The metrics reported at <paramref name="TimestampUtc"/>, or <see langword="null"/> when this
/// point is an outage marker.
/// </param>
public sealed record ConnectionHistoryPoint(DateTimeOffset TimestampUtc, MetricSnapshot? Snapshot)
{
    /// <summary>
    /// <see langword="true"/> when this point marks a connectivity gap (a reconnect after the entry
    /// went stale) rather than a reported snapshot.
    /// </summary>
    public bool IsOutage => Snapshot is null;

    /// <summary>Creates an outage marker at <paramref name="timestampUtc"/>.</summary>
    /// <param name="timestampUtc">The server-clock time the reconnect was observed.</param>
    public static ConnectionHistoryPoint Outage(DateTimeOffset timestampUtc) => new(timestampUtc, null);
}
