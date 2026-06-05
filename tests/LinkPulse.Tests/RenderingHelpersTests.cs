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
    public void Build_rejects_null_samples_and_non_positive_dimensions()
    {
        Assert.Throws<ArgumentNullException>(() => SparklineGeometry.Build(null!, 100, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparklineGeometry.Build([], 0, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparklineGeometry.Build([], 100, -1));
    }
}
