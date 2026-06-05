namespace LinkPulse.Abstractions;

/// <summary>
/// Identifies which Blazor Auto render phase a client is measuring from. The same
/// measurement code path runs in both phases, so snapshots remain comparable across
/// the Server&#8594;WebAssembly transition.
/// </summary>
public enum ClientPhase
{
    /// <summary>Interactive Server (SignalR circuit) phase, including prerender.</summary>
    Server,

    /// <summary>WebAssembly phase, after the client-side runtime has taken over.</summary>
    Wasm,
}
