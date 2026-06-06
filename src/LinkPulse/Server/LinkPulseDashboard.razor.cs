using System.Globalization;
using LinkPulse.Abstractions;
using LinkPulse.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace LinkPulse.Server;

/// <summary>
/// The operator-facing dashboard (v1 spec, &#167;10): a routable Blazor Server component that lists
/// every connection the <see cref="LinkPulseRegistry"/> tracks &#8212; a sortable, filterable table
/// with a summary bar and expandable per-row detail (per-session breakdown, server-side RTT sparkline
/// with outage markers, and an optional user-agent). It is read-only apart from a manual
/// "remove stale entry" action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization (&#167;11).</b> The component is default-deny: its content is wrapped in an
/// <c>AuthorizeView</c>, so an unauthenticated or unauthorized user only ever sees an access-denied
/// notice. Set <see cref="Policy"/> and/or <see cref="Roles"/> to gate it on a specific authorization
/// policy or role; with neither set it requires an authenticated user (anonymous is denied). The host
/// places it on its own authorized route or page.
/// </para>
/// <para>
/// <b>Live updates (&#167;10).</b> It subscribes to <see cref="LinkPulseRegistry.Changed"/> and, in
/// response to those notifications, re-reads the registry at most once per second &#8212; coalescing
/// bursts so a thousand reporting clients cannot thrash the UI. The displayed last-seen/uptime ages
/// tick each second; the registry is only re-queried when something actually changed. (User-initiated
/// sort, filter, and remove actions rebuild on demand.)
/// </para>
/// </remarks>
public sealed partial class LinkPulseDashboard : IAsyncDisposable
{
    private const string ModulePath = "./_content/LinkPulse/LinkPulseDashboard.razor.js";
    private const double SparklineWidth = 240;
    private const double SparklineHeight = 40;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private IReadOnlyList<ConnectionRow> _rows = [];
    private DashboardSummary _summary = DashboardSummary.Empty;

    private DashboardColumn _sortColumn = DashboardColumn.Quality;
    private bool _sortDescending;
    private QualityRating? _qualityFilter;
    private LivenessFilter _livenessFilter = LivenessFilter.All;
    private Guid? _expanded;

    private ITimer? _timer;
    private volatile bool _dirty;
    private DateTimeOffset _now;
    private IJSObjectReference? _module;
    private bool _disposed;

    [Inject]
    private LinkPulseRegistry Registry { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <summary>
    /// The authorization policy required to view the dashboard (&#167;11). When <see langword="null"/>,
    /// no specific policy is required and any authenticated user is allowed.
    /// </summary>
    [Parameter]
    public string? Policy { get; set; }

    /// <summary>
    /// A comma-separated list of roles allowed to view the dashboard (&#167;11), passed through to the
    /// internal <c>AuthorizeView</c>. When <see langword="null"/>, role membership is not checked.
    /// </summary>
    [Parameter]
    public string? Roles { get; set; }

    /// <summary>Quality-classification tier boundaries used to rate each row (&#167;6); defaults to <see cref="QualityThresholds.Default"/>.</summary>
    [Parameter]
    public QualityThresholds Thresholds { get; set; } = QualityThresholds.Default;

    /// <summary>
    /// Whether the expandable row detail shows the connection's user-agent (&#167;10/&#167;11). Off by
    /// default because the user-agent is an identifying field (the client IP, also identifying, is
    /// shown ungated &#8212; see ADR-0001).
    /// </summary>
    [Parameter]
    public bool ShowUserAgent { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Registry.Changed += OnRegistryChanged;
        Rebuild(); // render the current snapshot immediately (and during prerender)
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        // Start the 1 Hz live-update timer only once the component is interactive — never during static
        // prerender, whose instance is created and disposed without ever ticking. This mirrors the
        // client component, which likewise defers its live machinery to the first render.
        if (firstRender && !_disposed)
        {
            _timer = Time.CreateTimer(OnTick, state: null, RefreshInterval, RefreshInterval);
        }
    }

    // Raised on the registry's thread (a probe handler or the sweep service). Just flag dirty — the
    // 1 Hz timer does the actual re-read and re-render on the renderer's synchronization context.
    private void OnRegistryChanged(object? sender, EventArgs e) => _dirty = true;

    private void OnTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        _ = RefreshAsync(); // fire-and-forget is intentional; RefreshAsync never throws

        async Task RefreshAsync()
        {
            try
            {
                // Marshal onto the renderer's context: StateHasChanged and the registry read must not
                // run on the timer's thread-pool thread.
                await InvokeAsync(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _now = Time.GetUtcNow();
                    if (_dirty)
                    {
                        // Only re-query/re-project the table when something actually changed (no
                        // full-table polling); otherwise we just refresh the ticking ages below.
                        _dirty = false;
                        Rebuild();
                    }

                    StateHasChanged();
                });
            }
            catch (Exception)
            {
                // A telemetry widget must never fault its host circuit: a torn-down renderer during
                // disposal, or a transient projection fault, skips this one refresh rather than letting
                // an unobserved exception tear down the operator's dashboard. The next tick re-reads a
                // fresh snapshot. (v1 ships without a logger; this is the call site that must log once
                // the deferred logger lands.)
            }
        }
    }

    private void Rebuild()
    {
        _now = Time.GetUtcNow();
        var views = Registry.GetConnections();
        var filter = new DashboardFilter(_qualityFilter, _livenessFilter);
        _rows = DashboardProjection.Project(views, _now, Thresholds, filter, _sortColumn, _sortDescending);

        // The summary covers every connection, independent of the table filter.
        _summary = DashboardSummary.From(DashboardProjection.Project(views, _now, Thresholds, DashboardFilter.None));
    }

    private void SortBy(DashboardColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        Rebuild();
    }

    private void OnQualityFilterChanged(ChangeEventArgs e)
    {
        _qualityFilter = Enum.TryParse<QualityRating>(e.Value as string, out var rating) ? rating : null;
        Rebuild();
    }

    private void OnLivenessFilterChanged(ChangeEventArgs e)
    {
        _livenessFilter = Enum.TryParse<LivenessFilter>(e.Value as string, out var liveness) ? liveness : LivenessFilter.All;
        Rebuild();
    }

    private void ToggleExpand(Guid clientId) => _expanded = _expanded == clientId ? null : clientId;

    private void RemoveEntry(Guid clientId)
    {
        Registry.Remove(clientId);
        if (_expanded == clientId)
        {
            _expanded = null;
        }

        try
        {
            // Reflect the removal at once; on a projection fault, don't fault the host circuit — the
            // Changed event from Remove coalesces into the next tick and re-renders the table anyway.
            Rebuild();
        }
        catch (Exception)
        {
            // See OnTick: a telemetry widget must never tear down the operator's circuit.
        }
    }

    private async Task CopyClientIdAsync(Guid clientId)
    {
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await _module.InvokeVoidAsync("copyText", clientId.ToString());
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            // Clipboard unavailable, blocked, or the circuit is gone. The full id is in the cell's
            // title attribute for manual copy, so a failed copy is a no-op rather than an error.
        }
    }

    private string SortCaret(DashboardColumn column) =>
        _sortColumn != column ? string.Empty : _sortDescending ? " ▼" : " ▲";

    private string FormatAge(DateTimeOffset timestampUtc) =>
        DurationFormat.Compact((_now - timestampUtc).TotalSeconds) + " ago";

    private string FormatUptime(DateTimeOffset firstSeenUtc) =>
        DurationFormat.Compact((_now - firstSeenUtc).TotalSeconds);

    private static string FormatMs(double? ms) =>
        ms is double v ? string.Create(CultureInfo.InvariantCulture, $"{v:0.#} ms") : "—";

    private static string FormatPct(double? pct) =>
        pct is double v ? string.Create(CultureInfo.InvariantCulture, $"{v:0.#}%") : "—";

    private static string ShortId(Guid clientId) => clientId.ToString()[..8];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Registry.Changed -= OnRegistryChanged;

        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        try
        {
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            // The circuit is already being torn down; the JS side goes with it.
        }
    }
}
