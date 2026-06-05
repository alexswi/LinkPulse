using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// Which liveness tier the dashboard table shows (v1 spec, &#167;10): everything, only live
/// connections, or only stale ones.
/// </summary>
internal enum LivenessFilter
{
    /// <summary>Show both live and stale connections. Listed first so <c>default(DashboardFilter)</c> is unfiltered.</summary>
    All,

    /// <summary>Show only connections that are not stale.</summary>
    Live,

    /// <summary>Show only stale connections.</summary>
    Stale,
}

/// <summary>
/// The active table filter (v1 spec, &#167;10): an optional quality level and a liveness tier. The
/// default (<see cref="Quality"/> <see langword="null"/>, <see cref="Liveness"/>
/// <see cref="LivenessFilter.All"/>) shows everything.
/// </summary>
/// <param name="Quality">When set, keep only rows whose overall rating equals this value.</param>
/// <param name="Liveness">Which liveness tier to keep.</param>
internal readonly record struct DashboardFilter(QualityRating? Quality, LivenessFilter Liveness)
{
    /// <summary>The default, unfiltered view: every quality level, both live and stale.</summary>
    public static DashboardFilter None => new(null, LivenessFilter.All);

    /// <summary>Reports whether <paramref name="row"/> passes both the quality and liveness predicates.</summary>
    /// <param name="row">The projected row to test.</param>
    public bool Matches(ConnectionRow row)
    {
        if (Quality is { } q && row.Rating != q)
        {
            return false;
        }

        return Liveness switch
        {
            LivenessFilter.Live => !row.IsStale,
            LivenessFilter.Stale => row.IsStale,
            _ => true,
        };
    }
}
