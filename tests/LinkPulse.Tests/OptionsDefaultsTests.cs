using LinkPulse.Abstractions;

namespace LinkPulse.Tests;

/// <summary>
/// Pins the default option and threshold values to the v1 spec (&#167;6, &#167;7, &#167;13) so accidental
/// drift in the defaults is caught.
/// </summary>
public sealed class OptionsDefaultsTests
{
    [Fact]
    public void LinkPulseOptions_defaults_match_spec()
    {
        var options = new LinkPulseOptions();

        Assert.Equal(1000, options.PingIntervalMs);
        Assert.Equal(5000, options.HiddenTabPingIntervalMs);
        Assert.Equal(30, options.WindowSize);
        Assert.Equal(5000, options.PingTimeoutMs);
        Assert.Equal(16, options.JitterSmoothingFactor);
        Assert.Equal(5000, options.SnapshotIntervalMs);
        Assert.Equal(3, options.HysteresisSamples);
        Assert.Equal(30_000, options.StaleThresholdMs);
        Assert.Equal(7_200_000, options.StaleRetentionMs);
        Assert.Equal(DisplayMode.Badge, options.Display);
        Assert.Equal(QualityThresholds.Default, options.Thresholds);
    }

    [Fact]
    public void MinPingInterval_hard_bound_is_100ms()
    {
        Assert.Equal(100, LinkPulseOptions.MinPingIntervalMs);
    }

    [Fact]
    public void QualityThresholds_defaults_match_spec_section_6()
    {
        var t = QualityThresholds.Default;

        Assert.Equal(50, t.RttExcellentMs);
        Assert.Equal(150, t.RttGoodMs);
        Assert.Equal(300, t.RttFairMs);

        Assert.Equal(10, t.JitterExcellentMs);
        Assert.Equal(30, t.JitterGoodMs);
        Assert.Equal(60, t.JitterFairMs);

        Assert.Equal(0, t.LossExcellentPct);
        Assert.Equal(1, t.LossGoodPct);
        Assert.Equal(5, t.LossFairPct);
    }

    [Fact]
    public void QualityRating_is_ordered_so_min_picks_the_weakest_link()
    {
        // The overall rating will be computed as min(rtt, jitter, loss); the numerically
        // smallest measured tier must therefore be the worst. This ordering is the load-bearing
        // contract — the rating logic (a later issue) depends on it.
        Assert.True(QualityRating.Poor < QualityRating.Fair);
        Assert.True(QualityRating.Fair < QualityRating.Good);
        Assert.True(QualityRating.Good < QualityRating.Excellent);
        Assert.True(QualityRating.Disconnected < QualityRating.Poor);
    }
}
