using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// An immutable, point-in-time copy of a single registry entry (v1 spec, &#167;9), as exposed to
/// readers such as the dashboard (#6). It is a defensive snapshot: the live entry keeps mutating
/// behind its lock, but a <see cref="ConnectionView"/> never changes after it is handed out, so a
/// reader can sort, filter, and render it without racing the probe handlers writing to the registry.
/// </summary>
public sealed record ConnectionView
{
    /// <summary>The stable per-browser identifier this entry is grouped by.</summary>
    public required Guid ClientId { get; init; }

    /// <summary>
    /// The currently-open probe sessions for this client. More than one means the same browser has
    /// several tabs connected at once; an empty set means every socket has closed but the entry is
    /// retained pending the stale/retention lifecycle (&#167;5.3).
    /// </summary>
    public required IReadOnlyList<Guid> ActiveSessions { get; init; }

    /// <summary>The most recently reported metrics, or <see langword="null"/> if none have arrived yet.</summary>
    public MetricSnapshot? LatestSnapshot { get; init; }

    /// <summary>The render phase of the most recent snapshot (&#167;3.5).</summary>
    public ClientPhase Phase { get; init; }

    /// <summary>Server-clock time the entry was first created.</summary>
    public required DateTimeOffset FirstSeenUtc { get; init; }

    /// <summary>Server-clock time the most recent snapshot was received.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>
    /// <see langword="true"/> when no snapshot has arrived within <c>StaleThresholdMs</c> (&#167;5.3);
    /// the entry is retained for <c>StaleRetentionMs</c> after this before removal.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>The rolling history (oldest first, at most 60 points) for the sparkline and outage markers.</summary>
    public required IReadOnlyList<ConnectionHistoryPoint> History { get; init; }

    /// <summary>
    /// The connection's reported <c>User-Agent</c>, bounded as untrusted input (&#167;11), or
    /// <see langword="null"/> if none was sent. The only potentially identifying field, surfaced on the
    /// dashboard only when the operator toggles it on (&#167;10).
    /// </summary>
    public string? UserAgent { get; init; }
}
