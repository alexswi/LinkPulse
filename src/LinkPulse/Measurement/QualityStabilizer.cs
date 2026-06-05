using LinkPulse.Abstractions;

namespace LinkPulse;

/// <summary>
/// Applies hysteresis to a stream of measured quality ratings so the displayed rating does not
/// flicker on a borderline connection (v1 spec, &#167;6): a new level must hold for a configured
/// number of consecutive snapshots before <see cref="Current"/> changes.
/// </summary>
/// <remarks>
/// This filter handles only measured ratings. <see cref="QualityRating.Disconnected"/> is a
/// connection state driven by socket close / ping timeout, wired in the client component (#4);
/// on disconnect that component shows <see cref="QualityRating.Disconnected"/> directly and calls
/// <see cref="Reset"/>, so the first measured rating after reconnect is adopted at once rather
/// than waiting out the hysteresis streak.
/// </remarks>
public sealed class QualityStabilizer
{
    private readonly int _hysteresisSamples;

    private bool _initialized;
    private QualityRating _current;
    private QualityRating _candidate;
    private int _candidateStreak;

    /// <summary>
    /// Creates a stabilizer requiring a candidate rating to repeat for <paramref name="hysteresisSamples"/>
    /// consecutive pushes before it becomes <see cref="Current"/>.
    /// </summary>
    /// <param name="hysteresisSamples">
    /// Consecutive snapshots a new rating must hold before it is adopted (the v1 default is 3,
    /// from <see cref="LinkPulseOptions.HysteresisSamples"/>). A value of 1 or less makes every
    /// push take effect immediately.
    /// </param>
    public QualityStabilizer(int hysteresisSamples) => _hysteresisSamples = hysteresisSamples;

    /// <summary>
    /// The current stable rating that should be displayed. Before the first <see cref="Push"/> this
    /// is <see cref="QualityRating.Disconnected"/> (the default, no measurement yet).
    /// </summary>
    public QualityRating Current => _current;

    /// <summary>
    /// Feeds the next measured rating in and returns the stable rating. The first push after
    /// construction or <see cref="Reset"/> is adopted immediately; afterwards a rating differing
    /// from <see cref="Current"/> must repeat for the configured number of consecutive pushes before
    /// it is adopted, and any interruption restarts that streak.
    /// </summary>
    /// <param name="candidate">The freshly measured rating for this snapshot.</param>
    /// <returns>The stable rating after applying hysteresis.</returns>
    public QualityRating Push(QualityRating candidate)
    {
        if (!_initialized)
        {
            _initialized = true;
            _current = candidate;
            _candidate = candidate;
            _candidateStreak = 0;
            return _current;
        }

        if (candidate == _current)
        {
            // Reading matches what we display; cancel any pending change.
            _candidate = _current;
            _candidateStreak = 0;
            return _current;
        }

        if (candidate == _candidate)
        {
            _candidateStreak++;
        }
        else
        {
            _candidate = candidate;
            _candidateStreak = 1;
        }

        if (_candidateStreak >= _hysteresisSamples)
        {
            _current = candidate;
            _candidateStreak = 0;
        }

        return _current;
    }

    /// <summary>
    /// Clears all state so the next <see cref="Push"/> is adopted immediately. Called by the client
    /// component when the probe connection drops, so a reconnect re-establishes the rating without
    /// waiting out the hysteresis streak.
    /// </summary>
    public void Reset()
    {
        _initialized = false;
        _current = default;
        _candidate = default;
        _candidateStreak = 0;
    }
}
