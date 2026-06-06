# Login name is shown ungated on the dashboard

The operator dashboard shows each connection's **login name** — the authenticated identity on the
probe request (`context.User.Identity?.Name`) — as an always-visible table column, with no opt-in
toggle. This matches the **Client IP** column (ADR-0001) and is deliberately *unlike* the
User-Agent, which is hidden behind `ShowUserAgent` (default off).

We accept this deliberately, for the same reasons as the IP. The dashboard is an internal operations
tool that is already default-deny authorized (`AuthorizeView` on Policy/Roles); everyone who can see
it is trusted, and identifying *who* is on the other end of a probe connection — not merely what
address it came from — is the whole point of the feature. Gating the login name the way the
User-Agent is gated would add a knob that, for this tool's audience, would almost always be on.

The probe endpoint stays anonymous-usable: it reads the identity opportunistically *if present* and
never starts requiring auth (§11). An anonymous connection has no login name and renders `—`.

## Consequences

- A login name is **more identifying than a raw IP** — it is personal data naming a person, not just
  an address. This sharpens the inconsistency ADR-0001 already recorded: the dashboard now has *two*
  identifying fields always on (IP, login name) while the *milder* User-Agent stays opt-in. We accept
  the same trade-off for the same reason, and extend ADR-0001's consequence rather than revisit it.
- It is a one-way door for exposure: flipping back to gated later is a cheap code change, but names
  already seen by operators cannot be un-exposed. Hosts with stricter privacy needs should weigh this
  before deploying the dashboard, and should note that the field carries the host's authenticated
  identity verbatim (an email or federated UPN may itself be sensitive).
- The value is bounded before it is stored and rendered. Its provenance is the host's auth middleware
  (not an attacker-controlled request header like the User-Agent), so it is not untrusted input in the
  §11 sense; the bound is defense-in-depth, so a pathological identity cannot bloat a registry entry
  or break the table layout.
