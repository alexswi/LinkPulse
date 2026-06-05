using LinkPulse.Abstractions;
using LinkPulse.Measurement;

namespace LinkPulse.Tests;

/// <summary>
/// Deterministic tests for the measurement core (#3). Every timestamp is supplied explicitly
/// (no real clock), so loss attribution, late/out-of-order echoes, sanity guards, and the jitter
/// recurrence are exercised at exact, hand-verifiable values.
/// </summary>
public sealed class MeasurementEngineTests
{
    private static MeasurementEngine NewEngine(int windowSize = 30, int pingTimeoutMs = 5000) =>
        new(new LinkPulseOptions { WindowSize = windowSize, PingTimeoutMs = pingTimeoutMs });

    [Fact]
    public void A_ping_with_no_echo_times_out_as_loss()
    {
        var engine = NewEngine();
        engine.RecordPing(1, sentAtMs: 0);

        engine.ExpireOutstanding(nowMs: 6000); // 6000 − 0 > 5000

        Assert.Equal(0, engine.OutstandingCount);
        var snap = engine.CreateSnapshot(ClientPhase.Server);
        Assert.Equal(1, snap.SampleCount);
        Assert.Equal(100, snap.LossPct, 10);
        Assert.Equal(0, snap.RttAvg, 10);
        Assert.True(engine.Window[0].IsLost);
    }

    [Fact]
    public void A_ping_exactly_at_the_timeout_is_still_in_flight()
    {
        var engine = NewEngine(pingTimeoutMs: 5000);
        engine.RecordPing(1, sentAtMs: 0);

        engine.ExpireOutstanding(nowMs: 5000); // boundary: not yet lost

        Assert.Equal(1, engine.OutstandingCount);
        Assert.Empty(engine.Window);
    }

    [Fact]
    public void Loss_percentage_is_lost_over_total_resolved()
    {
        var engine = NewEngine();
        engine.RecordPing(1, 0);
        engine.RecordEcho(1, 20); // clean RTT 20
        engine.RecordPing(2, 100);
        engine.ExpireOutstanding(5200); // 5200 − 100 = 5100 > 5000 → lost

        var snap = engine.CreateSnapshot(ClientPhase.Wasm);

        Assert.Equal(2, snap.SampleCount);
        Assert.Equal(50, snap.LossPct, 10);
        Assert.Equal(20, snap.RttAvg, 10);
    }

    [Theory]
    [InlineData(100, 50)]   // negative RTT (received before sent — clock weirdness)
    [InlineData(0, 6000)]   // RTT 6000 > 5000 timeout
    public void A_sample_failing_the_sanity_guard_is_counted_as_loss(double sentAt, double receivedAt)
    {
        var engine = NewEngine(pingTimeoutMs: 5000);
        engine.RecordPing(1, sentAt);

        engine.RecordEcho(1, receivedAt); // matched, but rejected by the §3.2 guard

        var snap = engine.CreateSnapshot(ClientPhase.Server);
        Assert.Equal(1, snap.SampleCount);
        Assert.Equal(100, snap.LossPct, 10);
        Assert.True(engine.Window[0].IsLost);
        Assert.Null(engine.LatestRttMs);
    }

    [Fact]
    public void A_late_echo_for_a_timed_out_ping_is_discarded_and_stays_loss()
    {
        var engine = NewEngine();
        engine.RecordPing(1, 0);
        engine.RecordEcho(1, 10); // clean RTT 10
        engine.RecordPing(2, 1000);
        engine.ExpireOutstanding(6001); // seq 2 lost (6001 − 1000 > 5000)

        engine.RecordEcho(2, 6050); // late echo for seq 2 — must be discarded, not received

        var snap = engine.CreateSnapshot(ClientPhase.Server);
        Assert.Equal(2, snap.SampleCount); // still just clean(1) + lost(2)
        Assert.Equal(50, snap.LossPct, 10);
        Assert.Equal(10, snap.RttAvg, 10); // late echo did not feed RTT
        Assert.Equal(0, engine.OutstandingCount);
    }

    [Fact]
    public void Out_of_order_echoes_are_paired_by_sequence_number_not_arrival_order()
    {
        var engine = NewEngine();
        engine.RecordPing(4, sentAtMs: 0);
        engine.RecordPing(5, sentAtMs: 100);

        engine.RecordEcho(5, receivedAtMs: 130); // arrives first → RTT 30 for seq 5
        engine.RecordEcho(4, receivedAtMs: 200); // arrives second → RTT 200 for seq 4

        var window = engine.Window;
        // Pairing is by seq: had it paired by arrival order, seq 5's echo (130) would have matched
        // seq 4's send-stamp (0) and produced RTT 130.
        Assert.Equal(30, window.Single(s => s.Seq == 5).RttMs);
        Assert.Equal(200, window.Single(s => s.Seq == 4).RttMs);
    }

    [Fact]
    public void Jitter_follows_the_rfc3550_recurrence_over_clean_samples()
    {
        var engine = NewEngine(); // JitterSmoothingFactor G = 16 (default)
        foreach (var (seq, rtt) in new[] { (1L, 10.0), (2L, 20.0), (3L, 30.0), (4L, 30.0) })
        {
            engine.RecordPing(seq, 0);
            engine.RecordEcho(seq, rtt);
        }

        var snap = engine.CreateSnapshot(ClientPhase.Server);

        // J += (|D| − J)/16 over D = 10, 10, 0:
        //   0 → 0.625 → 1.2109375 → 1.13525390625  (2325/2048, exact in binary)
        Assert.Equal(1.13525390625, snap.Jitter, 12);
    }

    [Fact]
    public void Constant_rtt_yields_zero_jitter()
    {
        var engine = NewEngine();
        for (var seq = 1L; seq <= 5; seq++)
        {
            engine.RecordPing(seq, 0);
            engine.RecordEcho(seq, 50); // identical RTT every time → |D| = 0
        }

        Assert.Equal(0, engine.CreateSnapshot(ClientPhase.Server).Jitter, 12);
    }

    [Fact]
    public void Rtt_min_avg_max_are_computed_over_clean_samples()
    {
        var engine = NewEngine();
        foreach (var (seq, rtt) in new[] { (1L, 10.0), (2L, 30.0), (3L, 20.0) })
        {
            engine.RecordPing(seq, 0);
            engine.RecordEcho(seq, rtt);
        }

        var snap = engine.CreateSnapshot(ClientPhase.Server);

        Assert.Equal(10, snap.RttMin, 10);
        Assert.Equal(30, snap.RttMax, 10);
        Assert.Equal(20, snap.RttAvg, 10);
    }

    [Fact]
    public void The_window_retains_only_the_most_recent_samples()
    {
        var engine = NewEngine(windowSize: 3);
        for (var seq = 1L; seq <= 5; seq++)
        {
            engine.RecordPing(seq, 0);
            engine.RecordEcho(seq, seq); // RTT == seq
        }

        var window = engine.Window;
        Assert.Equal(3, window.Count);
        Assert.Equal([3L, 4L, 5L], window.Select(s => s.Seq));

        var snap = engine.CreateSnapshot(ClientPhase.Server);
        Assert.Equal(3, snap.SampleCount);
        Assert.Equal(3, snap.RttMin, 10);
        Assert.Equal(5, snap.RttMax, 10);
        Assert.Equal(4, snap.RttAvg, 10);
    }

    [Fact]
    public void Latest_rtt_reflects_the_last_clean_sample_ignoring_a_trailing_loss()
    {
        var engine = NewEngine();
        engine.RecordPing(1, 0);
        engine.RecordEcho(1, 10);
        engine.RecordPing(2, 0);
        engine.RecordEcho(2, 20);
        Assert.Equal(20, engine.LatestRttMs);

        engine.RecordPing(3, 1000);
        engine.ExpireOutstanding(6001); // trailing loss

        Assert.Equal(20, engine.LatestRttMs); // unchanged — loss carries no RTT
    }

    [Fact]
    public void An_empty_window_produces_an_all_zero_snapshot()
    {
        var engine = NewEngine();

        var snap = engine.CreateSnapshot(ClientPhase.Server);

        Assert.Equal(ClientPhase.Server, snap.Phase);
        Assert.Equal(0, snap.SampleCount);
        Assert.Equal(0, snap.RttMin, 10);
        Assert.Equal(0, snap.RttAvg, 10);
        Assert.Equal(0, snap.RttMax, 10);
        Assert.Equal(0, snap.Jitter, 10);
        Assert.Equal(0, snap.LossPct, 10);
        Assert.Null(engine.LatestRttMs);
        Assert.Empty(engine.Window);
    }

    [Fact]
    public void Jitter_treats_clean_samples_either_side_of_a_loss_as_consecutive()
    {
        // A loss between two clean samples does not reset the jitter recurrence: D is taken across
        // the gap (RFC 3550 is over received packets). This pins that deliberate choice — were the
        // loss to reset `prev`, there'd be no D pair and jitter would be 0 instead of 2.5.
        var engine = NewEngine();
        engine.RecordPing(1, 0);
        engine.RecordEcho(1, 10); // clean RTT 10
        engine.RecordPing(2, 1000);
        engine.ExpireOutstanding(6001); // seq 2 lost
        engine.RecordPing(3, 0);
        engine.RecordEcho(3, 50); // clean RTT 50

        // single D = |50 − 10| = 40 → 0 + (40 − 0)/16 = 2.5
        Assert.Equal(2.5, engine.CreateSnapshot(ClientPhase.Server).Jitter, 12);
    }

    [Fact]
    public void Several_pings_expiring_together_enter_the_window_in_send_order()
    {
        var engine = NewEngine();
        engine.RecordPing(3, 0); // recorded out of seq order
        engine.RecordPing(1, 0);
        engine.RecordPing(2, 0);

        engine.ExpireOutstanding(6000); // all three time out in one call

        Assert.Equal([1L, 2L, 3L], engine.Window.Select(s => s.Seq)); // ascending seq == send order
        Assert.All(engine.Window, s => Assert.True(s.IsLost));
    }

    [Fact]
    public void An_echo_for_a_never_sent_seq_is_ignored()
    {
        var engine = NewEngine();

        engine.RecordEcho(99, 10); // no ping was ever recorded for seq 99

        Assert.Empty(engine.Window);
        Assert.Equal(0, engine.OutstandingCount);
        Assert.Equal(0, engine.CreateSnapshot(ClientPhase.Server).SampleCount);
    }

    [Fact]
    public void A_duplicate_echo_for_an_already_resolved_seq_is_ignored()
    {
        var engine = NewEngine();
        engine.RecordPing(1, 0);
        engine.RecordEcho(1, 20); // clean RTT 20

        engine.RecordEcho(1, 25); // duplicate — must not create a second sample or perturb metrics

        var snap = engine.CreateSnapshot(ClientPhase.Server);
        Assert.Single(engine.Window);
        Assert.Equal(1, snap.SampleCount);
        Assert.Equal(20, snap.RttAvg, 10);
    }

    [Fact]
    public void A_loss_aging_out_of_the_window_no_longer_counts_toward_loss()
    {
        // Loss% is computed only over the retained window, so an old loss aging out must change it.
        var engine = NewEngine(windowSize: 2);
        engine.RecordPing(1, 1000);
        engine.ExpireOutstanding(6001); // lost(1) — oldest
        engine.RecordPing(2, 0);
        engine.RecordEcho(2, 10); // clean
        engine.RecordPing(3, 0);
        engine.RecordEcho(3, 20); // clean — evicts lost(1)

        var snap = engine.CreateSnapshot(ClientPhase.Server);
        Assert.Equal(2, snap.SampleCount);
        Assert.Equal(0, snap.LossPct, 10);
        Assert.DoesNotContain(engine.Window, s => s.IsLost);
    }

    [Fact]
    public void Latest_rtt_is_null_once_the_last_clean_sample_is_evicted()
    {
        var engine = NewEngine(windowSize: 2);
        engine.RecordPing(1, 0);
        engine.RecordEcho(1, 15); // the only clean sample
        engine.RecordPing(2, 1000);
        engine.RecordPing(3, 2000);

        engine.ExpireOutstanding(7002); // lost(2), lost(3) evict clean(1)

        Assert.Null(engine.LatestRttMs);
        Assert.DoesNotContain(engine.Window, s => !s.IsLost);
    }

    [Theory]
    [InlineData(0, 5000, 16)]    // windowSize < 1
    [InlineData(30, -1, 16)]     // negative timeout
    [InlineData(30, 5000, 0)]    // G == 0 (would divide the jitter recurrence)
    [InlineData(30, 5000, -4)]   // negative G
    public void The_constructor_rejects_out_of_range_parameters(int windowSize, double timeout, double g) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeasurementEngine(windowSize, timeout, g));

    [Fact]
    public void The_options_constructor_rejects_null() =>
        Assert.Throws<ArgumentNullException>(() => new MeasurementEngine(null!));
}
