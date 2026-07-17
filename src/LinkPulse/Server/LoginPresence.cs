namespace LinkPulse.Server;

/// <summary>
/// An immutable, point-in-time presence summary for one authenticated login name, aggregated
/// across every registry entry (browser) that reported it (#26). This is the allocation-cheap
/// projection consumers should use for "is this user online?" checks: unlike
/// <see cref="ConnectionView"/> it carries no history, session list, or user-agent, only the
/// per-login scalars.
/// </summary>
/// <remarks>
/// A login name is retained latest-wins-non-empty per client (see
/// <see cref="ConnectionView.LoginName"/>): a reconnect with no identity leaves the last known
/// name in place. That is deliberate for the diagnostics dashboard, but it means presence can
/// report a false positive after a logout — a user signs out on a shared browser, the tab keeps
/// probing anonymously, and the entry still carries the old name while its sessions stay live.
/// Treat <see cref="IsOnline"/> as "a browser that last authenticated as this login is connected",
/// not as proof the user is still signed in.
/// </remarks>
/// <param name="LoginName">
/// The authenticated login name this summary aggregates, taken from the first entry encountered
/// in an unspecified order. Login names are compared case-insensitively (matching ASP.NET
/// Identity's username normalization), so the aggregate is order-independent; only the reported
/// casing is not guaranteed when a login's entries disagree on it.
/// </param>
/// <param name="IsOnline">
/// The canonical presence predicate: <see langword="true"/> when at least one of the login's
/// entries is not stale <em>and</em> has at least one active probe session. A session-less entry
/// that has not yet gone stale (&#167;5.2/&#167;5.3) does not count as online. This &#8212; not the
/// counts below &#8212; is the value consumers should test for online-ness.
/// </param>
/// <param name="ActiveSessionCount">
/// The total open probe sessions across the login's entries (tabs &#215; browsers), <em>including
/// sessions on stale-but-retained entries</em>. It can therefore be non-zero while
/// <see cref="IsOnline"/> is <see langword="false"/> (e.g. a hung socket on a client that has gone
/// silent), so do not derive online-ness from this count &#8212; use <see cref="IsOnline"/>.
/// </param>
/// <param name="ClientCount">
/// The number of distinct <c>ClientId</c>s (browsers) currently tracked for this login, including
/// stale entries still inside the retention window. Like <see cref="ActiveSessionCount"/> this
/// counts tracked entries, not online ones; use <see cref="IsOnline"/> to test presence.
/// </param>
/// <param name="LastSeenUtc">
/// The most recent server-clock snapshot time across the login's entries.
/// </param>
public sealed record LoginPresence(
    string LoginName,
    bool IsOnline,
    int ActiveSessionCount,
    int ClientCount,
    DateTimeOffset LastSeenUtc);
