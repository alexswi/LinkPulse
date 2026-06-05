using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the untrusted-input gate for snapshot frames (&#167;11). Every field is attacker-controlled,
/// so the validator must accept only fully well-formed, in-range frames and reject everything else
/// outright rather than clamping a malformed frame into plausible-looking data.
/// </summary>
public sealed class SnapshotValidatorTests
{
    private static SnapshotFrame ValidFrame() => new()
    {
        ClientId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        Phase = ClientPhase.Wasm,
        RttMin = 10,
        RttAvg = 20,
        RttMax = 30,
        Jitter = 2,
        LossPct = 1,
        SampleCount = 30,
    };

    [Fact]
    public void A_well_formed_frame_validates_and_yields_its_identity_and_metrics()
    {
        var frame = ValidFrame();

        var ok = SnapshotValidator.TryValidate(frame, out var clientId, out var sessionId, out var snapshot);

        Assert.True(ok);
        Assert.Equal(frame.ClientId, clientId);
        Assert.Equal(frame.SessionId, sessionId);
        Assert.Equal(ClientPhase.Wasm, snapshot.Phase);
        Assert.Equal(10, snapshot.RttMin);
        Assert.Equal(20, snapshot.RttAvg);
        Assert.Equal(30, snapshot.RttMax);
        Assert.Equal(2, snapshot.Jitter);
        Assert.Equal(1, snapshot.LossPct);
        Assert.Equal(30, snapshot.SampleCount);
    }

    [Fact]
    public void Equal_rtt_bounds_and_zero_loss_are_accepted()
    {
        // The min == avg == max, zero-loss case is the common healthy snapshot; it must pass.
        var frame = ValidFrame() with { RttMin = 12, RttAvg = 12, RttMax = 12, Jitter = 0, LossPct = 0 };

        Assert.True(SnapshotValidator.TryValidate(frame, out _, out _, out _));
    }

    [Fact]
    public void A_null_frame_is_rejected()
    {
        Assert.False(SnapshotValidator.TryValidate(null, out _, out _, out _));
    }

    [Theory]
    [MemberData(nameof(MalformedFrames))]
    public void Malformed_frames_are_rejected(SnapshotFrame frame)
    {
        var ok = SnapshotValidator.TryValidate(frame, out var clientId, out var sessionId, out var snapshot);

        Assert.False(ok);
        Assert.Equal(Guid.Empty, clientId);
        Assert.Equal(Guid.Empty, sessionId);
        Assert.Null(snapshot);
    }

    public static TheoryData<SnapshotFrame> MalformedFrames() =>
    [
        ValidFrame() with { ClientId = Guid.Empty },
        ValidFrame() with { SessionId = Guid.Empty },
        ValidFrame() with { Phase = (ClientPhase)99 },
        ValidFrame() with { RttAvg = double.NaN },
        ValidFrame() with { RttMax = double.PositiveInfinity },
        ValidFrame() with { RttMin = -1 },
        ValidFrame() with { RttMax = SnapshotValidator.MaxRttMs + 1 },
        ValidFrame() with { RttMin = 25, RttAvg = 20, RttMax = 30 }, // min > avg
        ValidFrame() with { RttMin = 10, RttAvg = 40, RttMax = 30 }, // avg > max
        ValidFrame() with { Jitter = -0.5 },
        ValidFrame() with { Jitter = double.NaN },
        ValidFrame() with { Jitter = SnapshotValidator.MaxJitterMs + 1 },
        ValidFrame() with { LossPct = -0.1 },
        ValidFrame() with { LossPct = 100.1 },
        ValidFrame() with { LossPct = double.NaN },
        ValidFrame() with { SampleCount = -1 },
        ValidFrame() with { SampleCount = SnapshotValidator.MaxSampleCount + 1 },
    ];
}
