using System.Globalization;

namespace LinkPulse.Rendering;

/// <summary>
/// Formats an elapsed duration for the badge/panel: the connection uptime and the
/// "reconnecting&#8230; {n}s" count-up (&#167;5.2). Pure and culture-invariant so the rendered text
/// is deterministic and unit-testable.
/// </summary>
internal static class DurationFormat
{
    /// <summary>
    /// Renders a non-negative duration compactly: <c>"42s"</c> under a minute, <c>"3m 05s"</c>
    /// under an hour, and <c>"2h 07m"</c> beyond. Sub-second precision is dropped (the displays
    /// tick once per second), and negative inputs clamp to zero.
    /// </summary>
    /// <param name="totalSeconds">Elapsed time in seconds.</param>
    public static string Compact(double totalSeconds)
    {
        var seconds = totalSeconds > 0 ? (long)totalSeconds : 0;

        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds}s");
        }

        if (seconds < 3600)
        {
            var (m, s) = (seconds / 60, seconds % 60);
            return string.Create(CultureInfo.InvariantCulture, $"{m}m {s:00}s");
        }

        var (h, rem) = (seconds / 3600, seconds % 3600);
        return string.Create(CultureInfo.InvariantCulture, $"{h}h {rem / 60:00}m");
    }
}
