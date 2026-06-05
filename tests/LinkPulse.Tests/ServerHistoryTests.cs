using LinkPulse.Abstractions;
using LinkPulse.Server;

namespace LinkPulse.Tests;

/// <summary>
/// Tests the history-to-sample projection (&#167;9/&#167;10) that feeds the dashboard's server-side
/// sparkline: reported points contribute their average RTT, and outage markers become lost samples so
/// the shared sparkline renderer breaks the line at the gap rather than bridging it (&#167;5.3).
/// </summary>
public sealed class ServerHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ConnectionHistoryPoint Point(double rttAvg, int second) =>
        new(T0.AddSeconds(second), new MetricSnapshot
        {
            Phase = ClientPhase.Server,
            RttMin = rttAvg,
            RttAvg = rttAvg,
            RttMax = rttAvg,
            Jitter = 1,
            LossPct = 0,
            SampleCount = 30,
        });

    [Fact]
    public void Reported_points_map_to_their_average_rtt_and_outages_map_to_lost_samples()
    {
        ConnectionHistoryPoint[] history =
        [
            Point(rttAvg: 30, second: 0),
            ConnectionHistoryPoint.Outage(T0.AddSeconds(1)),
            Point(rttAvg: 45, second: 2),
        ];

        var samples = ServerHistory.ToSamples(history);

        Assert.Collection(
            samples,
            s => { Assert.Equal(0, s.Seq); Assert.Equal(30, s.RttMs); Assert.False(s.IsLost); },
            s => { Assert.Equal(1, s.Seq); Assert.Null(s.RttMs); Assert.True(s.IsLost); },
            s => { Assert.Equal(2, s.Seq); Assert.Equal(45, s.RttMs); Assert.False(s.IsLost); });
    }

    [Fact]
    public void An_empty_history_yields_no_samples()
    {
        Assert.Empty(ServerHistory.ToSamples([]));
    }
}
