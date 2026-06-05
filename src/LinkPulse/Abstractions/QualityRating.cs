namespace LinkPulse.Abstractions;

/// <summary>
/// Overall connection-quality rating. The four measured tiers are ordered so that the
/// numerically smallest is the worst, which lets the overall rating be computed as the
/// minimum of the independent RTT, jitter, and loss sub-ratings (weakest-link wins).
/// <see cref="Disconnected"/> is a distinct state entered on probe timeout or socket
/// close, not a product of the sub-rating minimum.
/// </summary>
public enum QualityRating
{
    /// <summary>No live probe connection (ping timed out or the socket closed).</summary>
    Disconnected = 0,

    /// <summary>Poor connection quality.</summary>
    Poor = 1,

    /// <summary>Fair connection quality.</summary>
    Fair = 2,

    /// <summary>Good connection quality.</summary>
    Good = 3,

    /// <summary>Excellent connection quality.</summary>
    Excellent = 4,
}
