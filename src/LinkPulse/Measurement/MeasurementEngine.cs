using LinkPulse.Abstractions;

namespace LinkPulse;

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
    /// Creates an engine configured from the supplied options. Reads
    /// <see cref="LinkPulseOptions.WindowSize"/>, <see cref="LinkPulseOptions.PingTimeoutMs"/>,
    /// and <see cref="LinkPulseOptions.JitterSmoothingFactor"/>; the other knobs are not used by
    /// the measurement core.
    /// </summary>
    /// <param name="options">Measurement configuration. Assumed already bounds-checked (#2/#4/#5).</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public MeasurementEngine(LinkPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _windowSize = options.WindowSize;
        _pingTimeoutMs = options.PingTimeoutMs;
        _jitterSmoothingFactor = options.JitterSmoothingFactor;
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
    /// <param name="seq">The monotonic sequence number of the ping.</param>
    /// <param name="sentAtMs">The client-clock send time, in milliseconds.</param>
    public void RecordPing(long seq, double sentAtMs) => _outstanding[seq] = sentAtMs;

    /// <summary>
    /// Records a received echo, matched to its ping <em>by sequence number</em> so out-of-order
    /// arrival cannot corrupt RTT pairing (&#167;3.6). An echo whose seq is no longer outstanding
    /// &#8212; a late echo for an already-timed-out ping, a duplicate, or an unknown seq &#8212; is
    /// discarded: it is not counted as received and never feeds RTT or jitter, and any loss was
    /// already attributed when the ping timed out. A matched sample that is negative or exceeds the
    /// ping timeout is discarded as loss by the sanity guard (&#167;3.2).
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
