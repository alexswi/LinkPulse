# Client IP is shown ungated on the dashboard

The operator dashboard shows each connection's **Client IP** as an always-visible table column,
with no opt-in toggle — unlike the User-Agent, which is hidden behind `ShowUserAgent` (default off)
and was documented as "the only potentially identifying field."

We accept this deliberately. The dashboard is an internal operations tool that is already
default-deny authorized (`AuthorizeView` on Policy/Roles); everyone who can see it is trusted, and
identifying which client a connection belongs to is the whole point of the feature. Gating the IP
the way the User-Agent is gated would add a knob that, for this tool's audience, would almost always
be turned on.

## Consequences

- It is inconsistent with the User-Agent's opt-in posture: the *more* identifying field (a raw IP,
  arguably PII under GDPR) is always on while the *milder* one is opt-in. The `ShowUserAgent` doc
  comment claiming UA is "the only potentially identifying field" is updated to reflect this.
- It is a one-way door for exposure: flipping back to gated later is a cheap code change, but IPs
  already seen by operators cannot be un-exposed. Hosts with stricter privacy needs should weigh
  this before deploying the dashboard.
