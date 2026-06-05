using System.Globalization;
using LinkPulse.Measurement;

namespace LinkPulse.Rendering;

/// <summary>
/// Projects the raw measurement window onto SVG coordinates for the RTT sparkline (&#167;8), with no
/// charting library. The geometry is pure (a function of the samples and the box size), so it is
/// fully unit-testable and the component only has to emit the resulting strings.
/// </summary>
/// <remarks>
/// Clean RTT samples form connected polyline runs; a lost sample (an outage) breaks the line and
/// is reported separately as an <see cref="Outages"/> x-position, so gaps stay visible rather than
/// being bridged by a misleading straight segment. The y-axis is inverted in the SVG convention
/// (larger RTT sits higher, i.e. a smaller y), and the value range is auto-scaled to the box.
/// </remarks>
internal sealed record SparklineGeometry
{
    private SparklineGeometry(IReadOnlyList<string> segments, IReadOnlyList<string> outages, string? latestX, string? latestY)
    {
        Segments = segments;
        Outages = outages;
        LatestX = latestX;
        LatestY = latestY;
    }

    /// <summary>An empty graph: no segments, no outages, no current point.</summary>
    public static SparklineGeometry Empty { get; } = new([], [], null, null);

    /// <summary>
    /// Connected runs of clean samples, each a <c>"x,y x,y &#8230;"</c> string ready for a
    /// <c>&lt;polyline points="&#8230;"&gt;</c>. The line is split into multiple runs wherever an
    /// outage interrupts it.
    /// </summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The x-coordinates of lost samples, for drawing outage markers along the baseline.</summary>
    public IReadOnlyList<string> Outages { get; }

    /// <summary>The x of the most recent clean sample (the "current" dot), or <see langword="null"/> when none.</summary>
    public string? LatestX { get; }

    /// <summary>The y of the most recent clean sample (the "current" dot), or <see langword="null"/> when none.</summary>
    public string? LatestY { get; }

    /// <summary>
    /// Builds the geometry for a window of <paramref name="samples"/> (oldest first) scaled into a
    /// <paramref name="width"/> &#215; <paramref name="height"/> box. Returns <see cref="Empty"/>
    /// for an empty window.
    /// </summary>
    /// <param name="samples">The raw window, oldest sample first.</param>
    /// <param name="width">Box width in SVG user units; must be positive.</param>
    /// <param name="height">Box height in SVG user units; must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static SparklineGeometry Build(IReadOnlyList<RttSample> samples, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var count = samples.Count;
        if (count == 0)
        {
            return Empty;
        }

        // Auto-scale the vertical axis to the clean samples in the window.
        double min = double.MaxValue, max = double.MinValue;
        foreach (var sample in samples)
        {
            if (sample.RttMs is double rtt)
            {
                min = Math.Min(min, rtt);
                max = Math.Max(max, rtt);
            }
        }

        if (min == double.MaxValue)
        {
            // No clean samples: the whole window is one long outage.
            var allOut = new List<string>(count);
            var step = count > 1 ? width / (count - 1) : 0;
            for (var i = 0; i < count; i++)
            {
                allOut.Add(Coord(i * step));
            }

            return new SparklineGeometry([], allOut, null, null);
        }

        var span = max - min;
        var xStep = count > 1 ? width / (count - 1) : 0;

        var segments = new List<string>();
        var outages = new List<string>();
        var run = new List<string>();
        string? latestX = null, latestY = null;

        for (var i = 0; i < count; i++)
        {
            var x = i * xStep;
            if (samples[i].RttMs is double rtt)
            {
                // Flat line down the middle when every clean value is identical.
                var y = span > 0 ? height - ((rtt - min) / span * height) : height / 2;
                latestX = Coord(x);
                latestY = Coord(y);
                run.Add($"{latestX},{latestY}");
            }
            else
            {
                outages.Add(Coord(x));
                if (run.Count > 0)
                {
                    segments.Add(string.Join(' ', run));
                    run = [];
                }
            }
        }

        if (run.Count > 0)
        {
            segments.Add(string.Join(' ', run));
        }

        return new SparklineGeometry(segments, outages, latestX, latestY);
    }

    private static string Coord(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
