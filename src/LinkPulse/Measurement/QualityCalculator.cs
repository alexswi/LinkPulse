using LinkPulse.Abstractions;

namespace LinkPulse;

/// <summary>
/// Classifies measured metrics into a <see cref="QualityRating"/> using the weakest-link model
/// from the v1 spec (&#167;6): RTT, jitter, and loss are rated independently and the overall rating
/// is the worst of the three, so a single bad dimension cannot hide behind two good ones.
/// </summary>
/// <remarks>
/// These are pure functions of their inputs and the supplied <see cref="QualityThresholds"/>.
/// Thresholds are exclusive upper bounds (a metric earns a tier when strictly below the bound),
/// except the loss "excellent" tier which is inclusive so that exactly 0% loss rates excellent
/// &#8212; see <see cref="QualityThresholds"/>. None of these methods returns
/// <see cref="QualityRating.Disconnected"/>; that is a connection state, not a measured tier.
/// </remarks>
public static class QualityCalculator
{
    /// <summary>Rates average RTT against the RTT tier bounds.</summary>
    /// <param name="rttAvgMs">Average RTT over the window, in milliseconds.</param>
    /// <param name="thresholds">The tier boundaries to apply.</param>
    /// <returns>The RTT sub-rating, from <see cref="QualityRating.Excellent"/> to <see cref="QualityRating.Poor"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="thresholds"/> is <see langword="null"/>.</exception>
    public static QualityRating ClassifyRtt(double rttAvgMs, QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (rttAvgMs < thresholds.RttExcellentMs)
        {
            return QualityRating.Excellent;
        }

        if (rttAvgMs < thresholds.RttGoodMs)
        {
            return QualityRating.Good;
        }

        return rttAvgMs < thresholds.RttFairMs ? QualityRating.Fair : QualityRating.Poor;
    }

    /// <summary>Rates jitter against the jitter tier bounds.</summary>
    /// <param name="jitterMs">Jitter over the window, in milliseconds.</param>
    /// <param name="thresholds">The tier boundaries to apply.</param>
    /// <returns>The jitter sub-rating, from <see cref="QualityRating.Excellent"/> to <see cref="QualityRating.Poor"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="thresholds"/> is <see langword="null"/>.</exception>
    public static QualityRating ClassifyJitter(double jitterMs, QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (jitterMs < thresholds.JitterExcellentMs)
        {
            return QualityRating.Excellent;
        }

        if (jitterMs < thresholds.JitterGoodMs)
        {
            return QualityRating.Good;
        }

        return jitterMs < thresholds.JitterFairMs ? QualityRating.Fair : QualityRating.Poor;
    }

    /// <summary>Rates packet loss against the loss tier bounds.</summary>
    /// <param name="lossPct">Packet loss over the window, as a percentage in [0, 100].</param>
    /// <param name="thresholds">The tier boundaries to apply.</param>
    /// <returns>The loss sub-rating, from <see cref="QualityRating.Excellent"/> to <see cref="QualityRating.Poor"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="thresholds"/> is <see langword="null"/>.</exception>
    public static QualityRating ClassifyLoss(double lossPct, QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        // Inclusive at the excellent bound so 0% loss (the default bound) rates excellent.
        if (lossPct <= thresholds.LossExcellentPct)
        {
            return QualityRating.Excellent;
        }

        if (lossPct < thresholds.LossGoodPct)
        {
            return QualityRating.Good;
        }

        return lossPct < thresholds.LossFairPct ? QualityRating.Fair : QualityRating.Poor;
    }

    /// <summary>
    /// Computes the overall rating for a snapshot as the weakest of the RTT, jitter, and loss
    /// sub-ratings (&#167;6). Uses <see cref="MetricSnapshot.RttAvg"/> for the RTT dimension.
    /// </summary>
    /// <param name="snapshot">The measured metrics to rate.</param>
    /// <param name="thresholds">The tier boundaries to apply.</param>
    /// <returns>The overall rating, never <see cref="QualityRating.Disconnected"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="thresholds"/> is <see langword="null"/>.</exception>
    public static QualityRating Classify(MetricSnapshot snapshot, QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);

        var rtt = ClassifyRtt(snapshot.RttAvg, thresholds);
        var jitter = ClassifyJitter(snapshot.Jitter, thresholds);
        var loss = ClassifyLoss(snapshot.LossPct, thresholds);

        // The enum is ordered worst-to-best, so the numeric minimum is the weakest link.
        return (QualityRating)Math.Min((int)rtt, Math.Min((int)jitter, (int)loss));
    }
}
