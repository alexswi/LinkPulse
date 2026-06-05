using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// A single dashboard table row (v1 spec, &#167;10): the columns projected from a
/// <see cref="ConnectionView"/> plus its derived overall <see cref="Rating"/>. The source
/// <see cref="View"/> is carried along so the expandable row detail can render the per-session list,
/// history sparkline, and user-agent without a second registry lookup.
/// </summary>
internal sealed record ConnectionRow
{
    /// <summary>The client this row represents.</summary>
    public required Guid ClientId { get; init; }

    /// <summary>The overall quality rating (&#167;6), or <see cref="QualityRating.Disconnected"/> when stale/unreported.</summary>
    public required QualityRating Rating { get; init; }

    /// <summary>Whether the entry has gone stale (no snapshot within the threshold).</summary>
    public required bool IsStale { get; init; }

    /// <summary>Number of currently-active probe sessions for this client.</summary>
    public required int ActiveSessionCount { get; init; }

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

    /// <summary>The source view, for the expandable row detail.</summary>
    public required ConnectionView View { get; init; }

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
            ActiveSessionCount = view.ActiveSessions.Count,
            Phase = view.Phase,
            RttAvg = snapshot?.RttAvg,
            Jitter = snapshot?.Jitter,
            LossPct = snapshot?.LossPct,
            FirstSeenUtc = view.FirstSeenUtc,
            LastSeenUtc = view.LastSeenUtc,
            View = view,
        };
    }
}
