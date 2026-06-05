using System.Collections.Concurrent;
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

    /// <summary>Creates a registry whose lifecycle thresholds come from <paramref name="options"/> (&#167;5.3).</summary>
    /// <param name="options">The configured stale threshold and retention window.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public LinkPulseRegistry(LinkPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _staleThreshold = TimeSpan.FromMilliseconds(options.StaleThresholdMs);
        _retention = TimeSpan.FromMilliseconds(options.StaleRetentionMs);
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
    public void RecordSnapshot(Guid clientId, Guid sessionId, MetricSnapshot snapshot, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entry = _entries.GetOrAdd(clientId, static (id, now) => new ConnectionEntry(id, now), nowUtc);
        entry.RecordSnapshot(sessionId, snapshot, nowUtc);
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

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
