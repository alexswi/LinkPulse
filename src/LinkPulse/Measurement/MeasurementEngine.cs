using LinkPulse.Abstractions;

namespace LinkPulse.Measurement;

/// <summary>
/// The pure-C# measurement core: turns a stream of ping/echo events into RTT, jitter, and loss
/// metrics over a rolling window (v1 spec, &#167;3). It has no Blazor, JS, or networking dependency
/// &#8212; all timing enters as explicit client-clock milliseconds (the monotonic
/// <c>performance.now()</c> values the JS layer supplies in #4), so its behaviour is fully
/// deterministic and unit-testable in isolation.
/// </summary>
/// <remarks>
/// The engine is single-threaded by design: a Blazor client drives it from one synchronization
/// context, so it takes no locks. RTT is computed entirely on the client clock (&#167;3.1); the
/// engine never reads a clock itself, the caller passes every timestamp in.
/// </remarks>
public sealed class MeasurementEngine
{
    private readonly int _windowSize;
    private readonly double _pingTimeoutMs;
    private readonly double _jitterSmoothingFactor;

    // seq -> client send-stamp (ms) of pings awaiting an echo.
    private readonly Dictionary<long, double> _outstanding = [];

    // Resolved samples, oldest -> newest, capped at _windowSize (the raw ring buffer).
    private readonly Queue<RttSample> _window = [];

    /// <summary>
    /// Creates an engine from the three measurement parameters it actually depends on, failing fast
    /// on values that would make the metrics meaningless. This is the honest dependency surface; the
    /// <see cref="MeasurementEngine(LinkPulseOptions)"/> overload is the convenience path for callers
    /// that already hold a full options record.
    /// </summary>
    /// <param name="windowSize">Rolling sample count; must be at least 1.</param>
    /// <param name="pingTimeoutMs">Loss/sanity-guard threshold in milliseconds; must be non-negative.</param>
    /// <param name="jitterSmoothingFactor">
    /// The RFC 3550 gain denominator <c>G</c>; must be greater than zero (it divides the jitter
    /// recurrence, so zero would yield a non-finite jitter).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its required range.</exception>
    public MeasurementEngine(int windowSize, double pingTimeoutMs, double jitterSmoothingFactor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(pingTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(jitterSmoothingFactor);
        _windowSize = windowSize;
        _pingTimeoutMs = pingTimeoutMs;
        _jitterSmoothingFactor = jitterSmoothingFactor;
    }

    /// <summary>
    /// Creates an engine from a <see cref="LinkPulseOptions"/>, reading only
    /// <see cref="LinkPulseOptions.WindowSize"/>, <see cref="LinkPulseOptions.PingTimeoutMs"/>, and
    /// <see cref="LinkPulseOptions.JitterSmoothingFactor"/>; the other knobs are not used by the
    /// measurement core. Applies the same fail-fast bounds as the primary constructor.
    /// </summary>
    /// <param name="options">Measurement configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its required range.</exception>
    public MeasurementEngine(LinkPulseOptions options)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).WindowSize,
            options.PingTimeoutMs,
            options.JitterSmoothingFactor)
    {
    }

    /// <summary>
    /// The raw measurement window, oldest sample first. Retained for sparkline rendering (&#167;3.4);
    /// lost slots appear as <see cref="RttSample.IsLost"/> entries so outages stay visible. Each
    /// access returns an independent snapshot of the current window.
    /// </summary>
    public IReadOnlyList<RttSample> Window => [.. _window];

    /// <summary>The number of pings sent but not yet echoed, timed out, or discarded.</summary>
    public int OutstandingCount => _outstanding.Count;

    /// <summary>
    /// The most recent clean RTT (ms) still inside the window, or <see langword="null"/> when the
    /// window holds no clean sample.
    /// </summary>
    public double? LatestRttMs => _window.LastOrDefault(static s => !s.IsLost).RttMs;

    /// <summary>
    /// Records that a ping was sent. The send-stamp is held until the matching echo arrives or the
    /// ping times out via <see cref="ExpireOutstanding"/>.
    /// </summary>
    /// <param name="seq">
    /// The sequence number of the ping. Assumed monotonic (never reused while outstanding); calling
    /// this twice with the same <paramref name="seq"/> overwrites the earlier send-stamp, so the
    /// earlier ping is neither resolved nor counted as loss.
    /// </param>
    /// <param name="sentAtMs">The client-clock send time, in milliseconds.</param>
    public void RecordPing(long seq, double sentAtMs) => _outstanding[seq] = sentAtMs;

    /// <summary>
    /// Records a received echo, matched to its ping <em>by sequence number</em> so out-of-order
    /// arrival cannot corrupt RTT pairing (&#167;3.6). An echo whose seq is no longer outstanding is
    /// discarded: it is not counted as received and never feeds RTT or jitter. This covers three
    /// cases &#8212; a late echo for an already-timed-out ping (whose loss was attributed when it
    /// timed out), a duplicate of an already-resolved ping, and an echo for an unknown seq (for
    /// which nothing was ever recorded). A matched sample that is negative or exceeds the ping
    /// timeout is discarded as loss by the sanity guard (&#167;3.2).
    /// </summary>
    /// <param name="seq">The sequence number echoed back by the server.</param>
    /// <param name="receivedAtMs">The client-clock receive time, in milliseconds.</param>
    public void RecordEcho(long seq, double receivedAtMs)
    {
        if (!_outstanding.Remove(seq, out var sentAtMs))
        {
            return; // late / duplicate / unknown echo — discarded
        }

        var rtt = receivedAtMs - sentAtMs;
        var clean = rtt >= 0 && rtt <= _pingTimeoutMs;
        Append(new RttSample(seq, clean ? rtt : null));
    }

    /// <summary>
    /// Finalises every outstanding ping whose age exceeds the ping timeout as lost (&#167;3.6),
    /// removing it from the outstanding set and appending it to the window. Pings exactly at the
    /// timeout are still considered in-flight (consistent with the receive-side guard's
    /// <c>&gt; PingTimeoutMs</c> bound). Call this on each cadence tick before taking a snapshot.
    /// </summary>
    /// <param name="nowMs">The current client-clock time, in milliseconds.</param>
    public void ExpireOutstanding(double nowMs)
    {
        if (_outstanding.Count == 0)
        {
            return;
        }

        List<long>? expired = null;
        foreach (var (seq, sentAt) in _outstanding)
        {
            if (nowMs - sentAt > _pingTimeoutMs)
            {
                (expired ??= []).Add(seq);
            }
        }

        if (expired is null)
        {
            return;
        }

        // seq is monotonic, so ascending seq == send order — deterministic window placement.
        expired.Sort();
        foreach (var seq in expired)
        {
            _outstanding.Remove(seq);
            Append(new RttSample(seq, null));
        }
    }

    /// <summary>
    /// Computes an aggregate <see cref="MetricSnapshot"/> over the current window: RTT min/avg/max
    /// and the RFC 3550 jitter estimate over the clean samples, packet loss as a percentage of all
    /// resolved samples, and the window size. Does not mutate engine state, so it may be called as
    /// often as needed.
    /// </summary>
    /// <param name="phase">The render phase the measurements were taken in.</param>
    /// <returns>The aggregate metrics. All fields are zero when the window is empty.</returns>
    public MetricSnapshot CreateSnapshot(ClientPhase phase)
    {
        double sum = 0, min = double.MaxValue, max = double.MinValue, jitter = 0;
        int lost = 0, clean = 0;
        double? prev = null;

        foreach (var sample in _window)
        {
            if (sample.RttMs is not double rtt)
            {
                lost++;
                continue;
            }

            clean++;
            sum += rtt;
            if (rtt < min)
            {
                min = rtt;
            }

            if (rtt > max)
            {
                max = rtt;
            }

            // RFC 3550 interarrival estimate over consecutive clean samples: J += (|D| − J) / G.
            if (prev is double previous)
            {
                jitter += (Math.Abs(rtt - previous) - jitter) / _jitterSmoothingFactor;
            }

            prev = rtt;
        }

        var total = lost + clean;
        return new MetricSnapshot
        {
            Phase = phase,
            RttMin = clean > 0 ? min : 0,
            RttAvg = clean > 0 ? sum / clean : 0,
            RttMax = clean > 0 ? max : 0,
            Jitter = jitter,
            LossPct = total > 0 ? 100.0 * lost / total : 0,
            SampleCount = total,
        };
    }

    private void Append(RttSample sample)
    {
        _window.Enqueue(sample);
        while (_window.Count > _windowSize)
        {
            _window.Dequeue();
        }
    }
}
