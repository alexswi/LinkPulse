using System.Text.Json.Serialization;

namespace LinkPulse.Abstractions;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the LinkPulse wire protocol and
/// DTOs. Using a generated context keeps serialization trimming- and AOT-safe for the
/// WebAssembly phase and avoids reflection-based metadata at runtime.
/// </summary>
/// <remarks>
/// camelCase property naming and string-valued enums produce the exact wire shape from the
/// v1 spec (&#167;3.5), e.g. <c>{"type":"ping","seq":...}</c> and <c>"phase":"Wasm"</c>.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProbeFrame))]
[JsonSerializable(typeof(PingFrame))]
[JsonSerializable(typeof(SnapshotFrame))]
[JsonSerializable(typeof(MetricSnapshot))]
public sealed partial class LinkPulseJsonContext : JsonSerializerContext;
