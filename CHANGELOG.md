# Changelog

All notable changes to LinkPulse are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-06-06

### Added
- **Client IP on the operator dashboard**: each connection's server-observed remote address is shown as
  a sortable column (ordinal order, missing addresses last), resolved via forwarded-headers behind a
  proxy and normalized from IPv4-mapped IPv6 to plain dotted IPv4. Shown ungated by design — the
  dashboard is a default-deny internal ops tool (see `docs/adr/0001-client-ip-shown-ungated-on-dashboard.md`).

### Changed
- **Detail panel RTT display split into two metrics**: the combined RTT line is now a separate "RTT"
  (window average) and "Range" (min–max spread) that flow side by side as full-size peers; the range
  collapses to a single dash when no snapshot has arrived yet.

## [1.1.0] - 2026-06-06

### Added
- NuGet package icon shown on nuget.org (embedded `icon.jpg`, not the deprecated icon URL).

## [1.0.0] - 2026-06-06

Initial release.

### Added
- **Client badge** (`<LinkPulse />`): measures real-time connection quality — RTT, jitter, and packet
  loss over a rolling window — via a dedicated WebSocket probe, derives a stabilized quality rating
  (hysteresis-filtered), and reports snapshots to the server. Renders as a compact badge, an expandable
  detail panel (RTT sparkline with outage markers, live metrics), or hidden. Measurement is consistent
  across the Blazor Auto Server↔WebAssembly transition.
- **Expandable detail panel** that floats as a popover anchored to the badge, so a fixed-height/sticky
  host toolbar can never clip it, with an explicit close button.
- **Server registry & probe endpoint**: `AddLinkPulse()` DI wiring and `MapLinkPulseProbe()` WebSocket
  echo endpoint; an in-memory registry tracking one entry per client across sessions, a background
  staleness sweep, and bounded user-agent capture.
- **Operator dashboard** (`<LinkPulseDashboard />`): authorization-gated, live (≤1 update/sec), sortable
  and filterable table with a summary bar, per-row server-side RTT sparkline, copyable client id, and a
  "remove stale entry" action.
- **Demo host app**: a Blazor Auto Web App wiring the badge and dashboard end to end behind a
  cookie-authenticated, default-deny route.
- **Zero third-party runtime dependencies**: the entire surface comes from the ASP.NET Core shared
  framework via a single `FrameworkReference`. Themeable via `--lp-*` CSS custom properties.

[1.2.0]: https://github.com/alexswi/LinkPulse/releases/tag/v1.2.0
[1.1.0]: https://github.com/alexswi/LinkPulse/releases/tag/v1.1.0
[1.0.0]: https://github.com/alexswi/LinkPulse/releases/tag/v1.0.0
