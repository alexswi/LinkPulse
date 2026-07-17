using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// The mutable, lock-guarded state the <see cref="LinkPulseRegistry"/> keeps per <c>ClientId</c>
/// (v1 spec, &#167;9). All access goes through the private gate, so concurrent probe handlers writing
/// snapshots and dashboard readers taking <see cref="ToView"/> copies never tear each other's state.
/// Readers only ever see the immutable <see cref="ConnectionView"/>; the live entry stays internal.
/// </summary>
internal sealed class ConnectionEntry
{
    /// <summary>Maximum points kept in the rolling history (§9: "last 60 snapshots").</summary>
    private const int MaxHistory = 60;

    private readonly object _gate = new();
    private readonly Guid _clientId;
    private readonly HashSet<Guid> _sessions = [];
    private readonly Queue<ConnectionHistoryPoint> _history = new();

    private MetricSnapshot? _latest;
    private ClientPhase _phase;
    private DateTimeOffset _firstSeen;
    private DateTimeOffset _lastSeen;
    private bool _stale;
    private string? _userAgent;
    private string? _clientIp;
    private string? _loginName;

    internal ConnectionEntry(Guid clientId, DateTimeOffset nowUtc)
    {
        _clientId = clientId;
        _firstSeen = nowUtc;
        _lastSeen = nowUtc;
    }

    /// <summary>
    /// Applies a validated snapshot: registers the session, refreshes the latest metrics, phase, and
    /// last-seen, appends a history point, and &#8212; if the entry had gone stale &#8212; clears the
    /// stale flag and records an outage marker for the gap (&#167;5.3) <em>before</em> the new point.
    /// The latest non-empty <paramref name="userAgent"/>, <paramref name="clientIp"/>, and
    /// <paramref name="loginName"/> are retained for the dashboard (&#167;10).
    /// </summary>
    internal void RecordSnapshot(
        Guid sessionId, MetricSnapshot snapshot, DateTimeOffset nowUtc,
        string? userAgent = null, string? clientIp = null, string? loginName = null)
    {
        lock (_gate)
        {
            if (_stale)
            {
                _stale = false;
                Append(ConnectionHistoryPoint.Outage(nowUtc));
            }

            _sessions.Add(sessionId);
            _latest = snapshot;
            _phase = snapshot.Phase;
            _lastSeen = nowUtc;
            if (!string.IsNullOrEmpty(userAgent))
            {
                _userAgent = userAgent;
            }

            if (!string.IsNullOrEmpty(clientIp))
            {
                _clientIp = clientIp;
            }

            if (!string.IsNullOrEmpty(loginName))
            {
                _loginName = loginName;
            }

            Append(new ConnectionHistoryPoint(nowUtc, snapshot));
        }
    }

    /// <summary>Removes a closed session from the active set. The entry itself is retained for the stale lifecycle.</summary>
    internal void RemoveSession(Guid sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <summary>
    /// Flips the entry to stale when it has been silent for longer than <paramref name="staleThreshold"/>.
    /// </summary>
    /// <returns><see langword="true"/> if this call changed the stale flag.</returns>
    internal bool MarkStaleIfDue(DateTimeOffset nowUtc, TimeSpan staleThreshold)
    {
        lock (_gate)
        {
            if (_stale || nowUtc - _lastSeen <= staleThreshold)
            {
                return false;
            }

            _stale = true;
            return true;
        }
    }

    /// <summary>Reports whether the entry has been silent long enough to be removed entirely (&#167;5.3).</summary>
    internal bool IsExpired(DateTimeOffset nowUtc, TimeSpan retention)
    {
        lock (_gate)
        {
            return nowUtc - _lastSeen > retention;
        }
    }

    /// <summary>Takes an immutable, independent copy of the current state for readers.</summary>
    internal ConnectionView ToView()
    {
        lock (_gate)
        {
            return new ConnectionView
            {
                ClientId = _clientId,
                ActiveSessions = [.. _sessions],
                LatestSnapshot = _latest,
                Phase = _phase,
                FirstSeenUtc = _firstSeen,
                LastSeenUtc = _lastSeen,
                IsStale = _stale,
                History = [.. _history],
                UserAgent = _userAgent,
                ClientIp = _clientIp,
                LoginName = _loginName,
            };
        }
    }

    /// <summary>
    /// Reads only the scalars the login-presence projection needs (#26) &#8212; no history,
    /// session-list, or user-agent copies, so presence checks stay allocation-cheap.
    /// </summary>
    internal PresenceScalars ReadPresenceScalars()
    {
        lock (_gate)
        {
            return new PresenceScalars(_loginName, _sessions.Count, _stale, _lastSeen);
        }
    }

    /// <summary>The per-entry scalars the presence projection aggregates per login name (#26).</summary>
    internal readonly record struct PresenceScalars(
        string? LoginName, int ActiveSessionCount, bool IsStale, DateTimeOffset LastSeenUtc);

    private void Append(ConnectionHistoryPoint point)
    {
        _history.Enqueue(point);
        while (_history.Count > MaxHistory)
        {
            _history.Dequeue();
        }
    }
}
