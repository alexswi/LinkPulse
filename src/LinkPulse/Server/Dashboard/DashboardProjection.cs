using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// Projects the registry's <see cref="ConnectionView"/> snapshot into the ordered, filtered set of
/// dashboard rows (v1 spec, &#167;10). Pure and deterministic given the current time, so the whole
/// sort/filter behaviour is unit-testable without a renderer.
/// </summary>
internal static class DashboardProjection
{
    /// <summary>
    /// Builds the table rows: project each view to a <see cref="ConnectionRow"/>, apply the
    /// <paramref name="filter"/>, then sort by <paramref name="sortColumn"/>. The default
    /// (<see cref="DashboardColumn.Quality"/>, ascending) lists worst quality first &#8212; operators
    /// care about problems. A deterministic tie-break (most-recently-seen first, then ClientId) keeps
    /// the order stable across refreshes.
    /// </summary>
    /// <param name="views">The registry snapshot to project.</param>
    /// <param name="nowUtc">The current server-clock time, used for the last-seen-age and uptime sort keys.</param>
    /// <param name="thresholds">The quality tier boundaries used to rate each row.</param>
    /// <param name="filter">The active quality/liveness filter.</param>
    /// <param name="sortColumn">The column to sort by.</param>
    /// <param name="descending">Whether to sort the chosen column descending.</param>
    public static IReadOnlyList<ConnectionRow> Project(
        IReadOnlyList<ConnectionView> views,
        DateTimeOffset nowUtc,
        QualityThresholds thresholds,
        DashboardFilter filter,
        DashboardColumn sortColumn = DashboardColumn.Quality,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(thresholds);

        var rows = new List<ConnectionRow>(views.Count);
        foreach (var view in views)
        {
            var row = ConnectionRow.From(view, thresholds);
            if (filter.Matches(row))
            {
                rows.Add(row);
            }
        }

        // Sort by the chosen column's natural displayed order. Last-seen and uptime sort by the
        // displayed age/duration (now − timestamp), so "descending" means oldest/longest first as a
        // reader expects, rather than by the raw timestamp.
        return sortColumn switch
        {
            DashboardColumn.Quality => Sort(rows, r => (int)r.Rating, descending),
            DashboardColumn.ClientId => Sort(rows, r => r.ClientId, descending),
            DashboardColumn.Sessions => Sort(rows, r => r.ActiveSessionCount, descending),
            DashboardColumn.Phase => Sort(rows, r => (int)r.Phase, descending),
            DashboardColumn.Rtt => Sort(rows, r => r.RttAvg ?? double.MaxValue, descending),
            DashboardColumn.Jitter => Sort(rows, r => r.Jitter ?? double.MaxValue, descending),
            DashboardColumn.Loss => Sort(rows, r => r.LossPct ?? double.MaxValue, descending),
            DashboardColumn.LastSeen => Sort(rows, r => nowUtc - r.LastSeenUtc, descending),
            DashboardColumn.Uptime => Sort(rows, r => nowUtc - r.FirstSeenUtc, descending),
            _ => Sort(rows, r => (int)r.Rating, descending),
        };
    }

    private static IReadOnlyList<ConnectionRow> Sort<TKey>(
        List<ConnectionRow> rows, Func<ConnectionRow, TKey> key, bool descending)
    {
        var ordered = descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

        // Stable, direction-independent tie-break so equal keys never reshuffle between refreshes.
        return [.. ordered.ThenByDescending(r => r.LastSeenUtc).ThenBy(r => r.ClientId)];
    }
}
