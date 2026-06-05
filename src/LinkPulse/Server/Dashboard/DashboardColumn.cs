namespace LinkPulse.Server;

/// <summary>
/// The dashboard table columns the operator can sort by (v1 spec, &#167;10). The default view sorts
/// by <see cref="Quality"/> worst-first; any column can be chosen as the sort key, ascending or
/// descending.
/// </summary>
internal enum DashboardColumn
{
    /// <summary>Overall quality rating (the default sort, worst-first).</summary>
    Quality,

    /// <summary>The client identifier.</summary>
    ClientId,

    /// <summary>Number of currently-active probe sessions.</summary>
    Sessions,

    /// <summary>The most recent render phase (Server/WASM).</summary>
    Phase,

    /// <summary>Average round-trip time.</summary>
    Rtt,

    /// <summary>Jitter.</summary>
    Jitter,

    /// <summary>Packet loss percentage.</summary>
    Loss,

    /// <summary>How long ago the last snapshot arrived (last-seen age).</summary>
    LastSeen,

    /// <summary>How long the client has been tracked (uptime).</summary>
    Uptime,
}
