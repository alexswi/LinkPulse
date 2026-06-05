using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// A single dashboard table row (v1 spec, &#167;10): the columns projected from a
/// <see cref="ConnectionView"/> plus its derived overall <see cref="Rating"/>. It is a self-contained
/// view model &#8212; it projects exactly the fields the table and its expandable detail need
/// (including the session list, history, and user-agent), so there is no second source of truth to
/// drift from and no need to reach back into the registry while rendering.
/// </summary>
internal sealed record ConnectionRow
{
    /// <summary>The client this row represents.</summary>
    public required Guid ClientId { get; init; }

    /// <summary>The overall quality rating (&#167;6), or <see cref="QualityRating.Disconnected"/> when stale/unreported.</summary>
    public required QualityRating Rating { get; init; }

    /// <summary>Whether the entry has gone stale (no snapshot within the threshold).</summary>
    public required bool IsStale { get; init; }

    /// <summary>The currently-active probe sessions for this client (its count is the "sessions" column).</summary>
    public required IReadOnlyList<Guid> ActiveSessions { get; init; }

    /// <summary>The most recent render phase reported.</summary>
    public required ClientPhase Phase { get; init; }

    /// <summary>Average RTT from the latest snapshot, or <see langword="null"/> when none has arrived.</summary>
    public double? RttAvg { get; init; }

    /// <summary>Jitter from the latest snapshot, or <see langword="null"/> when none has arrived.</summary>
    public double? Jitter { get; init; }

    /// <summary>Packet loss percentage from the latest snapshot, or <see langword="null"/> when none has arrived.</summary>
    public double? LossPct { get; init; }

    /// <summary>Server-clock time the entry was first seen.</summary>
    public required DateTimeOffset FirstSeenUtc { get; init; }

    /// <summary>Server-clock time the latest snapshot was received.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>The rolling history (oldest first) for the row-detail sparkline and outage markers.</summary>
    public required IReadOnlyList<ConnectionHistoryPoint> History { get; init; }

    /// <summary>The bounded user-agent (&#167;11), or <see langword="null"/>; shown in the detail only when toggled on.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Projects a registry <paramref name="view"/> into a table row, computing its overall rating.</summary>
    /// <param name="view">The immutable connection view to project.</param>
    /// <param name="thresholds">The quality tier boundaries to apply.</param>
    public static ConnectionRow From(ConnectionView view, QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(view);

        var snapshot = view.LatestSnapshot;
        return new ConnectionRow
        {
            ClientId = view.ClientId,
            Rating = DashboardQuality.Rate(view, thresholds),
            IsStale = view.IsStale,
            ActiveSessions = view.ActiveSessions,
            Phase = view.Phase,
            RttAvg = snapshot?.RttAvg,
            Jitter = snapshot?.Jitter,
            LossPct = snapshot?.LossPct,
            FirstSeenUtc = view.FirstSeenUtc,
            LastSeenUtc = view.LastSeenUtc,
            History = view.History,
            UserAgent = view.UserAgent,
        };
    }
}
