using Microsoft.Extensions.Hosting;

namespace LinkPulse.Server;

/// <summary>
/// A background service that periodically advances the registry lifecycle (v1 spec, &#167;5.3) by
/// calling <see cref="LinkPulseRegistry.Sweep"/>. A dead client cannot report, so staleness and
/// retention removal cannot be driven by incoming traffic alone &#8212; this timer is what makes a
/// silent connection flip to stale and eventually be removed even when nothing else touches the registry.
/// </summary>
internal sealed class LinkPulseSweepService(LinkPulseRegistry registry, TimeProvider timeProvider) : BackgroundService
{
    // Far finer than the 30 s default stale threshold, so the dashboard's "last seen" age is timely
    // without the sweep itself being a meaningful load.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                registry.Sweep(timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
