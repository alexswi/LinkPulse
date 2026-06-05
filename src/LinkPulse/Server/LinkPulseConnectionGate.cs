using System.Collections.Concurrent;
using System.Net;
using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// Enforces the per-IP concurrent-session cap (v1 spec, &#167;11): an abuse bound so a single host
/// cannot open an unbounded number of probe sockets and exhaust server resources. Registered as a
/// singleton; the probe endpoint acquires a slot before accepting an upgrade and releases it when the
/// socket closes.
/// </summary>
internal sealed class LinkPulseConnectionGate
{
    private readonly ConcurrentDictionary<IPAddress, int> _counts = new();
    private readonly int _maxPerIp;

    public LinkPulseConnectionGate(LinkPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxPerIp = options.MaxConcurrentSessionsPerIp;
    }

    /// <summary>
    /// Atomically reserves a session slot for <paramref name="ip"/> if the cap is not already reached.
    /// </summary>
    /// <returns><see langword="true"/> if a slot was taken (the caller must later <see cref="Release"/> it); otherwise <see langword="false"/>.</returns>
    public bool TryAcquire(IPAddress ip)
    {
        while (true)
        {
            var current = _counts.GetOrAdd(ip, 0);
            if (current >= _maxPerIp)
            {
                return false;
            }

            if (_counts.TryUpdate(ip, current + 1, current))
            {
                return true;
            }
            // Lost the race to another connection from the same IP; re-read and retry.
        }
    }

    /// <summary>Releases a slot previously taken by <see cref="TryAcquire"/>, pruning the bucket when it empties.</summary>
    public void Release(IPAddress ip)
    {
        while (_counts.TryGetValue(ip, out var current))
        {
            if (current <= 1)
            {
                // Remove the exact pair so we never delete a bucket another connection just grew.
                if (_counts.TryRemove(KeyValuePair.Create(ip, current)))
                {
                    return;
                }
            }
            else if (_counts.TryUpdate(ip, current - 1, current))
            {
                return;
            }
            // Concurrent change; re-read and retry.
        }
    }
}
