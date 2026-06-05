// LinkPulse.razor.js — collocated RCL module for the <LinkPulse /> component.
//
// This module is deliberately "dumb": it owns only the browser-side concerns that cannot live in
// .NET — the raw WebSocket probe channel, high-resolution timing (performance.now), the Page
// Visibility adaptive cadence, reconnect backoff, and the localStorage client identity. Every
// timestamp it reads is handed to .NET verbatim; all metric math (RTT, jitter, loss, quality)
// runs in the C# MeasurementEngine, which is exactly why that engine takes its time from outside.
//
// One probe instance per <LinkPulse /> component; createProbe returns a handle the component holds
// as an IJSObjectReference and calls send()/dispose() on.

const CLIENT_ID_KEY = "linkpulse.clientId";

// A UUID that works everywhere. crypto.randomUUID() is the happy path but is only defined in secure
// contexts (HTTPS / localhost), so plain-http intranet hosting would throw; fall back to an RFC 4122
// v4 built from crypto.getRandomValues (available in insecure contexts too), then to Math.random as
// a last resort. This never throws, so identity is always available.
function newId() {
    try {
        if (typeof crypto !== "undefined" && crypto.randomUUID) {
            return crypto.randomUUID();
        }
    } catch {
        // secure-context-only; fall through
    }

    const bytes = new Uint8Array(16);
    if (typeof crypto !== "undefined" && crypto.getRandomValues) {
        crypto.getRandomValues(bytes);
    } else {
        for (let i = 0; i < 16; i++) {
            bytes[i] = Math.floor(Math.random() * 256);
        }
    }
    bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
    bytes[8] = (bytes[8] & 0x3f) | 0x80; // variant 10
    const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0"));
    return `${hex.slice(0, 4).join("")}-${hex.slice(4, 6).join("")}-${hex.slice(6, 8).join("")}-${hex.slice(8, 10).join("")}-${hex.slice(10, 16).join("")}`;
}

// Stable per-browser id (spec §4): minted once, persisted, survives reloads and the Server→WASM
// transition. Falls back to an ephemeral id when storage is unavailable (private mode), so
// measurement still works — only cross-reload grouping is lost.
function getClientId() {
    try {
        let id = localStorage.getItem(CLIENT_ID_KEY);
        if (!id) {
            id = newId();
            localStorage.setItem(CLIENT_ID_KEY, id);
        }
        return id;
    } catch {
        return newId();
    }
}

// Resolves the probe path against the document base and forces the ws/wss scheme to match the page,
// so a relative "/connection-probe" reaches the app's own origin over the right transport.
function probeUrl(probePath) {
    const url = new URL(probePath, document.baseURI);
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    return url.toString();
}

export function createProbe(dotNet, probePath, pingIntervalMs, hiddenTabPingIntervalMs) {
    const clientId = getClientId();
    const initialReconnectMs = 1000;
    const maxReconnectMs = 30000;

    let socket = null;
    let sessionId = null;
    let seq = 0;
    let connected = false;
    let disposed = false;
    let reconnectAttempt = 0;
    let tickTimer = null;
    let reconnectTimer = null;

    // Fire-and-forget interop: the .NET object reference may already be torn down when a callback
    // lands (component disposed mid-flight), so swallow the resulting rejection.
    const notify = (method, ...args) => {
        if (disposed) {
            return;
        }
        dotNet.invokeMethodAsync(method, ...args).catch(() => { });
    };

    const currentInterval = () =>
        document.visibilityState === "hidden" ? hiddenTabPingIntervalMs : pingIntervalMs;

    // A single self-rescheduling loop drives everything. Its period is the adaptive ping cadence
    // (1 Hz visible / 5 s hidden, §3.4); .NET decides snapshot timing itself from the timestamps,
    // so the loop needs only to (a) send a ping when connected and (b) hand .NET the current clock.
    function tick() {
        if (disposed) {
            return;
        }

        const now = performance.now();
        if (connected && socket && socket.readyState === WebSocket.OPEN) {
            seq += 1;
            try {
                socket.send(JSON.stringify({ type: "ping", seq, payload: now.toString() }));
                notify("OnPing", seq, now);
            } catch {
                // Socket dropped between the readyState check and send; onclose will handle it.
            }
        }

        notify("OnTick", now);
        tickTimer = setTimeout(tick, currentInterval());
    }

    function connect() {
        if (disposed) {
            return;
        }

        let ws;
        try {
            ws = new WebSocket(probeUrl(probePath));
        } catch {
            scheduleReconnect();
            return;
        }

        socket = ws;

        ws.onopen = () => {
            if (disposed) {
                try { ws.close(); } catch { /* ignore */ }
                return;
            }
            connected = true;
            reconnectAttempt = 0;
            sessionId = newId(); // fresh per connection (spec §4)
            notify("OnConnected", clientId, sessionId, performance.now());
        };

        ws.onmessage = (event) => {
            const t1 = performance.now();
            let frame;
            try {
                frame = JSON.parse(event.data);
            } catch {
                return; // not our frame
            }
            // Echo of our ping, matched by seq in .NET (out-of-order safe). payload is ignored here:
            // .NET already holds t0 keyed by seq, so only the receive stamp is needed.
            if (frame && frame.type === "ping" && typeof frame.seq === "number") {
                notify("OnEcho", frame.seq, t1);
            }
        };

        ws.onerror = () => {
            try { ws.close(); } catch { /* onclose follows */ }
        };

        ws.onclose = () => {
            if (socket !== ws) {
                return; // superseded by a newer socket
            }
            const wasConnected = connected;
            connected = false;
            socket = null;
            if (wasConnected) {
                notify("OnDisconnected", performance.now());
            }
            scheduleReconnect();
        };
    }

    // Exponential backoff with full jitter (spec §5.2): 1→2→4…s, capped at 30 s, retried forever.
    // Math.min absorbs the eventual 2**n → Infinity, so the cap holds without overflow.
    function scheduleReconnect() {
        if (disposed) {
            return;
        }
        const ceiling = Math.min(maxReconnectMs, initialReconnectMs * 2 ** reconnectAttempt);
        reconnectAttempt += 1;
        reconnectTimer = setTimeout(connect, Math.random() * ceiling);
    }

    // Becoming visible should resume the fast cadence at once rather than waiting out a 5 s hidden
    // tick, so cancel the pending tick and fire immediately.
    function onVisibility() {
        if (!disposed && document.visibilityState === "visible") {
            if (tickTimer) {
                clearTimeout(tickTimer);
            }
            tick();
        }
    }

    document.addEventListener("visibilitychange", onVisibility);
    connect();
    tick();

    return {
        // .NET pushes a fully-serialized snapshot frame; we just put it on the wire when open.
        send(json) {
            if (socket && socket.readyState === WebSocket.OPEN) {
                try { socket.send(json); } catch { /* drop; next snapshot will follow */ }
            }
        },
        dispose() {
            disposed = true;
            document.removeEventListener("visibilitychange", onVisibility);
            if (tickTimer) {
                clearTimeout(tickTimer);
            }
            if (reconnectTimer) {
                clearTimeout(reconnectTimer);
            }
            if (socket) {
                try { socket.close(); } catch { /* ignore */ }
            }
            socket = null;
        },
    };
}
