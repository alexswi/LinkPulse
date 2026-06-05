namespace LinkPulse.Abstractions;

/// <summary>
/// Controls how the <c>&lt;LinkPulse /&gt;</c> client component renders. All modes measure
/// and report identically; only the rendered output differs.
/// </summary>
public enum DisplayMode
{
    /// <summary>Compact badge: coloured dot, one-word label, and current RTT. Expandable.</summary>
    Badge,

    /// <summary>Always-expanded panel showing the full metric breakdown and sparkline.</summary>
    Panel,

    /// <summary>Measures and reports silently while rendering nothing.</summary>
    Hidden,
}
