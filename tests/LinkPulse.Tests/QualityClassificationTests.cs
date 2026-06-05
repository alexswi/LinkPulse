using LinkPulse;
using LinkPulse.Abstractions;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the §6 quality model: per-metric tier boundaries, the weakest-link combination, and the
/// hysteresis filter that holds a level for a number of consecutive snapshots before it changes.
/// </summary>
public sealed class QualityClassificationTests
{
    private static readonly QualityThresholds Thresholds = QualityThresholds.Default;

    [Theory]
    [InlineData(0, QualityRating.Excellent)]
    [InlineData(49.999, QualityRating.Excellent)]
    [InlineData(50, QualityRating.Good)]       // strict < bound: exactly 50 is no longer excellent
    [InlineData(149.999, QualityRating.Good)]
    [InlineData(150, QualityRating.Fair)]
    [InlineData(299.999, QualityRating.Fair)]
    [InlineData(300, QualityRating.Poor)]
    [InlineData(1000, QualityRating.Poor)]
    public void Rtt_is_classified_against_its_tier_bounds(double rttAvgMs, QualityRating expected) =>
        Assert.Equal(expected, QualityCalculator.ClassifyRtt(rttAvgMs, Thresholds));

    [Theory]
    [InlineData(0, QualityRating.Excellent)]
    [InlineData(9.999, QualityRating.Excellent)]
    [InlineData(10, QualityRating.Good)]
    [InlineData(29.999, QualityRating.Good)]
    [InlineData(30, QualityRating.Fair)]
    [InlineData(59.999, QualityRating.Fair)]
    [InlineData(60, QualityRating.Poor)]
    public void Jitter_is_classified_against_its_tier_bounds(double jitterMs, QualityRating expected) =>
        Assert.Equal(expected, QualityCalculator.ClassifyJitter(jitterMs, Thresholds));

    [Theory]
    [InlineData(0, QualityRating.Excellent)]   // inclusive at 0: the loss-excellent exception
    [InlineData(0.0001, QualityRating.Good)]
    [InlineData(0.999, QualityRating.Good)]
    [InlineData(1, QualityRating.Fair)]
    [InlineData(4.999, QualityRating.Fair)]
    [InlineData(5, QualityRating.Poor)]
    [InlineData(100, QualityRating.Poor)]
    public void Loss_is_classified_against_its_tier_bounds(double lossPct, QualityRating expected) =>
        Assert.Equal(expected, QualityCalculator.ClassifyLoss(lossPct, Thresholds));

    [Theory]
    // Each row drives the overall rating from a different dimension; the other two are excellent.
    [InlineData(10, 5, 0, QualityRating.Excellent)]   // all excellent
    [InlineData(10, 100, 0, QualityRating.Poor)]      // jitter drags it to Poor
    [InlineData(400, 5, 0, QualityRating.Poor)]       // RTT drags it to Poor
    [InlineData(10, 5, 10, QualityRating.Poor)]       // loss drags it to Poor
    [InlineData(10, 20, 0, QualityRating.Good)]       // weakest is the Good jitter
    [InlineData(200, 5, 0, QualityRating.Fair)]       // weakest is the Fair RTT
    public void Overall_rating_is_the_weakest_of_the_three_sub_ratings(
        double rttAvg, double jitter, double lossPct, QualityRating expected)
    {
        var snapshot = new MetricSnapshot
        {
            Phase = ClientPhase.Server,
            RttAvg = rttAvg,
            Jitter = jitter,
            LossPct = lossPct,
        };

        Assert.Equal(expected, QualityCalculator.Classify(snapshot, Thresholds));
    }

    [Fact]
    public void A_fresh_stabilizer_starts_disconnected()
    {
        var stabilizer = new QualityStabilizer(3);

        Assert.Equal(QualityRating.Disconnected, stabilizer.Current);
    }

    [Fact]
    public void The_first_reading_is_adopted_immediately()
    {
        var stabilizer = new QualityStabilizer(3);

        Assert.Equal(QualityRating.Excellent, stabilizer.Push(QualityRating.Excellent));
        Assert.Equal(QualityRating.Excellent, stabilizer.Current);
    }

    [Fact]
    public void A_new_level_must_hold_for_the_configured_number_of_snapshots()
    {
        var stabilizer = new QualityStabilizer(3);
        stabilizer.Push(QualityRating.Excellent); // establishes Excellent

        Assert.Equal(QualityRating.Excellent, stabilizer.Push(QualityRating.Good)); // 1st Good
        Assert.Equal(QualityRating.Excellent, stabilizer.Push(QualityRating.Good)); // 2nd Good
        Assert.Equal(QualityRating.Good, stabilizer.Push(QualityRating.Good));      // 3rd Good → switch
    }

    [Fact]
    public void An_interrupting_reading_restarts_the_streak()
    {
        var stabilizer = new QualityStabilizer(3);
        stabilizer.Push(QualityRating.Excellent);

        stabilizer.Push(QualityRating.Good); // Good streak = 1
        stabilizer.Push(QualityRating.Good); // Good streak = 2
        Assert.Equal(QualityRating.Excellent, stabilizer.Push(QualityRating.Fair)); // restarts on Fair (=1)
        Assert.Equal(QualityRating.Excellent, stabilizer.Push(QualityRating.Fair)); // Fair streak = 2
        Assert.Equal(QualityRating.Fair, stabilizer.Push(QualityRating.Fair));      // Fair streak = 3 → switch
    }

    [Fact]
    public void A_reading_matching_the_current_level_cancels_a_pending_change()
    {
        var stabilizer = new QualityStabilizer(3);
        stabilizer.Push(QualityRating.Excellent);

        stabilizer.Push(QualityRating.Good);      // Good streak = 1
        stabilizer.Push(QualityRating.Excellent); // back to current → cancels the pending Good
        stabilizer.Push(QualityRating.Good);      // streak restarts at 1
        Assert.Equal(QualityRating.Excellent, stabilizer.Push(QualityRating.Good)); // only 2 in a row → no switch
    }

    [Fact]
    public void Reset_re_arms_so_the_next_reading_is_adopted_immediately()
    {
        var stabilizer = new QualityStabilizer(3);
        stabilizer.Push(QualityRating.Excellent);

        stabilizer.Reset();

        Assert.Equal(QualityRating.Poor, stabilizer.Push(QualityRating.Poor)); // adopted at once
        Assert.Equal(QualityRating.Poor, stabilizer.Current);
    }
}
