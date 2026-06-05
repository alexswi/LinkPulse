using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Integration tests for the live delta path the dashboard depends on (&#167;9 &#8594; &#167;10): a snapshot
/// landing in the <see cref="LinkPulseRegistry"/> raises <see cref="LinkPulseRegistry.Changed"/> and is
/// reflected by <see cref="DashboardProjection"/> over <see cref="LinkPulseRegistry.GetConnections"/> &#8212;
/// exactly the sequence the component runs on each throttled refresh. These wire the registry and the pure
/// projection together (without a renderer), so the path from "snapshot arrives" to "row appears" is proven
/// end to end, deterministically on a caller-supplied clock.
/// </summary>
public sealed class DashboardLivePathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly QualityThresholds Thresholds = QualityThresholds.Default;

    private static LinkPulseRegistry NewRegistry() =>
        new(new LinkPulseOptions { StaleThresholdMs = 1_000, StaleRetentionMs = 5_000 });

    private static MetricSnapshot Snapshot(double rttAvg, ClientPhase phase = ClientPhase.Server) => new()
    {
        Phase = phase,
        RttMin = rttAvg,
        RttAvg = rttAvg,
        RttMax = rttAvg,
        Jitter = 1,
        LossPct = 0,
        SampleCount = 30,
    };

    // The component's Rebuild() in one line: read the registry, project it worst-first, unfiltered.
    private static IReadOnlyList<ConnectionRow> Project(LinkPulseRegistry registry, DateTimeOffset nowUtc) =>
        DashboardProjection.Project(registry.GetConnections(), nowUtc, Thresholds, DashboardFilter.None);

    [Fact]
    public void A_recorded_snapshot_raises_changed_and_then_surfaces_as_a_dashboard_row()
    {
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        var notified = false;
        registry.Changed += (_, _) => notified = true;

        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(rttAvg: 400), T0);

        // The Changed event is what the dashboard subscribes to before it re-projects.
        Assert.True(notified);

        var row = Assert.Single(Project(registry, T0));
        Assert.Equal(clientId, row.ClientId);
        Assert.Equal(QualityRating.Poor, row.Rating); // 400 ms avg RTT is Poor
        Assert.Equal(400, row.RttAvg);
        Assert.False(row.IsStale);
    }

    [Fact]
    public void A_client_reconnecting_in_a_new_phase_stays_one_row_and_updates_in_place()
    {
        // The Blazor Auto transition: the same browser (one ClientId) drops its Server-phase probe and
        // reconnects with a fresh session in the WebAssembly phase. The dashboard must keep showing a
        // single row whose phase follows the client across the boundary — the measurement-continuity
        // guarantee, proven here at the registry+projection level (the live WASM badge can't run while the
        // RCL ships the server shared framework).
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();

        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(rttAvg: 30, phase: ClientPhase.Server), T0);
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(rttAvg: 45, phase: ClientPhase.Wasm), T0.AddSeconds(1));

        var row = Assert.Single(Project(registry, T0.AddSeconds(1)));
        Assert.Equal(clientId, row.ClientId);
        Assert.Equal(ClientPhase.Wasm, row.Phase); // phase followed the client across the transition
        Assert.Equal(45, row.RttAvg);

        // Both phases' samples are retained as one continuous history (no reset on the boundary).
        var historyCount = row.History.Count;
        Assert.Equal(2, historyCount);
    }

    [Fact]
    public void Removing_an_entry_raises_changed_and_drops_it_from_the_projection()
    {
        var registry = NewRegistry();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        registry.RecordSnapshot(keep, Guid.NewGuid(), Snapshot(rttAvg: 20), T0);
        registry.RecordSnapshot(drop, Guid.NewGuid(), Snapshot(rttAvg: 20), T0);

        var notified = false;
        registry.Changed += (_, _) => notified = true;

        var removed = registry.Remove(drop);

        Assert.True(removed);
        Assert.True(notified); // the dashboard's "remove stale entry" action triggers a refresh
        var row = Assert.Single(Project(registry, T0));
        Assert.Equal(keep, row.ClientId);
    }

    [Fact]
    public void A_silent_client_ages_to_stale_in_the_projection_after_a_sweep()
    {
        // The sweep service is the other writer the dashboard reacts to: a client that stops reporting is
        // flipped to stale (and rated Disconnected) by a sweep, which raises Changed and re-projects.
        var registry = NewRegistry();
        var clientId = Guid.NewGuid();
        registry.RecordSnapshot(clientId, Guid.NewGuid(), Snapshot(rttAvg: 20), T0);

        var liveRow = Assert.Single(Project(registry, T0));
        Assert.False(liveRow.IsStale);
        Assert.Equal(QualityRating.Excellent, liveRow.Rating);

        registry.Sweep(T0.AddMilliseconds(1_001)); // past the 1 s stale threshold

        var staleRow = Assert.Single(Project(registry, T0.AddMilliseconds(1_001)));
        Assert.True(staleRow.IsStale);
        Assert.Equal(QualityRating.Disconnected, staleRow.Rating);
    }
}
