using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// The in-memory connection registry (v1 spec, &#167;9): a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by <c>ClientId</c>, holding each client's latest snapshot, active sessions, phase,
/// first/last-seen timestamps, a short rolling history, and a stale flag. It is registered as a
/// singleton by <see cref="LinkPulseServiceCollectionExtensions.AddLinkPulse(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>;
/// probe handlers write to it and the dashboard (#6) reads <see cref="ConnectionView"/> copies from it.
/// </summary>
/// <remarks>
/// <para>
/// Like the client measurement core, the registry never reads a clock itself: every mutation takes
/// the server-clock <c>nowUtc</c> from the caller, which keeps the stale/retention lifecycle
/// deterministic and testable on a compressed clock. The probe endpoint passes
/// <see cref="TimeProvider.GetUtcNow"/>; the background <see cref="LinkPulseSweepService"/> drives
/// <see cref="Sweep"/>.
/// </para>
/// <para>
/// The dictionary handles cross-client concurrency; each <see cref="ConnectionEntry"/> guards its own
/// state with a lock. <see cref="Changed"/> is raised after any mutation so a dashboard can refresh
/// without polling; handlers must be cheap and must not throw.
/// </para>
/// </remarks>
public sealed class LinkPulseRegistry
{
    private readonly ConcurrentDictionary<Guid, ConnectionEntry> _entries = new();
    private readonly TimeSpan _staleThreshold;
    private readonly TimeSpan _retention;
    private readonly int _maxClients;

    /// <summary>
    /// Creates a registry whose lifecycle thresholds and capacity come from <paramref name="options"/>
    /// (&#167;5.3, &#167;11), enforcing the documented bounds the options record itself does not.
    /// </summary>
    /// <param name="options">The configured stale threshold, retention window, and client cap.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A threshold is negative, or the client cap is below 1.</exception>
    public LinkPulseRegistry(LinkPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StaleThresholdMs);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StaleRetentionMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTrackedClients, 1);
        _staleThreshold = TimeSpan.FromMilliseconds(options.StaleThresholdMs);
        _retention = TimeSpan.FromMilliseconds(options.StaleRetentionMs);
        _maxClients = options.MaxTrackedClients;
    }

    /// <summary>
    /// Raised after a mutation (a recorded snapshot, a closed session, or a sweep that changed
    /// anything). Intended for the dashboard's push updates (&#167;10); raised on whatever thread
    /// caused the change, so subscribers must marshal to their own context as needed.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The number of clients currently tracked (live and stale).</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Records a validated snapshot for <paramref name="clientId"/>, creating the entry on first sight
    /// and updating it in place on reconnect (&#167;4) &#8212; clearing any stale flag and marking the
    /// gap as an outage (&#167;5.3).
    /// </summary>
    /// <param name="clientId">The validated stable client identifier.</param>
    /// <param name="sessionId">The validated per-connection identifier.</param>
    /// <param name="snapshot">The validated metrics to store.</param>
    /// <param name="nowUtc">The server-clock time the snapshot was received.</param>
    /// <param name="userAgent">
    /// The connection's bounded <c>User-Agent</c> string (&#167;10/&#167;11), or <see langword="null"/>
    /// when unknown; the latest non-empty value is retained on the entry for the dashboard's optional
    /// user-agent column.
    /// </param>
    /// <param name="clientIp">
    /// The connection's remote address as the server sees it (&#167;10), or <see langword="null"/> when
    /// unavailable; the latest non-empty value is retained on the entry for the dashboard's IP column.
    /// </param>
    /// <param name="loginName">
    /// The connection's authenticated login name (&#167;10), or <see langword="null"/> when the client is
    /// anonymous; the latest non-empty value is retained on the entry for the dashboard's login column.
    /// </param>
    public void RecordSnapshot(
        Guid clientId, Guid sessionId, MetricSnapshot snapshot, DateTimeOffset nowUtc,
        string? userAgent = null, string? clientIp = null, string? loginName = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_entries.TryGetValue(clientId, out var entry))
        {
            // §11 abuse bound: cap the number of distinct clients so an attacker minting unlimited
            // random ClientIds cannot grow the registry without limit (entries linger for the whole
            // retention window). Updates to already-tracked clients are always allowed.
            if (_entries.Count >= _maxClients)
            {
                return;
            }

            entry = _entries.GetOrAdd(clientId, static (id, now) => new ConnectionEntry(id, now), nowUtc);
        }

        entry.RecordSnapshot(sessionId, snapshot, nowUtc, userAgent, clientIp, loginName);
        OnChanged();
    }

    /// <summary>
    /// Removes a closed session from its client's active set (called when a probe socket closes). The
    /// entry is retained for the stale lifecycle; a no-op if the client or session is unknown.
    /// </summary>
    /// <param name="clientId">The client whose session closed.</param>
    /// <param name="sessionId">The session that closed.</param>
    public void RemoveSession(Guid clientId, Guid sessionId)
    {
        if (_entries.TryGetValue(clientId, out var entry))
        {
            entry.RemoveSession(sessionId);
            OnChanged();
        }
    }

    /// <summary>
    /// Removes a client entry outright. This backs the dashboard's manual "remove stale entry" action
    /// (&#167;10) &#8212; the only registry mutation a v1 operator can trigger &#8212; and raises
    /// <see cref="Changed"/> so the dashboard reflects the removal at once.
    /// </summary>
    /// <param name="clientId">The client to remove.</param>
    /// <returns><see langword="true"/> if an entry was removed; <see langword="false"/> if the client was unknown.</returns>
    public bool Remove(Guid clientId)
    {
        if (_entries.TryRemove(clientId, out _))
        {
            OnChanged();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Advances the lifecycle against the current clock (&#167;5.3): flips silent entries to stale
    /// after <c>StaleThresholdMs</c> and removes them after <c>StaleRetentionMs</c>. Idempotent and
    /// safe to call as often as the caller likes; <see cref="Changed"/> is raised once if anything moved.
    /// </summary>
    /// <param name="nowUtc">The current server-clock time.</param>
    public void Sweep(DateTimeOffset nowUtc)
    {
        var mutated = false;
        foreach (var (clientId, entry) in _entries)
        {
            if (entry.IsExpired(nowUtc, _retention))
            {
                // TryRemove by key/value pair so a snapshot racing in on another thread (which would
                // have refreshed last-seen) is not silently dropped along with the stale entry.
                mutated |= _entries.TryRemove(KeyValuePair.Create(clientId, entry));
            }
            else
            {
                mutated |= entry.MarkStaleIfDue(nowUtc, _staleThreshold);
            }
        }

        if (mutated)
        {
            OnChanged();
        }
    }

    /// <summary>Takes an immutable snapshot of every tracked connection, for the dashboard to render.</summary>
    /// <returns>An independent list of views; safe to sort, filter, and hold.</returns>
    public IReadOnlyList<ConnectionView> GetConnections()
    {
        var views = new List<ConnectionView>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            views.Add(entry.ToView());
        }

        return views;
    }

    /// <summary>Takes an immutable snapshot of a single client, if it is tracked.</summary>
    /// <param name="clientId">The client to look up.</param>
    /// <param name="view">The immutable view on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the client is tracked; otherwise <see langword="false"/>.</returns>
    public bool TryGetConnection(Guid clientId, out ConnectionView? view)
    {
        if (_entries.TryGetValue(clientId, out var entry))
        {
            view = entry.ToView();
            return true;
        }

        view = null;
        return false;
    }

    /// <summary>
    /// Takes an allocation-cheap presence summary of every <em>authenticated</em> login currently
    /// tracked (#26), keyed case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>,
    /// matching ASP.NET Identity's username normalization). Anonymous entries (no
    /// <c>LoginName</c>) are excluded. Unlike <see cref="GetConnections"/> this copies no history,
    /// session lists, or user-agents &#8212; only per-entry scalars are read under each entry's lock
    /// &#8212; so it is suitable for per-request presence checks. Subscribe to <see cref="Changed"/>
    /// to refresh presence live.
    /// </summary>
    /// <remarks>
    /// Because login names are retained latest-wins-non-empty per client (see
    /// <see cref="ConnectionView.LoginName"/>), a browser whose user signed out but that keeps
    /// probing anonymously still counts toward its last known login &#8212; see the caveat on
    /// <see cref="LoginPresence"/>.
    /// </remarks>
    /// <returns>An independent snapshot; safe to hold and query.</returns>
    public IReadOnlyDictionary<string, LoginPresence> GetLoginPresences()
    {
        var presences = new Dictionary<string, LoginPresence>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Values)
        {
            var scalars = entry.ReadPresenceScalars();
            if (string.IsNullOrEmpty(scalars.LoginName))
            {
                continue;
            }

            presences[scalars.LoginName] = Merge(
                presences.TryGetValue(scalars.LoginName, out var current) ? current : null,
                scalars);
        }

        return presences;
    }

    /// <summary>
    /// Takes the presence summary for a single login name (#26), compared case-insensitively.
    /// Cheaper than <see cref="GetLoginPresences"/> when only one login is of interest: it scans
    /// the per-entry scalars without building the full dictionary.
    /// </summary>
    /// <param name="loginName">The login name to look up.</param>
    /// <param name="presence">The aggregated summary on success; otherwise <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> if any tracked entry carries <paramref name="loginName"/>; otherwise
    /// <see langword="false"/> (including for an empty name &#8212; anonymous entries are never matched).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="loginName"/> is <see langword="null"/>.</exception>
    public bool TryGetLoginPresence(string loginName, [NotNullWhen(true)] out LoginPresence? presence)
    {
        ArgumentNullException.ThrowIfNull(loginName);

        presence = null;
        if (loginName.Length == 0)
        {
            return false;
        }

        foreach (var entry in _entries.Values)
        {
            var scalars = entry.ReadPresenceScalars();
            if (string.Equals(scalars.LoginName, loginName, StringComparison.OrdinalIgnoreCase))
            {
                presence = Merge(presence, scalars);
            }
        }

        return presence is not null;
    }

    /// <summary>
    /// Folds one entry's scalars into a login's running summary. The first entry seen fixes the
    /// reported casing; <c>IsOnline</c> is the canonical predicate (any non-stale entry with at
    /// least one active session).
    /// </summary>
    private static LoginPresence Merge(LoginPresence? current, ConnectionEntry.PresenceScalars scalars)
    {
        var online = !scalars.IsStale && scalars.ActiveSessionCount > 0;
        return current is null
            ? new LoginPresence(scalars.LoginName!, online, scalars.ActiveSessionCount, ClientCount: 1, scalars.LastSeenUtc)
            : current with
            {
                IsOnline = current.IsOnline || online,
                ActiveSessionCount = current.ActiveSessionCount + scalars.ActiveSessionCount,
                ClientCount = current.ClientCount + 1,
                LastSeenUtc = scalars.LastSeenUtc > current.LastSeenUtc ? scalars.LastSeenUtc : current.LastSeenUtc,
            };
    }

    private void OnChanged()
    {
        // Notification is best-effort: a misbehaving subscriber (e.g. a buggy dashboard handler) must
        // not throw out of here and tear down the probe connection that is feeding the registry. The
        // contract is still "handlers must be cheap and must not throw"; this only contains the blast
        // radius if one breaks it.
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Swallow: a notification failure is not a registry failure.
        }
    }
}
