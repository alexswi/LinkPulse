# LinkPulse

A drop-in Blazor component and server dashboard that measures and reports the quality of a
client's live connection. This glossary fixes the language used across the component, the
measurement engine, and the dashboard.

## Language

### Round-trip

**RTT**:
A round-trip time for one ping, in milliseconds. As a panel metric, "RTT" means the **average**
clean RTT over the measurement window (`RttAvg`); the collapsed badge instead shows the **latest**
instantaneous RTT. A "clean" round-trip is one that was paired with its echo and passed the sanity
guards — losses and discards are excluded.
_Avoid_: latency, ping (for the metric)

**RTT range**:
The minimum-to-maximum spread of clean RTTs over the window (`RttMin`–`RttMax`), shown in the panel
as its own "Range" metric beside the average. The average always sits within this range.
_Avoid_: spread, min/max

**Window**:
The rolling buffer of recent samples (clean RTTs and losses) the metrics are computed over. Drives
both the numeric metrics and the sparkline.

### Connection metadata

**Client IP**:
The network address the probe connection arrived from — the server's view of the client's remote
address (resolved by the host's forwarded-headers middleware behind a proxy), not a value the client
reports. Latest-wins per client: a reconnect from a new **known** address replaces it; a reconnect
with no resolvable address leaves the last known IP in place. Shown on the dashboard; distinct from
the **ClientId**, which is a stable per-browser identifier, not an address.
_Avoid_: remote address, host, source IP
