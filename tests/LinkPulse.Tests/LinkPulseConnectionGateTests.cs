using System.Net;
using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the per-IP concurrent-session cap (&#167;11): the gate must admit up to the configured limit
/// per address, refuse beyond it, free a slot on release, and track each address independently.
/// </summary>
public sealed class LinkPulseConnectionGateTests
{
    private static LinkPulseConnectionGate NewGate(int maxPerIp) =>
        new(new LinkPulseOptions { MaxConcurrentSessionsPerIp = maxPerIp });

    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.7");

    [Fact]
    public void Acquire_succeeds_up_to_the_cap_then_refuses()
    {
        var gate = NewGate(maxPerIp: 2);

        Assert.True(gate.TryAcquire(Ip));
        Assert.True(gate.TryAcquire(Ip));
        Assert.False(gate.TryAcquire(Ip));
    }

    [Fact]
    public void Releasing_a_slot_lets_a_new_acquire_succeed()
    {
        var gate = NewGate(maxPerIp: 1);
        Assert.True(gate.TryAcquire(Ip));
        Assert.False(gate.TryAcquire(Ip));

        gate.Release(Ip);

        Assert.True(gate.TryAcquire(Ip));
    }

    [Fact]
    public void Each_address_is_capped_independently()
    {
        var gate = NewGate(maxPerIp: 1);
        var other = IPAddress.Parse("203.0.113.8");

        Assert.True(gate.TryAcquire(Ip));
        Assert.True(gate.TryAcquire(other)); // a different IP is unaffected by Ip being at cap
        Assert.False(gate.TryAcquire(Ip));
    }

    [Fact]
    public void Fully_releasing_then_reacquiring_works_repeatedly()
    {
        var gate = NewGate(maxPerIp: 1);

        // Exercises the bucket being pruned at zero and recreated on the next acquire.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(gate.TryAcquire(Ip));
            gate.Release(Ip);
        }

        Assert.True(gate.TryAcquire(Ip));
    }

    [Fact]
    public void A_cap_below_one_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewGate(maxPerIp: 0));
    }
}
