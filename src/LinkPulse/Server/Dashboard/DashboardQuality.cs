using LinkPulse.Abstractions;
using LinkPulse.Measurement;

namespace LinkPulse.Server;

/// <summary>
/// Derives the dashboard's overall quality rating for a connection (v1 spec, &#167;6/&#167;10),
/// bridging the server-side <see cref="ConnectionView"/> to the shared, weakest-link
/// <see cref="QualityCalculator"/>. Kept pure and separate from the markup so the rule is
/// unit-testable.
/// </summary>
internal static class DashboardQuality
{
    /// <summary>
    /// Rates a connection: <see cref="QualityRating.Disconnected"/> when it is stale or has reported no
    /// snapshot yet (no live measurement to rate); otherwise the weakest-link rating of its latest
    /// snapshot. A session-less but not-yet-stale entry keeps its last-known rating, mirroring the
    /// client's "show last-known, greyed" behaviour (&#167;5.2).
    /// </summary>
    /// <param name="view">The connection to rate.</param>
    /// <param name="thresholds">The quality tier boundaries to apply.</param>
    public static QualityRating Rate(ConnectionView view, QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (view.IsStale || view.LatestSnapshot is null)
        {
            return QualityRating.Disconnected;
        }

        return QualityCalculator.Classify(view.LatestSnapshot, thresholds);
    }
}
