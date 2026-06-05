using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the pure dashboard projection (&#167;10): how registry views are rated, filtered, and sorted
/// into table rows. Deterministic — every timestamp is explicit — so the worst-first default, the
/// quality/liveness filters, and per-column sorting are verified without a renderer.
/// </summary>
public sealed class DashboardProjectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly QualityThresholds Thresholds = QualityThresholds.Default;

    // rttAvg drives the rating (jitter/loss kept good): <50 Excellent, <150 Good, <300 Fair, else Poor.
    private static ConnectionView View(
        double? rttAvg = 20,
        bool stale = false,
        int sessions = 1,
        ClientPhase phase = ClientPhase.Server,
        Guid? clientId = null,
        DateTimeOffset? firstSeen = null,
        DateTimeOffset? lastSeen = null)
    {
        MetricSnapshot? snapshot = rttAvg is double r
            ? new MetricSnapshot { Phase = phase, RttMin = r, RttAvg = r, RttMax = r, Jitter = 1, LossPct = 0, SampleCount = 30 }
            : null;

        return new ConnectionView
        {
            ClientId = clientId ?? Guid.NewGuid(),
            ActiveSessions = [.. Enumerable.Range(0, sessions).Select(_ => Guid.NewGuid())],
            LatestSnapshot = snapshot,
            Phase = phase,
            FirstSeenUtc = firstSeen ?? T0,
            LastSeenUtc = lastSeen ?? T0,
            IsStale = stale,
            History = [],
        };
    }

    private static IReadOnlyList<ConnectionRow> Project(
        IReadOnlyList<ConnectionView> views,
        DashboardFilter? filter = null,
        DashboardColumn sort = DashboardColumn.Quality,
        bool descending = false) =>
        DashboardProjection.Project(views, T0, Thresholds, filter ?? DashboardFilter.None, sort, descending);

    [Fact]
    public void A_stale_or_snapshotless_entry_is_rated_disconnected()
    {
        var rows = Project([View(stale: true), View(rttAvg: null)]);

        Assert.All(rows, row => Assert.Equal(QualityRating.Disconnected, row.Rating));
    }

    [Fact]
    public void The_default_sort_lists_worst_quality_first()
    {
        // One of each rating, supplied best-first to prove the sort reorders them.
        var views = new[]
        {
            View(rttAvg: 20),    // Excellent
            View(rttAvg: 100),   // Good
            View(rttAvg: 200),   // Fair
            View(rttAvg: 400),   // Poor
            View(stale: true),   // Disconnected
        };

        var ratings = Project(views).Select(r => r.Rating);

        Assert.Equal(
            new[] { QualityRating.Disconnected, QualityRating.Poor, QualityRating.Fair, QualityRating.Good, QualityRating.Excellent },
            ratings);
    }

    [Fact]
    public void Sorting_quality_descending_lists_best_first()
    {
        var views = new[] { View(rttAvg: 400), View(rttAvg: 20), View(rttAvg: 200) };

        var ratings = Project(views, sort: DashboardColumn.Quality, descending: true).Select(r => r.Rating);

        Assert.Equal(new[] { QualityRating.Excellent, QualityRating.Fair, QualityRating.Poor }, ratings);
    }

    [Fact]
    public void Sorting_by_rtt_ascending_orders_by_latency_with_unreported_last()
    {
        var views = new[] { View(rttAvg: 200), View(rttAvg: null), View(rttAvg: 20), View(rttAvg: 100) };

        var rtts = Project(views, sort: DashboardColumn.Rtt).Select(r => r.RttAvg);

        // Smallest first; the snapshotless row (null) sorts last.
        Assert.Equal(new double?[] { 20, 100, 200, null }, rtts);
    }

    [Fact]
    public void The_quality_filter_keeps_only_matching_rows()
    {
        var views = new[] { View(rttAvg: 20), View(rttAvg: 400), View(rttAvg: 410), View(stale: true) };

        var rows = Project(views, new DashboardFilter(QualityRating.Poor, LivenessFilter.All));

        Assert.All(rows, row => Assert.Equal(QualityRating.Poor, row.Rating));
        var poorCount = rows.Count;
        Assert.Equal(2, poorCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_liveness_filter_keeps_only_the_chosen_tier(bool expectStale)
    {
        var liveness = expectStale ? LivenessFilter.Stale : LivenessFilter.Live;
        var views = new[] { View(rttAvg: 20), View(stale: true), View(rttAvg: 100), View(stale: true) };

        var rows = Project(views, new DashboardFilter(null, liveness));

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(expectStale, row.IsStale));
    }

    [Fact]
    public void Ties_break_by_most_recently_seen_then_client_id()
    {
        // Same rating (all Excellent); only LastSeen distinguishes them.
        var newer = View(rttAvg: 20, lastSeen: T0.AddSeconds(10));
        var older = View(rttAvg: 20, lastSeen: T0);

        var rows = Project([older, newer]);

        // Most recently seen comes first within an equal-rating group.
        Assert.Equal(newer.ClientId, rows[0].ClientId);
        Assert.Equal(older.ClientId, rows[1].ClientId);
    }

    [Fact]
    public void A_sessionless_but_not_yet_stale_entry_keeps_its_last_known_rating()
    {
        // Zero active sessions, not stale, with a prior snapshot: it must keep its measured rating
        // (greyed last-known, §5.2), NOT flip to Disconnected. This is the one nuance the §6/§5.2 docs
        // single out, so it is pinned explicitly.
        var view = View(rttAvg: 20, sessions: 0, stale: false);

        var row = Assert.Single(Project([view]));

        Assert.Equal(QualityRating.Excellent, row.Rating);
    }

    [Fact]
    public void Sorting_by_last_seen_orders_by_age()
    {
        var older = View(rttAvg: 20, lastSeen: T0.AddSeconds(-50)); // age 50s at T0
        var newer = View(rttAvg: 20, lastSeen: T0.AddSeconds(-10)); // age 10s at T0

        // Ascending age (default direction): youngest age first.
        var ascending = Project([older, newer], sort: DashboardColumn.LastSeen).Select(r => r.ClientId);
        Assert.Equal(new[] { newer.ClientId, older.ClientId }, ascending);

        // Descending age: oldest-seen first (what an operator hunting for problems expects).
        var descending = Project([older, newer], sort: DashboardColumn.LastSeen, descending: true).Select(r => r.ClientId);
        Assert.Equal(new[] { older.ClientId, newer.ClientId }, descending);
    }

    [Fact]
    public void Sorting_by_uptime_descending_lists_the_longest_lived_first()
    {
        var longLived = View(rttAvg: 20, firstSeen: T0.AddSeconds(-100)); // uptime 100s
        var shortLived = View(rttAvg: 20, firstSeen: T0.AddSeconds(-10)); // uptime 10s

        var order = Project([shortLived, longLived], sort: DashboardColumn.Uptime, descending: true).Select(r => r.ClientId);

        Assert.Equal(new[] { longLived.ClientId, shortLived.ClientId }, order);
    }

    [Fact]
    public void The_quality_and_liveness_filters_combine_as_an_intersection()
    {
        var livePoor = View(rttAvg: 400);            // Poor, live
        var staleExcellent = View(rttAvg: 20, stale: true); // stale ⇒ rated Disconnected

        // Poor AND Stale: the Poor row is live (excluded) and the stale row is not Poor (excluded).
        Assert.Empty(Project([livePoor, staleExcellent], new DashboardFilter(QualityRating.Poor, LivenessFilter.Stale)));

        // Poor AND Live keeps exactly the live Poor row — proving the predicates AND rather than OR.
        var liveOnly = Project([livePoor, staleExcellent], new DashboardFilter(QualityRating.Poor, LivenessFilter.Live));
        Assert.Equal(livePoor.ClientId, Assert.Single(liveOnly).ClientId);
    }

    [Fact]
    public void Projecting_an_empty_registry_yields_no_rows()
    {
        Assert.Empty(Project([]));
    }
}
