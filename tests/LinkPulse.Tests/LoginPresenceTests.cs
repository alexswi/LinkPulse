using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the login-presence projection (#26): the canonical online predicate, per-login
/// aggregation across clients, case-insensitive keying, and the exclusion of anonymous entries.
/// Like the registry lifecycle tests, everything runs deterministically on a compressed clock.
/// </summary>
public sealed class LoginPresenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Compressed thresholds: stale after 1 s of silence, removed 5 s after that.
    private static LinkPulseRegistry NewRegistry() =>
        new(new LinkPulseOptions { StaleThresholdMs = 1_000, StaleRetentionMs = 5_000 });

    private static MetricSnapshot Snapshot() => new()
    {
        Phase = ClientPhase.Server,
        RttMin = 20,
        RttAvg = 20,
        RttMax = 20,
        Jitter = 1,
        LossPct = 0,
        SampleCount = 30,
    };

    [Fact]
    public void A_live_entry_with_an_active_session_reports_its_login_as_online()
    {
        var registry = NewRegistry();
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: "alice");

        var presences = registry.GetLoginPresences();

        var presence = Assert.Single(presences).Value;
        Assert.Equal("alice", presence.LoginName);
        Assert.True(presence.IsOnline);
        Assert.Equal(1, presence.ActiveSessionCount);
        Assert.Equal(1, presence.ClientCount);
        Assert.Equal(T0, presence.LastSeenUtc);
    }

    [Fact]
    public void Anonymous_entries_are_excluded_entirely()
    {
        var registry = NewRegistry();
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: null);
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: "alice");

        var presences = registry.GetLoginPresences();

        Assert.Equal(2, registry.Count);
        Assert.Single(presences);
        Assert.True(presences.ContainsKey("alice"));
    }

    [Fact]
    public void One_login_across_several_clients_and_tabs_is_aggregated_into_one_summary()
    {
        var registry = NewRegistry();
        var laptop = Guid.NewGuid();
        var phone = Guid.NewGuid();

        // Two tabs on the laptop, one on the phone; the phone reported most recently.
        registry.RecordSnapshot(laptop, Guid.NewGuid(), Snapshot(), T0, loginName: "alice");
        registry.RecordSnapshot(laptop, Guid.NewGuid(), Snapshot(), T0.AddMilliseconds(100), loginName: "alice");
        registry.RecordSnapshot(phone, Guid.NewGuid(), Snapshot(), T0.AddMilliseconds(200), loginName: "alice");

        var presences = registry.GetLoginPresences();

        var presence = Assert.Single(presences).Value;
        Assert.True(presence.IsOnline);
        Assert.Equal(3, presence.ActiveSessionCount);
        Assert.Equal(2, presence.ClientCount);
        Assert.Equal(T0.AddMilliseconds(200), presence.LastSeenUtc);
    }

    [Fact]
    public void Login_names_differing_only_in_casing_fold_into_one_summary()
    {
        var registry = NewRegistry();
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: "Alice");
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0.AddMilliseconds(100), loginName: "ALICE");

        var presences = registry.GetLoginPresences();

        var presence = Assert.Single(presences).Value;
        Assert.Equal(2, presence.ClientCount);
        // The dictionary matches under any casing; the summary keeps the first casing seen.
        Assert.True(presences.ContainsKey("alice"));
        Assert.Equal("alice", presence.LoginName, ignoreCase: true);
    }

    [Fact]
    public void A_session_less_entry_that_is_not_yet_stale_does_not_count_as_online()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        registry.RecordSnapshot(clientId, sessionId, Snapshot(), T0, loginName: "alice");

        // The socket closes but the entry has not gone stale yet (§5.2/§5.3): tracked, not online.
        registry.RemoveSession(clientId, sessionId);

        Assert.True(registry.TryGetLoginPresence("alice", out var presence));
        Assert.False(presence!.IsOnline);
        Assert.Equal(0, presence.ActiveSessionCount);
        Assert.Equal(1, presence.ClientCount);
    }

    [Fact]
    public void A_stale_entry_does_not_count_as_online_even_with_a_lingering_session()
    {
        var registry = NewRegistry();
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: "alice");

        // Silent past the stale threshold; the session was never removed (e.g. a hung socket).
        registry.Sweep(T0.AddMilliseconds(1_001));

        Assert.True(registry.TryGetLoginPresence("alice", out var presence));
        Assert.False(presence!.IsOnline);
        Assert.Equal(1, presence.ActiveSessionCount);
    }

    [Fact]
    public void One_online_entry_makes_the_login_online_despite_other_stale_clients()
    {
        var registry = NewRegistry();
        var oldBrowser = Guid.NewGuid();
        var newBrowser = Guid.NewGuid();
        registry.RecordSnapshot(oldBrowser, Guid.NewGuid(), Snapshot(), T0, loginName: "alice");

        registry.Sweep(T0.AddMilliseconds(1_001)); // the old browser goes stale
        registry.RecordSnapshot(newBrowser, Guid.NewGuid(), Snapshot(), T0.AddMilliseconds(2_000), loginName: "alice");

        Assert.True(registry.TryGetLoginPresence("alice", out var presence));
        Assert.True(presence!.IsOnline);
        Assert.Equal(2, presence.ClientCount);
        // The counts fold in the stale-but-retained client too (its session never closed), so
        // ActiveSessionCount is non-zero independently of IsOnline — see the caveat on LoginPresence.
        Assert.Equal(2, presence.ActiveSessionCount);
        Assert.Equal(T0.AddMilliseconds(2_000), presence.LastSeenUtc);
    }

    [Fact]
    public void TryGetLoginPresence_matches_any_casing_and_misses_unknown_logins()
    {
        var registry = NewRegistry();
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: "Alice");

        Assert.True(registry.TryGetLoginPresence("aLiCe", out var presence));
        Assert.Equal("Alice", presence!.LoginName);

        Assert.False(registry.TryGetLoginPresence("bob", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void TryGetLoginPresence_rejects_null_and_never_matches_an_empty_name()
    {
        var registry = NewRegistry();
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: null);

        Assert.Throws<ArgumentNullException>(() => registry.TryGetLoginPresence(null!, out _));
        Assert.False(registry.TryGetLoginPresence("", out _));
    }

    [Fact]
    public void A_logged_out_client_still_probing_keeps_its_last_login_online_by_design()
    {
        // The documented sticky-login caveat (#26): a reconnect with no identity retains the last
        // known name, so presence reports the old login as online while the anonymous tab probes on.
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(), T0, loginName: "alice");
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(), T0.AddMilliseconds(500), loginName: null);

        Assert.True(registry.TryGetLoginPresence("alice", out var presence));
        Assert.True(presence!.IsOnline);
    }

    [Fact]
    public void An_expired_login_disappears_from_presence()
    {
        var registry = NewRegistry();
        registry.RecordSnapshot(Guid.NewGuid(), Guid.NewGuid(), Snapshot(), T0, loginName: "alice");

        registry.Sweep(T0.AddMilliseconds(5_001)); // past retention — entry removed

        Assert.Empty(registry.GetLoginPresences());
        Assert.False(registry.TryGetLoginPresence("alice", out _));
    }
}
