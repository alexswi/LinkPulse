# LinkPulse

**Real-time connection-quality monitoring for Blazor.**

LinkPulse measures the network quality of a Blazor client — round-trip time, jitter, and packet loss — and surfaces it both at the client (a compact, expandable quality badge) and on the server (a live dashboard of every connection and its parameters).

It is built for **Blazor Auto** and measures consistently across the render-mode transition: the same dedicated WebSocket probe runs whether the component is currently in its Server phase or its WebAssembly phase, so the numbers stay comparable for a client's whole lifetime.

> Status: **v1**. In-memory registry, component-parameter configuration, read-only dashboard. See [Roadmap](#roadmap).

---

## Features

- **Clock-skew-safe measurement** — RTT is computed entirely on the client clock with `performance.now()`. The server only echoes ping frames, so cross-machine clock differences never affect the result.
- **RTT, jitter, and packet loss** over a rolling window. Jitter uses the RFC 3550 interarrival estimator.
- **Works across the Auto transition** — one WebSocket probe channel, independent of the Blazor circuit, used identically in the Server and WebAssembly phases.
- **Weakest-link quality rating** — `Excellent / Good / Fair / Poor`, plus a distinct `Disconnected` state, with hysteresis to prevent flicker.
- **Compact, accessible client component** — colored dot + label + RTT, expandable to a full panel with a sparkline. Color is never the only signal.
- **Live server dashboard** — every connection grouped by a stable client id, sortable and filterable, with per-session detail, outage markers, and a quality distribution summary.
- **Resilient** — exponential reconnect backoff on the client; the server infers disconnection from snapshot silence and retains stale entries for later reconciliation.
- **Zero external dependencies** — no charting or SignalR-client packages. Pure CSS/SVG rendering keeps the WebAssembly bundle light.

---

## Requirements

- .NET 10 or later
- A Blazor Auto (or Server / WebAssembly) application

---

## Installation

```bash
dotnet add package LinkPulse
```

> If the `LinkPulse` package id is unavailable on nuget.org, the published id may be `LinkPulse.Blazor`. Check `nuget.org/packages/LinkPulse` or run `dotnet package search LinkPulse --exact-match`.

---

## Quick start

**1. Register services and map the probe endpoint** in `Program.cs`:

```csharp
using LinkPulse.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLinkPulse();

var app = builder.Build();

app.MapLinkPulseProbe();   // maps the WebSocket probe at /connection-probe (path configurable)

app.Run();
```

**2. Drop the client component** onto a layout or page:

```razor
@using LinkPulse

<LinkPulse />
```

It self-initializes, discovers the probe path, and starts measuring immediately — no further wiring required.

**3. Add the dashboard** to an authorized page:

```razor
@using LinkPulse.Server
@attribute [Authorize(Policy = "LinkPulseViewer")]

<LinkPulseDashboard />
```

The dashboard is **default-deny** — it exposes client identifiers and activity, so it must sit behind authorization. The policy name is configurable.

---

## The client component

`<LinkPulse />` is compact by default and expands on click.

- **Collapsed:** a colored dot (green / yellow-green / amber / red / grey for disconnected), a one-word label, and the current RTT in milliseconds.
- **Expanded:** RTT (min / avg / max), jitter, packet loss, current phase (Server or WebAssembly), uptime, and an RTT sparkline over the rolling window.
- **Disconnected:** an explicit `Disconnected — reconnecting… {n}s` state with last-known metrics greyed out, rather than a blank panel or a misleading "poor" rating.

### Display modes

| Mode | Behavior |
|------|----------|
| `Badge` *(default)* | Compact badge, expandable to a panel. |
| `Panel` | Always-expanded panel. |
| `Hidden` | Measures silently and reports to the server while rendering nothing — useful for pages that only feed the dashboard. |

Colors are themed via CSS custom properties, so the host application controls the palette.

---

## Configuration

In v1, configuration is done through component parameters. The server enforces hard bounds (e.g. a minimum ping interval) so a misconfiguration cannot turn the probe into a self-inflicted denial of service.

```razor
<LinkPulse Display="Panel"
           PingIntervalMs="2000"
           WindowSize="60" />
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `PingIntervalMs` | `1000` | Ping cadence while the tab is visible (bounded ≥ 100 ms). |
| `HiddenTabPingIntervalMs` | `5000` | Ping cadence while the tab is hidden. |
| `WindowSize` | `30` | Rolling sample count for all derived metrics. |
| `PingTimeoutMs` | `5000` | No echo within this window counts as a lost ping. |
| `JitterSmoothingFactor` | `16` | RFC 3550 jitter estimator gain. |
| `StaleThresholdMs` | `30000` | No-snapshot duration after which a connection is marked stale. |
| `StaleRetentionMs` | `7200000` | How long stale entries are retained (2 h) before removal. |
| `Display` | `Badge` | `Badge`, `Panel`, or `Hidden`. |
| Quality thresholds | see below | Tunable RTT / jitter / loss boundaries. |

### Quality rating

The overall rating is the **worst** of three independent sub-ratings (RTT, jitter, loss), so a problem in any one dimension cannot hide behind healthy values elsewhere. A new level must hold for three consecutive snapshots before the display changes.

| Metric | Excellent | Good | Fair | Poor |
|--------|-----------|------|------|------|
| RTT (avg) | < 50 ms | < 150 ms | < 300 ms | ≥ 300 ms |
| Jitter | < 10 ms | < 30 ms | < 60 ms | ≥ 60 ms |
| Loss | 0 % | < 1 % | < 5 % | ≥ 5 % |

Thresholds are exposed as parameters and can be tightened for latency-sensitive apps or loosened for tolerant ones.

---

## The server dashboard

`<LinkPulseDashboard />` is a routable Blazor Server component.

- A sortable, filterable table with one row per client, defaulting to **worst quality first**.
- Columns: status, client id, active session count, phase, RTT average, jitter, loss, last-seen age, and uptime.
- Expand a row for per-session detail, a server-side RTT sparkline, outage markers, and timestamps.
- A summary bar shows total / live / stale counts and the quality distribution.
- Updates are pushed as snapshots arrive and throttled to roughly one per second, so a large fleet of clients does not thrash the UI.

---

## How it works

```
 Client (Server or WASM phase)                       Server
 ─────────────────────────────                       ──────
 performance.now() stamps each ping
 ring buffer over the rolling window
 computes RTT / jitter / loss
 derives quality rating
        │  ping (seq, opaque send-stamp)
        ├───────────────────────────────────────────▶  echoes the frame verbatim
        │  echo (verbatim)                               (never parses the timing payload)
        ◀───────────────────────────────────────────┤
        │  snapshot (aggregates) every 5s
        ├───────────────────────────────────────────▶  in-memory registry keyed by ClientId
                                                         pushes deltas to the dashboard (≤ 1/s)
```

- **RTT** is `performance.now() − t0`, measured purely on the client. `performance.now()` is monotonic, so NTP corrections, manual clock changes, and VM pauses cannot produce negative or absurd values.
- **Pings carry a monotonic sequence number.** A ping with no echo within `PingTimeoutMs` is counted as lost. Late echoes (arriving after their timeout) are discarded entirely so they cannot inflate the received count or corrupt jitter. Out-of-order echoes are matched by sequence number.
- **Snapshots** carrying the computed aggregates are reported to the server every 5 seconds over the same socket.

### Client identity

| Identifier | Lifetime | Role |
|------------|----------|------|
| `ClientId` | Random GUID in `localStorage`; survives reconnects, refreshes, navigation, and the Server→WASM transition. | Primary dashboard grouping key; stale retention attaches to it. |
| `SessionId` | Fresh GUID per probe connection. | Distinguishes a reconnect from two tabs open at once (one `ClientId`, multiple live `SessionId`s). |

On reconnect the client presents its `ClientId`, and the server updates the existing entry in place rather than creating a duplicate.

---

## Security & privacy

- The **dashboard** is default-deny and must be placed behind an authorization policy. It is never anonymous.
- The **probe endpoint** must work pre-authentication and during the WebAssembly phase, so it is open to any client of the app, but hardened: it rejects WebSocket upgrades from foreign origins, enforces the minimum ping interval, and caps concurrent sessions per IP.
- Every snapshot is treated as untrusted input — validated, bounded, and never reflected unsanitized into the dashboard. `ClientId` is an opaque random GUID that grants nothing.
- The only potentially identifying field is the user-agent, which is optional and can be disabled.

---

## Packaging

A single Razor Class Library targeting `net10.0`, shipping both the client components and the server registry, probe endpoint, and dashboard.

| Namespace | Contents |
|-----------|----------|
| `LinkPulse` | Client components |
| `LinkPulse.Server` | Registry, probe endpoint, dashboard |
| `LinkPulse.Abstractions` | Snapshot and options DTOs shared across the render-mode boundary |

JavaScript interop (Page Visibility, WebSocket, high-resolution timing) ships as a collocated RCL module loaded on demand — no global script tags.

---

## Roadmap

Planned beyond v1:

- Layered configuration via dependency injection and `appsettings` binding.
- Persistent storage for the connection registry (survives restarts).
- Server-initiated active probing of silent clients.
- Dashboard admin actions (e.g. force-disconnect).

---

## Contributing

Issues and pull requests are welcome. Please open an issue to discuss substantial changes before submitting a PR.

---

## License

See [LICENSE](LICENSE).
