using LinkPulse.Measurement;

namespace LinkPulse.Server;

/// <summary>
/// Projects a connection's stored history (v1 spec, &#167;9) onto the sample sequence the
/// <c>SparklineGeometry</c> renderer draws (&#167;10), so the dashboard reuses the exact same
/// sparkline renderer as the client component. Pure, so it is unit-testable in isolation.
/// </summary>
internal static class ServerHistory
{
    /// <summary>
    /// Maps each history point to an <see cref="RttSample"/> using its average RTT as the plotted
    /// value; outage markers (points with no snapshot) become lost samples, so the sparkline breaks
    /// the line and draws an outage marker at that position rather than bridging the gap (&#167;5.3).
    /// The point's index is used as the sample sequence, which is all the geometry needs.
    /// </summary>
    /// <param name="history">The rolling history, oldest first.</param>
    public static IReadOnlyList<RttSample> ToSamples(IReadOnlyList<ConnectionHistoryPoint> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var samples = new List<RttSample>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            samples.Add(new RttSample(i, history[i].Snapshot?.RttAvg));
        }

        return samples;
    }
}
