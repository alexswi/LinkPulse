namespace LinkPulse.Server;

/// <summary>Well-known defaults for the LinkPulse probe endpoint (v1 spec, &#167;12).</summary>
public static class LinkPulseProbeDefaults
{
    /// <summary>The default path the probe WebSocket is mapped at; configurable via <c>MapLinkPulseProbe</c>.</summary>
    public const string ProbePath = "/connection-probe";
}
