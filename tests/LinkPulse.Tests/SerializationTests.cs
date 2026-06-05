using System.Text.Json;
using LinkPulse.Abstractions;

namespace LinkPulse.Tests;

/// <summary>
/// Verifies the wire protocol DTOs serialize to the exact shape defined in the v1 spec
/// (&#167;3.5) and round-trip losslessly through the source-generated context.
/// </summary>
public sealed class SerializationTests
{
    private static readonly LinkPulseJsonContext Context = LinkPulseJsonContext.Default;

    [Fact]
    public void PingFrame_serializes_to_spec_wire_shape()
    {
        // Frames go on the wire as ProbeFrame so the "type" discriminator is emitted.
        ProbeFrame ping = new PingFrame { Seq = 1287, Payload = "stamp-42" };

        var json = JsonSerializer.Serialize(ping, Context.ProbeFrame);

        Assert.Contains("\"type\":\"ping\"", json);
        Assert.Contains("\"seq\":1287", json);
        Assert.Contains("\"payload\":\"stamp-42\"", json);
    }

    [Fact]
    public void SnapshotFrame_serializes_to_spec_wire_shape()
    {
        ProbeFrame frame = new SnapshotFrame
        {
            ClientId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Phase = ClientPhase.Wasm,
            RttMin = 12.5,
            RttAvg = 18.0,
            RttMax = 40.25,
            Jitter = 3.5,
            LossPct = 1.0,
            SampleCount = 30,
        };

        var json = JsonSerializer.Serialize(frame, Context.ProbeFrame);

        Assert.Contains("\"type\":\"snapshot\"", json);
        Assert.Contains("\"clientId\":", json);
        Assert.Contains("\"sessionId\":", json);
        Assert.Contains("\"phase\":\"Wasm\"", json);
        Assert.Contains("\"rttAvg\":18", json);
        Assert.Contains("\"lossPct\":1", json);
        Assert.Contains("\"sampleCount\":30", json);
    }

    [Fact]
    public void PingFrame_round_trips_as_concrete_type()
    {
        var original = new PingFrame { Seq = 99, Payload = "abc" };

        var json = JsonSerializer.Serialize(original, Context.PingFrame);
        var back = JsonSerializer.Deserialize(json, Context.PingFrame);

        Assert.Equal(original, back);
    }

    [Fact]
    public void SnapshotFrame_round_trips_as_concrete_type()
    {
        var original = new SnapshotFrame
        {
            ClientId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Phase = ClientPhase.Server,
            RttMin = 1.1,
            RttAvg = 2.2,
            RttMax = 3.3,
            Jitter = 0.4,
            LossPct = 0,
            SampleCount = 17,
        };

        var json = JsonSerializer.Serialize(original, Context.SnapshotFrame);
        var back = JsonSerializer.Deserialize(json, Context.SnapshotFrame);

        Assert.Equal(original, back);
    }

    [Theory]
    [InlineData(typeof(PingFrame))]
    [InlineData(typeof(SnapshotFrame))]
    public void Frames_round_trip_polymorphically_via_base_type(Type expected)
    {
        ProbeFrame original = expected == typeof(PingFrame)
            ? new PingFrame { Seq = 7, Payload = "p" }
            : new SnapshotFrame { ClientId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Phase = ClientPhase.Wasm, SampleCount = 5 };

        // Serialize/deserialize through the base type: the "type" discriminator must
        // round-trip the concrete runtime type.
        var json = JsonSerializer.Serialize(original, Context.ProbeFrame);
        var back = JsonSerializer.Deserialize(json, Context.ProbeFrame);

        Assert.IsType(expected, back);
        Assert.Equal(original, back);
    }

    [Fact]
    public void SnapshotFrame_and_MetricSnapshot_convert_both_ways()
    {
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var snapshot = new MetricSnapshot
        {
            Phase = ClientPhase.Server,
            RttMin = 5,
            RttAvg = 10,
            RttMax = 25,
            Jitter = 2,
            LossPct = 3,
            SampleCount = 28,
        };

        var frame = SnapshotFrame.FromSnapshot(clientId, sessionId, snapshot);

        Assert.Equal(clientId, frame.ClientId);
        Assert.Equal(sessionId, frame.SessionId);
        Assert.Equal(snapshot, frame.ToSnapshot());
    }

    [Fact]
    public void MetricSnapshot_round_trips()
    {
        var original = new MetricSnapshot
        {
            Phase = ClientPhase.Wasm,
            RttMin = 1,
            RttAvg = 2,
            RttMax = 3,
            Jitter = 0.5,
            LossPct = 0,
            SampleCount = 30,
        };

        var json = JsonSerializer.Serialize(original, Context.MetricSnapshot);
        var back = JsonSerializer.Deserialize(json, Context.MetricSnapshot);

        Assert.Equal(original, back);
    }
}
