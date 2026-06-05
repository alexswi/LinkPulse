using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// The dashboard summary bar (v1 spec, &#167;10): total connections, the live/stale split, and the
/// distribution across the five quality ratings. Computed purely from a row set so it is testable in
/// isolation.
/// </summary>
/// <param name="Total">Total tracked connections.</param>
/// <param name="Live">Connections that are not stale.</param>
/// <param name="Stale">Connections that have gone stale.</param>
/// <param name="Excellent">Connections rated <see cref="QualityRating.Excellent"/>.</param>
/// <param name="Good">Connections rated <see cref="QualityRating.Good"/>.</param>
/// <param name="Fair">Connections rated <see cref="QualityRating.Fair"/>.</param>
/// <param name="Poor">Connections rated <see cref="QualityRating.Poor"/>.</param>
/// <param name="Disconnected">Connections rated <see cref="QualityRating.Disconnected"/>.</param>
internal sealed record DashboardSummary(
    int Total,
    int Live,
    int Stale,
    int Excellent,
    int Good,
    int Fair,
    int Poor,
    int Disconnected)
{
    /// <summary>An all-zero summary, for an empty registry.</summary>
    public static DashboardSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Tallies the live/stale split and the quality distribution over <paramref name="rows"/>.</summary>
    /// <param name="rows">The projected rows to summarize (the filtered or full set, as the caller chooses).</param>
    public static DashboardSummary From(IReadOnlyList<ConnectionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int stale = 0, excellent = 0, good = 0, fair = 0, poor = 0, disconnected = 0;
        foreach (var row in rows)
        {
            if (row.IsStale)
            {
                stale++;
            }

            switch (row.Rating)
            {
                case QualityRating.Excellent: excellent++; break;
                case QualityRating.Good: good++; break;
                case QualityRating.Fair: fair++; break;
                case QualityRating.Poor: poor++; break;
                default: disconnected++; break;
            }
        }

        return new DashboardSummary(rows.Count, rows.Count - stale, stale, excellent, good, fair, poor, disconnected);
    }
}
