using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the in-memory registry (&#167;9) and its stale/retention lifecycle (&#167;5.3). The registry
/// takes every timestamp from the caller, so the whole lifecycle is exercised deterministically on a
/// compressed clock with no real waiting.
/// </summary>
public sealed class LinkPulseRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Compressed thresholds: stale after 1 s of silence, removed 5 s after that.
    private static LinkPulseRegistry NewRegistry() =>
        new(new LinkPulseOptions { StaleThresholdMs = 1_000, StaleRetentionMs = 5_000 });

    private static MetricSnapshot Snapshot(double rttAvg = 20, ClientPhase phase = ClientPhase.Server) => new()
    {
        Phase = phase,
        RttMin = rttAvg,
        RttAvg = rttAvg,
        RttMax = rttAvg,
        Jitter = 1,
        LossPct = 0,
        SampleCount = 30,
    };

    [Fact]
    public void Recording_a_snapshot_creates_a_live_entry_with_its_metrics_and_session()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        registry.RecordSnapshot(clientId, sessionId, Snapshot(rttAvg: 42, phase: ClientPhase.Wasm), T0);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetConnection(clientId, out var view));
        Assert.NotNull(view);
        Assert.Equal(clientId, view!.ClientId);
        Assert.Equal(sessionId, Assert.Single(view.ActiveSessions));
        Assert.Equal(42, view.LatestSnapshot!.RttAvg);
        Assert.Equal(ClientPhase.Wasm, view.Phase);
        Assert.Equal(T0, view.FirstSeenUtc);
        Assert.Equal(T0, view.LastSeenUtc);
        Assert.False(view.IsStale);
        Assert.False(Assert.Single(view.History).IsOutage);
    }

    [Fact]
    public void Two_sessions_under_one_client_share_a_single_entry()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        registry.RecordSnapshot(clientId, first, Snapshot(), T0);
        registry.RecordSnapshot(clientId, second, Snapshot(), T0);

        Assert.Equal(1, registry.Count);
        registry.TryGetConnection(clientId, out var view);
        Assert.Equal(
            new[] { first, second }.OrderBy(g => g),
            view!.ActiveSessions.OrderBy(g => g));
    }

    [Fact]
    public void Removing_a_session_keeps_the_entry_but_drops_it_from_the_active_set()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        registry.RecordSnapshot(clientId, sessionId, Snapshot(), T0);

        registry.RemoveSession(clientId, sessionId);

        Assert.Equal(1, registry.Count);
        registry.TryGetConnection(clientId, out var view);
        Assert.Empty(view!.ActiveSessions);
    }

    [Fact]
    public void An_entry_flips_to_stale_only_after_the_threshold_is_exceeded()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(), T0);

        // Exactly at the threshold is still considered live (the boundary is strict).
        registry.Sweep(T0.AddMilliseconds(1_000));
        registry.TryGetConnection(clientId, out var stillLive);
        Assert.False(stillLive!.IsStale);

        // One tick past the threshold flips it.
        registry.Sweep(T0.AddMilliseconds(1_001));
        registry.TryGetConnection(clientId, out var nowStale);
        Assert.True(nowStale!.IsStale);
    }

    [Fact]
    public void A_stale_entry_is_removed_only_after_the_retention_window_is_exceeded()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(), T0);

        // Exactly at last-seen + retention is still retained.
        registry.Sweep(T0.AddMilliseconds(5_000));
        Assert.Equal(1, registry.Count);

        // One tick past retention removes it entirely.
        registry.Sweep(T0.AddMilliseconds(5_001));
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGetConnection(clientId, out _));
    }

    [Fact]
    public void Reconnecting_a_stale_entry_clears_stale_and_records_an_outage_marker()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(), T0);

        registry.Sweep(T0.AddMilliseconds(2_000)); // goes stale
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(), T0.AddMilliseconds(3_000)); // reconnect

        Assert.Equal(1, registry.Count);
        registry.TryGetConnection(clientId, out var view);
        Assert.False(view!.IsStale);

        // History: original snapshot, then the outage marker for the gap, then the reconnect snapshot.
        Assert.Collection(
            view.History,
            point => Assert.False(point.IsOutage),
            point => Assert.True(point.IsOutage),
            point => Assert.False(point.IsOutage));
    }

    [Fact]
    public void The_rolling_history_is_capped_at_sixty_points()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        for (var i = 0; i < 70; i++)
        {
            registry.RecordSnapshot(clientId, sessionId, Snapshot(), T0.AddSeconds(i));
        }

        registry.TryGetConnection(clientId, out var view);
        var historyCount = view!.History.Count;
        Assert.Equal(60, historyCount);
    }

    [Fact]
    public void New_clients_beyond_the_cap_are_dropped_but_tracked_clients_still_update()
    {
        var registry = new LinkPulseRegistry(new LinkPulseOptions { MaxTrackedClients = 2 });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        registry.RecordSnapshot(first, Guid.NewGuid(), Snapshot(), T0);
        registry.RecordSnapshot(second, Guid.NewGuid(), Snapshot(), T0);
        registry.RecordSnapshot(third, Guid.NewGuid(), Snapshot(), T0); // at cap — dropped

        Assert.Equal(2, registry.Count);
        Assert.False(registry.TryGetConnection(third, out _));

        // An already-tracked client is never blocked by the cap.
        registry.RecordSnapshot(first, Guid.NewGuid(), Snapshot(rttAvg: 99), T0.AddSeconds(1));
        registry.TryGetConnection(first, out var view);
        Assert.Equal(99, view!.LatestSnapshot!.RttAvg);
    }

    [Theory]
    [InlineData(-1, 1_000, 1)]
    [InlineData(1_000, -1, 1)]
    [InlineData(1_000, 1_000, 0)]
    public void Out_of_range_options_are_rejected_at_construction(int staleMs, int retentionMs, int maxClients)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinkPulseRegistry(new LinkPulseOptions
        {
            StaleThresholdMs = staleMs,
            StaleRetentionMs = retentionMs,
            MaxTrackedClients = maxClients,
        }));
    }

    [Fact]
    public void Changed_fires_on_record_remove_and_meaningful_sweeps()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var fired = 0;
        registry.Changed += (_, _) => fired++;

        registry.RecordSnapshot(clientId, sessionId, Snapshot(), T0);
        Assert.Equal(1, fired);

        registry.Sweep(T0); // nothing changed — no event
        Assert.Equal(1, fired);

        registry.Sweep(T0.AddMilliseconds(1_001)); // flips to stale — one event
        Assert.Equal(2, fired);

        registry.RemoveSession(clientId, sessionId);
        Assert.Equal(3, fired);
    }
}
