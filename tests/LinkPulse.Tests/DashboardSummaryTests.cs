using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the dashboard summary bar tallies (&#167;10): total connections, the live/stale split, and the
/// distribution across the five quality ratings. Built from projected rows, so it is verified purely.
/// </summary>
public sealed class DashboardSummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ConnectionRow Row(QualityRating rating, bool stale) => new()
    {
        ClientId = Guid.NewGuid(),
        Rating = rating,
        IsStale = stale,
        ActiveSessions = [],
        Phase = ClientPhase.Server,
        FirstSeenUtc = T0,
        LastSeenUtc = T0,
        History = [],
    };

    [Fact]
    public void An_empty_row_set_summarizes_to_all_zeroes()
    {
        var summary = DashboardSummary.From([]);

        Assert.Equal(DashboardSummary.Empty, summary);
    }

    [Fact]
    public void The_summary_counts_totals_the_live_stale_split_and_the_quality_distribution()
    {
        ConnectionRow[] rows =
        [
            Row(QualityRating.Excellent, stale: false),
            Row(QualityRating.Good, stale: false),
            Row(QualityRating.Good, stale: false),
            Row(QualityRating.Fair, stale: false),
            Row(QualityRating.Poor, stale: false),
            Row(QualityRating.Disconnected, stale: true),
            Row(QualityRating.Disconnected, stale: true),
        ];

        var summary = DashboardSummary.From(rows);

        Assert.Equal(new DashboardSummary(Total: 7, Live: 5, Stale: 2, Excellent: 1, Good: 2, Fair: 1, Poor: 1, Disconnected: 2), summary);
    }
}
