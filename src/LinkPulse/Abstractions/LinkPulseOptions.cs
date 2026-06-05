namespace LinkPulse.Abstractions;

/// <summary>
/// Tunable knobs for LinkPulse measurement, reporting, and registry lifecycle. In v1 these
/// are surfaced as <c>[Parameter]</c> properties on the <c>&lt;LinkPulse /&gt;</c> component;
/// defaults match the v1 spec (&#167;7 and &#167;13).
/// </summary>
/// <remarks>
/// Values are not bounds-checked at construction. The documented limits (e.g.
/// <see cref="MinPingIntervalMs"/>, positive window/sample counts) are enforced server-side
/// when the probe endpoint and registry land (issues #4/#5), not here.
/// </remarks>
public sealed record LinkPulseOptions
{
    /// <summary>The hard server-side lower bound on ping cadence, in milliseconds (&#167;7/&#167;11).</summary>
    public const int MinPingIntervalMs = 100;

    /// <summary>Visible-tab ping cadence, in milliseconds. Bounded below by <see cref="MinPingIntervalMs"/> server-side.</summary>
    public int PingIntervalMs { get; init; } = 1000;

    /// <summary>Hidden-tab ping cadence, in milliseconds (adaptive backoff via the Page Visibility API).</summary>
    public int HiddenTabPingIntervalMs { get; init; } = 5000;

    /// <summary>Rolling sample count over which all derived metrics are computed.</summary>
    public int WindowSize { get; init; } = 30;

    /// <summary>A ping with no echo within this many milliseconds is counted as lost.</summary>
    public int PingTimeoutMs { get; init; } = 5000;

    /// <summary>
    /// RFC 3550 jitter noise-reduction factor: the denominator <c>G</c> in
    /// <c>J += (|D| &#8722; J) / G</c>. The smoothing gain is therefore <c>1/G</c>; a larger
    /// value smooths more. The RFC's canonical value is 16.
    /// </summary>
    public double JitterSmoothingFactor { get; init; } = 16;

    /// <summary>Snapshot reporting cadence, in milliseconds.</summary>
    public int SnapshotIntervalMs { get; init; } = 5000;

    /// <summary>Consecutive snapshots a new rating must hold before the displayed rating changes (hysteresis).</summary>
    public int HysteresisSamples { get; init; } = 3;

    /// <summary>Duration of snapshot silence, in milliseconds, after which a registry entry flips to stale.</summary>
    public int StaleThresholdMs { get; init; } = 30_000;

    /// <summary>How long a stale registry entry is retained, in milliseconds, before removal (default 2 h).</summary>
    public int StaleRetentionMs { get; init; } = 7_200_000;

    /// <summary>
    /// Maximum number of concurrent probe sessions accepted from a single client IP (&#167;11 abuse
    /// bound). Upgrade requests beyond this are rejected so one host cannot exhaust server resources.
    /// </summary>
    public int MaxConcurrentSessionsPerIp { get; init; } = 20;

    /// <summary>How the client component renders.</summary>
    public DisplayMode Display { get; init; } = DisplayMode.Badge;

    /// <summary>Quality-classification tier boundaries.</summary>
    public QualityThresholds Thresholds { get; init; } = QualityThresholds.Default;
}

/// <summary>
/// Upper-bound tier boundaries for the three independent quality sub-ratings (&#167;6). A metric
/// earns a tier when its value is strictly below that tier's bound (<c>&lt;</c>); the overall
/// rating is the weakest of the three. Loss is the one exception: because its excellent bound
/// defaults to 0%, the excellent tier is inclusive there (loss must equal 0). Defaults match
/// the v1 spec; values are not validated here (monotonicity is assumed by the rating logic in
/// a later issue).
/// </summary>
public sealed record QualityThresholds
{
    /// <summary>The default thresholds from the v1 spec (&#167;6).</summary>
    public static QualityThresholds Default { get; } = new();

    /// <summary>Average RTT below this many milliseconds rates <see cref="QualityRating.Excellent"/>.</summary>
    public double RttExcellentMs { get; init; } = 50;

    /// <summary>Average RTT below this many milliseconds rates at least <see cref="QualityRating.Good"/>.</summary>
    public double RttGoodMs { get; init; } = 150;

    /// <summary>Average RTT below this many milliseconds rates at least <see cref="QualityRating.Fair"/>; at or above is <see cref="QualityRating.Poor"/>.</summary>
    public double RttFairMs { get; init; } = 300;

    /// <summary>Jitter below this many milliseconds rates <see cref="QualityRating.Excellent"/>.</summary>
    public double JitterExcellentMs { get; init; } = 10;

    /// <summary>Jitter below this many milliseconds rates at least <see cref="QualityRating.Good"/>.</summary>
    public double JitterGoodMs { get; init; } = 30;

    /// <summary>Jitter below this many milliseconds rates at least <see cref="QualityRating.Fair"/>; at or above is <see cref="QualityRating.Poor"/>.</summary>
    public double JitterFairMs { get; init; } = 60;

    /// <summary>Loss at or below this percentage rates <see cref="QualityRating.Excellent"/> (inclusive; 0% by default).</summary>
    public double LossExcellentPct { get; init; } = 0;

    /// <summary>Loss below this percentage rates at least <see cref="QualityRating.Good"/>.</summary>
    public double LossGoodPct { get; init; } = 1;

    /// <summary>Loss below this percentage rates at least <see cref="QualityRating.Fair"/>; at or above is <see cref="QualityRating.Poor"/>.</summary>
    public double LossFairPct { get; init; } = 5;
}
