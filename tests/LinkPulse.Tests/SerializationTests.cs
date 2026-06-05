using System.Text.Json;
using LinkPulse.Abstractions;

namespace LinkPulse.Tests;

/// <summary>
/// Verifies the wire protocol DTOs serialize to the exact shape defined in the v1 spec
/// (&#167;3.5) and round-trip losslessly through the source-generated context. The exact-shape
/// assertions pin field naming, ordering, the <c>"type"</c> discriminator, string enums, and
/// default-field emission &#8212; the full contract downstream issues (#3/#4/#5) build on.
/// </summary>
public sealed class SerializationTests
{
    private static readonly LinkPulseJsonContext Context = LinkPulseJsonContext.Default;

    // Fixed identities so exact-shape assertions are deterministic.
    private static readonly Guid ClientId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void PingFrame_serializes_to_exact_spec_wire_shape()
    {
        // Frames go on the wire as ProbeFrame so the "type" discriminator is emitted first.
        ProbeFrame ping = new PingFrame { Seq = 1287, Payload = "stamp-42" };

        var json = JsonSerializer.Serialize(ping, Context.ProbeFrame);

        Assert.Equal("""{"type":"ping","seq":1287,"payload":"stamp-42"}""", json);
    }

    [Fact]
    public void SnapshotFrame_serializes_to_exact_spec_wire_shape()
    {
        ProbeFrame frame = new SnapshotFrame
        {
            ClientId = ClientId,
            SessionId = SessionId,
            Phase = ClientPhase.Wasm,
            RttMin = 12.5,
            RttAvg = 18.0,
            RttMax = 40.25,
            Jitter = 3.5,
            LossPct = 1.0,
            SampleCount = 30,
        };

        var json = JsonSerializer.Serialize(frame, Context.ProbeFrame);

        Assert.Equal(
            """{"type":"snapshot","clientId":"11111111-1111-1111-1111-111111111111","sessionId":"22222222-2222-2222-2222-222222222222","phase":"Wasm","rttMin":12.5,"rttAvg":18,"rttMax":40.25,"jitter":3.5,"lossPct":1,"sampleCount":30}""",
            json);
    }

    [Fact]
    public void Concrete_type_serialization_omits_the_discriminator()
    {
        // The discriminator belongs to the polymorphic base; the concrete contract must not
        // carry "type". This is the other half of the polymorphism contract.
        var json = JsonSerializer.Serialize(new PingFrame { Seq = 1, Payload = "x" }, Context.PingFrame);

        Assert.DoesNotContain("\"type\"", json);
    }

    [Fact]
    public void SnapshotFrame_emits_zero_valued_metric_fields()
    {
        // DefaultIgnoreCondition is left at Never, so default/zero fields stay on the wire.
        // Adding WhenWritingDefault later would be a silent wire-breaking change; pin it now.
        ProbeFrame frame = new SnapshotFrame { ClientId = ClientId, SessionId = SessionId, Phase = ClientPhase.Server };

        var json = JsonSerializer.Serialize(frame, Context.ProbeFrame);

        Assert.Contains("\"rttMin\":0", json);
        Assert.Contains("\"sampleCount\":0", json);
    }

    [Theory]
    [InlineData(ClientPhase.Server, "\"phase\":\"Server\"")]
    [InlineData(ClientPhase.Wasm, "\"phase\":\"Wasm\"")]
    public void Phase_serializes_as_its_spec_string(ClientPhase phase, string expected)
    {
        ProbeFrame frame = new SnapshotFrame { ClientId = ClientId, SessionId = SessionId, Phase = phase };

        var json = JsonSerializer.Serialize(frame, Context.ProbeFrame);

        Assert.Contains(expected, json);
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
            ClientId = ClientId,
            SessionId = SessionId,
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
            : new SnapshotFrame { ClientId = ClientId, SessionId = SessionId, Phase = ClientPhase.Wasm, SampleCount = 5 };

        // Serialize/deserialize through the base type: the "type" discriminator must
        // round-trip the concrete runtime type.
        var json = JsonSerializer.Serialize(original, Context.ProbeFrame);
        var back = JsonSerializer.Deserialize(json, Context.ProbeFrame);

        Assert.IsType(expected, back);
        Assert.Equal(original, back);
    }

    [Theory]
    [InlineData("""{"type":"bogus","seq":1}""", typeof(JsonException))]        // unknown discriminator value
    [InlineData("""{"seq":1,"payload":"x"}""", typeof(NotSupportedException))] // discriminator absent
    public void Deserializing_a_malformed_discriminator_throws(string json, Type exceptionType)
    {
        // The server receives untrusted frames through the base type; malformed discriminators
        // must fail loudly rather than silently producing a default frame. Note the two cases
        // throw DIFFERENT types — the #4/#5 receive path must handle both: an unknown "type"
        // value surfaces as JsonException, an entirely absent discriminator as NotSupportedException.
        Assert.Throws(exceptionType, () => JsonSerializer.Deserialize(json, Context.ProbeFrame));
    }

    [Fact]
    public void SnapshotFrame_and_MetricSnapshot_convert_both_ways()
    {
        // Every field gets a distinct, non-default value so that a field dropped by either
        // FromSnapshot or ToSnapshot fails this test (guards the hand-written field mapping).
        var snapshot = new MetricSnapshot
        {
            Phase = ClientPhase.Wasm,
            RttMin = 5,
            RttAvg = 10,
            RttMax = 25,
            Jitter = 2,
            LossPct = 3,
            SampleCount = 28,
        };

        var frame = SnapshotFrame.FromSnapshot(ClientId, SessionId, snapshot);

        Assert.Equal(ClientId, frame.ClientId);
        Assert.Equal(SessionId, frame.SessionId);
        Assert.Equal(snapshot, frame.ToSnapshot());
    }

    [Fact]
    public void MetricSnapshot_round_trips_including_full_double_precision()
    {
        var original = new MetricSnapshot
        {
            Phase = ClientPhase.Wasm,
            RttMin = 0.1 + 0.2, // 0.30000000000000004 — exercises round-trippable double formatting
            RttAvg = 2,
            RttMax = double.MaxValue,
            Jitter = 0.5,
            LossPct = 0,
            SampleCount = 30,
        };

        var json = JsonSerializer.Serialize(original, Context.MetricSnapshot);
        var back = JsonSerializer.Deserialize(json, Context.MetricSnapshot);

        Assert.Equal(original, back);
    }
}
