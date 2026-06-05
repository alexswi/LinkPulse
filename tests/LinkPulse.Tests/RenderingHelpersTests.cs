using LinkPulse.Abstractions;
using LinkPulse.Measurement;
using LinkPulse.Rendering;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the pure presentation helpers behind the <c>&lt;LinkPulse /&gt;</c> component: the
/// rating-to-visual mapping (&#167;8 accessibility: a label always travels with the colour), the
/// elapsed-duration formatting for the uptime/reconnect displays (&#167;5.2), and the sparkline
/// geometry (&#167;8). All are pure functions, so the rendered output is verified without a browser.
/// </summary>
public sealed class RenderingHelpersTests
{
    [Theory]
    [InlineData(QualityRating.Excellent, "Excellent", "excellent")]
    [InlineData(QualityRating.Good, "Good", "good")]
    [InlineData(QualityRating.Fair, "Fair", "fair")]
    [InlineData(QualityRating.Poor, "Poor", "poor")]
    [InlineData(QualityRating.Disconnected, "Disconnected", "disconnected")]
    public void Each_rating_maps_to_its_label_and_css_modifier(
        QualityRating rating, string expectedLabel, string expectedModifier)
    {
        Assert.Equal(expectedLabel, QualityVisuals.Label(rating));
        Assert.Equal(expectedModifier, QualityVisuals.Modifier(rating));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(42, "42s")]
    [InlineData(59.9, "59s")]        // sub-second precision is dropped, not rounded up
    [InlineData(-5, "0s")]           // negative clamps to zero
    [InlineData(60, "1m 00s")]
    [InlineData(125, "2m 05s")]      // seconds are zero-padded
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 00m")]
    [InlineData(3725, "1h 02m")]     // 1h 2m 5s -> seconds dropped beyond the hour
    public void Durations_format_compactly(double totalSeconds, string expected) =>
        Assert.Equal(expected, DurationFormat.Compact(totalSeconds));

    [Fact]
    public void An_empty_window_produces_an_empty_sparkline()
    {
        var geometry = SparklineGeometry.Build([], 100, 30);

        Assert.Same(SparklineGeometry.Empty, geometry);
        Assert.Empty(geometry.Segments);
        Assert.Empty(geometry.Outages);
        Assert.Null(geometry.LatestX);
        Assert.Null(geometry.LatestY);
    }

    [Fact]
    public void Clean_samples_map_to_one_segment_with_an_inverted_auto_scaled_y_axis()
    {
        // rtt 10/20/30 over width 100, height 30: x steps 0/50/100; y inverts and scales so the
        // smallest rtt sits at the bottom (y=height) and the largest at the top (y=0).
        var geometry = SparklineGeometry.Build(
            [new RttSample(1, 10), new RttSample(2, 20), new RttSample(3, 30)], 100, 30);

        Assert.Equal(["0,30 50,15 100,0"], geometry.Segments);
        Assert.Empty(geometry.Outages);
        Assert.Equal("100", geometry.LatestX);
        Assert.Equal("0", geometry.LatestY);
    }

    [Fact]
    public void A_constant_window_draws_a_flat_line_down_the_middle()
    {
        var geometry = SparklineGeometry.Build([new RttSample(1, 5), new RttSample(2, 5)], 10, 20);

        Assert.Equal(["0,10 10,10"], geometry.Segments);
    }

    [Fact]
    public void A_loss_breaks_the_line_into_two_segments_and_marks_an_outage()
    {
        var geometry = SparklineGeometry.Build(
            [new RttSample(1, 10), new RttSample(2, null), new RttSample(3, 30)], 100, 20);

        Assert.Equal(["0,20", "100,0"], geometry.Segments);
        Assert.Equal(["50"], geometry.Outages);
        Assert.Equal("100", geometry.LatestX);
        Assert.Equal("0", geometry.LatestY);
    }

    [Fact]
    public void An_all_lost_window_has_no_line_only_outage_markers()
    {
        var geometry = SparklineGeometry.Build([new RttSample(1, null), new RttSample(2, null)], 10, 10);

        Assert.Empty(geometry.Segments);
        Assert.Equal(["0", "10"], geometry.Outages);
        Assert.Null(geometry.LatestX);
    }

    [Fact]
    public void A_single_clean_sample_sits_at_x_zero_on_the_mid_line()
    {
        // count == 1: the x step is 0 and the value range is degenerate (span == 0), so the point
        // lands on the mid-line. This is the first-tick-after-connect case the component renders.
        var geometry = SparklineGeometry.Build([new RttSample(1, 10)], 100, 30);

        Assert.Equal(["0,15"], geometry.Segments);
        Assert.Empty(geometry.Outages);
        Assert.Equal("0", geometry.LatestX);
        Assert.Equal("15", geometry.LatestY);
    }

    [Fact]
    public void A_single_lost_sample_is_one_outage_with_no_line()
    {
        var geometry = SparklineGeometry.Build([new RttSample(1, null)], 100, 30);

        Assert.Empty(geometry.Segments);
        Assert.Equal(["0"], geometry.Outages);
        Assert.Null(geometry.LatestX);
        Assert.Null(geometry.LatestY);
    }

    [Fact]
    public void A_leading_loss_marks_an_outage_before_the_line_starts()
    {
        var geometry = SparklineGeometry.Build(
            [new RttSample(1, null), new RttSample(2, 10), new RttSample(3, 20)], 100, 20);

        Assert.Equal(["0"], geometry.Outages);
        Assert.Equal(["50,20 100,0"], geometry.Segments);
        Assert.Equal("100", geometry.LatestX);
    }

    [Fact]
    public void A_trailing_loss_keeps_the_current_dot_on_the_last_clean_sample()
    {
        // The "current" dot must track the most recent CLEAN sample, never a trailing outage.
        var geometry = SparklineGeometry.Build(
            [new RttSample(1, 10), new RttSample(2, 20), new RttSample(3, null)], 100, 20);

        Assert.Equal(["0,20 50,0"], geometry.Segments);
        Assert.Equal(["100"], geometry.Outages);
        Assert.Equal("50", geometry.LatestX);
        Assert.Equal("0", geometry.LatestY);
    }

    [Fact]
    public void Coordinates_are_rounded_and_use_an_invariant_decimal_point()
    {
        // Four samples over width 100 give a non-integer x step (100/3 = 33.33…), exercising the
        // 2-dp rounding and the invariant-culture '.' separator (never a locale comma).
        var geometry = SparklineGeometry.Build(
            [new RttSample(1, 0), new RttSample(2, 10), new RttSample(3, 20), new RttSample(4, 30)],
            100, 30);

        var points = Assert.Single(geometry.Segments);
        Assert.Contains("33.33", points);
        Assert.Contains("66.67", points);
        Assert.DoesNotContain(",,", points); // no malformed/empty coordinate
    }

    [Fact]
    public void Build_rejects_null_samples_and_non_positive_dimensions()
    {
        Assert.Throws<ArgumentNullException>(() => SparklineGeometry.Build(null!, 100, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparklineGeometry.Build([], 0, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparklineGeometry.Build([], 100, -1));
    }
}
