using System.Text.Json;
using LinkPulse.Abstractions;
using LinkPulse.Measurement;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace LinkPulse;

/// <summary>
/// The client-facing LinkPulse component (v1 spec, &#167;8). It measures the real-time quality of
/// the browser's connection &#8212; RTT, jitter, and packet loss over a rolling window &#8212;
/// derives a stable quality rating, reports snapshots to the server probe endpoint, and renders the
/// result as a compact badge, an expanded panel, or nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// The same code path runs in both Blazor Auto phases (Server and WebAssembly), so the numbers stay
/// comparable across the transition (&#167;2). All timing comes from a collocated JS module via
/// <c>performance.now()</c>; this component owns the <see cref="MeasurementEngine"/>, the quality
/// classification, the hysteresis filter, the snapshot cadence, and the rendering. It is
/// single-threaded by construction &#8212; every JS callback arrives on the renderer's
/// synchronization context &#8212; so no locking is needed.
/// </para>
/// <para>
/// JS interop only starts once the component is interactive (after the first render), so static
/// prerender shows the initial "connecting" markup and live measurement begins on hydration.
/// </para>
/// </remarks>
public sealed partial class LinkPulse : IAsyncDisposable
{
    private const string ModulePath = "./_content/LinkPulse/LinkPulse.razor.js";
    private const double SparklineWidth = 120;
    private const double SparklineHeight = 32;

    private MeasurementEngine _engine = null!;
    private QualityStabilizer _stabilizer = null!;
    private ClientPhase _phase;

    private DotNetObjectReference<LinkPulse>? _selfRef;
    private IJSObjectReference? _module;
    private IJSObjectReference? _probe;
    private bool _disposed;

    private Guid _clientId;
    private Guid _sessionId;

    private bool _connected;
    private bool _expanded;
    private MetricSnapshot? _snapshot;
    private double _nowMs;
    private double _lastReportAtMs;
    private double? _connectedSinceMs;
    private double? _downSinceMs;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <summary>Visible-tab ping cadence, in milliseconds (&#167;7). Bounded below server-side.</summary>
    [Parameter]
    public int PingIntervalMs { get; set; } = 1000;

    /// <summary>Hidden-tab ping cadence, in milliseconds, used while the tab is backgrounded (&#167;3.4).</summary>
    [Parameter]
    public int HiddenTabPingIntervalMs { get; set; } = 5000;

    /// <summary>Rolling sample count over which all derived metrics are computed (&#167;3.4).</summary>
    [Parameter]
    public int WindowSize { get; set; } = 30;

    /// <summary>A ping with no echo within this many milliseconds is counted as lost (&#167;3.2).</summary>
    [Parameter]
    public int PingTimeoutMs { get; set; } = 5000;

    /// <summary>RFC 3550 jitter gain denominator <c>G</c> (&#167;3.3).</summary>
    [Parameter]
    public double JitterSmoothingFactor { get; set; } = 16;

    /// <summary>Snapshot reporting cadence, in milliseconds (&#167;3.5).</summary>
    [Parameter]
    public int SnapshotIntervalMs { get; set; } = 5000;

    /// <summary>Consecutive snapshots a new rating must hold before the display changes (&#167;6).</summary>
    [Parameter]
    public int HysteresisSamples { get; set; } = 3;

    /// <summary>How the component renders: <see cref="DisplayMode.Badge"/>, <see cref="DisplayMode.Panel"/>, or <see cref="DisplayMode.Hidden"/>.</summary>
    [Parameter]
    public DisplayMode Display { get; set; } = DisplayMode.Badge;

    /// <summary>Quality-classification tier boundaries (&#167;6); defaults to <see cref="QualityThresholds.Default"/>.</summary>
    [Parameter]
    public QualityThresholds Thresholds { get; set; } = QualityThresholds.Default;

    /// <summary>The WebSocket probe path the client connects to; resolved against the app origin (&#167;12).</summary>
    [Parameter]
    public string ProbePath { get; set; } = "/connection-probe";

    /// <summary>The rating currently displayed: the stabilized measured rating, or <see cref="QualityRating.Disconnected"/> while the probe is down.</summary>
    private QualityRating DisplayedRating => _connected ? _stabilizer.Current : QualityRating.Disconnected;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _phase = OperatingSystem.IsBrowser() ? ClientPhase.Wasm : ClientPhase.Server;
        _engine = new MeasurementEngine(WindowSize, PingTimeoutMs, JitterSmoothingFactor);
        _stabilizer = new QualityStabilizer(HysteresisSamples);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }

        _selfRef = DotNetObjectReference.Create(this);
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            if (_disposed)
            {
                return; // disposed mid-import
            }

            _probe = await _module.InvokeAsync<IJSObjectReference>(
                "createProbe", _selfRef, ProbePath, PingIntervalMs, HiddenTabPingIntervalMs);
        }
        catch (JSException)
        {
            // The module failed to load or initialise (network error, blocked dynamic import, …).
            // A telemetry widget must not take down its host page, so stay in the disconnected
            // state and simply do not measure.
        }
    }

    /// <summary>JS callback: the probe socket (re)connected. Adopts the identity and re-arms measurement.</summary>
    /// <param name="clientId">The stable per-browser identifier from <c>localStorage</c>.</param>
    /// <param name="sessionId">The fresh per-connection identifier.</param>
    /// <param name="nowMs">The client-clock time of the connect, in milliseconds.</param>
    [JSInvokable]
    public void OnConnected(string clientId, string sessionId, double nowMs)
    {
        // These ids are always freshly-minted UUID strings from the JS module, so the parse cannot
        // realistically fail; the guard merely keeps a malformed value from throwing, leaving an
        // empty Guid rather than crashing the connect.
        _ = Guid.TryParse(clientId, out _clientId);
        _ = Guid.TryParse(sessionId, out _sessionId);

        // A new connection is a new measurement session (fresh SessionId): start the engine clean so
        // the dropped connection's stale window and still-outstanding pings cannot bleed into — and
        // be counted as loss against — the new one.
        _engine = new MeasurementEngine(WindowSize, PingTimeoutMs, JitterSmoothingFactor);

        _connected = true;
        _nowMs = nowMs;
        _connectedSinceMs = nowMs;
        _downSinceMs = null;
        _lastReportAtMs = nowMs;
        _stabilizer.Reset(); // first measured rating after (re)connect is adopted at once (§6 seam)
        StateHasChanged();
    }

    /// <summary>JS callback: a ping was sent. Hands the send-stamp to the engine.</summary>
    /// <param name="seq">The ping's monotonic sequence number.</param>
    /// <param name="sentAtMs">The client-clock send time, in milliseconds.</param>
    [JSInvokable]
    public void OnPing(long seq, double sentAtMs) => _engine.RecordPing(seq, sentAtMs);

    /// <summary>JS callback: an echo arrived. Pairs it to its ping by sequence number.</summary>
    /// <param name="seq">The echoed sequence number.</param>
    /// <param name="receivedAtMs">The client-clock receive time, in milliseconds.</param>
    [JSInvokable]
    public void OnEcho(long seq, double receivedAtMs) => _engine.RecordEcho(seq, receivedAtMs);

    /// <summary>
    /// JS callback fired on the adaptive cadence: ages out timed-out pings, reports a snapshot when
    /// one is due, and refreshes the live counters.
    /// </summary>
    /// <param name="nowMs">The current client-clock time, in milliseconds.</param>
    [JSInvokable]
    public async Task OnTick(double nowMs)
    {
        _nowMs = nowMs;
        _engine.ExpireOutstanding(nowMs);

        if (!_connected)
        {
            _downSinceMs ??= nowMs; // count up from first tick even before the first connect
        }
        else if (nowMs - _lastReportAtMs >= SnapshotIntervalMs)
        {
            await ReportSnapshotAsync(nowMs);
        }

        StateHasChanged();
    }

    /// <summary>JS callback: the probe socket dropped. Freezes the last-known metrics and starts the reconnect count-up.</summary>
    /// <param name="nowMs">The client-clock time of the drop, in milliseconds.</param>
    [JSInvokable]
    public void OnDisconnected(double nowMs)
    {
        _connected = false;
        _nowMs = nowMs;
        _downSinceMs = nowMs;
        _connectedSinceMs = null;
        StateHasChanged();
    }

    private async Task ReportSnapshotAsync(double nowMs)
    {
        var snapshot = _engine.CreateSnapshot(_phase);
        _snapshot = snapshot;
        _stabilizer.Push(QualityCalculator.Classify(snapshot, Thresholds));
        _lastReportAtMs = nowMs;

        if (_probe is null)
        {
            return;
        }

        ProbeFrame frame = SnapshotFrame.FromSnapshot(_clientId, _sessionId, snapshot);
        var json = JsonSerializer.Serialize(frame, LinkPulseJsonContext.Default.ProbeFrame);
        try
        {
            await _probe.InvokeVoidAsync("send", json);
        }
        catch (JSException)
        {
            // Best-effort telemetry. A torn-down circuit (JSDisconnectedException) or a transient
            // interop fault should drop this one snapshot — never propagate out of this JSInvokable
            // callback and fault the circuit, which would end the user's whole session.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_probe is not null)
            {
                await _probe.InvokeVoidAsync("dispose");
                await _probe.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            // The circuit/renderer is already being torn down; the JS side is disposed with it.
        }

        _selfRef?.Dispose();
    }
}
