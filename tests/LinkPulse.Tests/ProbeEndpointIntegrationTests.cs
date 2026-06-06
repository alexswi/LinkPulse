using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LinkPulse.Abstractions;
using LinkPulse.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LinkPulse.Tests;

/// <summary>
/// End-to-end tests for the probe endpoint (&#167;12) hosted in an in-memory <see cref="TestServer"/>:
/// the ping echo round-trips verbatim (&#167;3.5), snapshot frames land in the registry (&#167;9), a
/// non-WebSocket request is refused, and a foreign-Origin upgrade is rejected (&#167;11).
/// </summary>
public sealed class ProbeEndpointIntegrationTests
{
    private const string ProbePath = "/connection-probe";
    private const string SameOrigin = "http://localhost";

    private static async Task<IHost> StartHostAsync(LinkPulseOptions? options = null, IPAddress? remoteIp = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        if (options is null)
                        {
                            services.AddLinkPulse();
                        }
                        else
                        {
                            services.AddLinkPulse(options);
                        }
                    })
                    .Configure(app =>
                    {
                        // The in-memory TestServer leaves Connection.RemoteIpAddress null; stand in for the
                        // host's networking (or its forwarded-headers middleware) so the §10 IP capture can
                        // be exercised end to end.
                        if (remoteIp is not null)
                        {
                            app.Use(async (context, next) =>
                            {
                                context.Connection.RemoteIpAddress = remoteIp;
                                await next();
                            });
                        }

                        app.UseWebSockets();
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapLinkPulseProbe(ProbePath));
                    });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<WebSocket> ConnectAsync(IHost host, string? origin = SameOrigin, string? userAgent = null)
    {
        var server = host.GetTestServer();
        var client = server.CreateWebSocketClient();
        if (origin is not null || userAgent is not null)
        {
            client.ConfigureRequest = request =>
            {
                if (origin is not null)
                {
                    request.Headers.Origin = origin;
                }

                if (userAgent is not null)
                {
                    request.Headers.UserAgent = userAgent;
                }
            };
        }

        var uri = new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = ProbePath }.Uri;
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await socket.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Fact]
    public async Task A_ping_frame_is_echoed_back_byte_for_byte()
    {
        using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host);

        // A deliberately odd shape — extra field, specific spacing — proves the server echoes the raw
        // bytes rather than re-serializing (which would never reproduce the opaque payload exactly).
        const string ping = """{"type":"ping","seq":7,"payload":"opaque-STAMP-42","extra":"keep me"}""";
        await SendTextAsync(socket, ping);

        var echo = await ReceiveTextAsync(socket);

        Assert.Equal(ping, echo);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_snapshot_frame_lands_in_the_registry()
    {
        using var host = await StartHostAsync();
        var registry = host.Services.GetRequiredService<LinkPulseRegistry>();
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        using (var socket = await ConnectAsync(host))
        {
            ProbeFrame frame = SnapshotFrame.FromSnapshot(clientId, sessionId, new MetricSnapshot
            {
                Phase = ClientPhase.Server,
                RttMin = 30,
                RttAvg = 33,
                RttMax = 40,
                Jitter = 2,
                LossPct = 0,
                SampleCount = 30,
            });
            var json = JsonSerializer.Serialize(frame, LinkPulseJsonContext.Default.ProbeFrame);
            await SendTextAsync(socket, json);

            var view = await SpinUntilAsync(() =>
                registry.TryGetConnection(clientId, out var v) ? v : null);

            Assert.NotNull(view);
            Assert.Equal(33, view!.LatestSnapshot!.RttAvg);
            Assert.Equal(ClientPhase.Server, view.Phase);
            Assert.Contains(sessionId, view.ActiveSessions);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_non_websocket_request_is_refused_with_400()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync(ProbePath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_foreign_origin_upgrade_is_rejected_with_403()
    {
        using var host = await StartHostAsync();

        // The TestHost client surfaces the failed handshake's status code in the exception message;
        // asserting 403 (not just "some failure") proves it was the origin check that refused, and
        // distinguishes it from the non-WebSocket 400 path.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => ConnectAsync(host, origin: "http://evil.example"));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task A_request_without_an_origin_header_is_allowed()
    {
        // Native (non-browser) clients send no Origin; that is not the cross-site threat the check
        // targets, so the upgrade must succeed and the socket must work.
        using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, origin: null);

        const string ping = """{"type":"ping","seq":1,"payload":"y"}""";
        await SendTextAsync(socket, ping);

        Assert.Equal(ping, await ReceiveTextAsync(socket));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_malformed_snapshot_is_discarded_and_the_socket_stays_open()
    {
        using var host = await StartHostAsync();
        var registry = host.Services.GetRequiredService<LinkPulseRegistry>();
        using var socket = await ConnectAsync(host);

        // clientId is the empty GUID — the validator rejects it (§11). The frame must be dropped, not
        // recorded, and the connection must stay usable.
        const string badSnapshot =
            """{"type":"snapshot","clientId":"00000000-0000-0000-0000-000000000000","sessionId":"11111111-1111-1111-1111-111111111111","phase":"Server","rttMin":1,"rttAvg":1,"rttMax":1,"jitter":0,"lossPct":0,"sampleCount":1}""";
        await SendTextAsync(socket, badSnapshot);

        // A following ping still round-trips, proving the socket was not torn down by the bad frame.
        const string ping = """{"type":"ping","seq":1,"payload":"z"}""";
        await SendTextAsync(socket, ping);

        Assert.Equal(ping, await ReceiveTextAsync(socket));
        Assert.Equal(0, registry.Count);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Closing_the_socket_deregisters_the_session()
    {
        using var host = await StartHostAsync();
        var registry = host.Services.GetRequiredService<LinkPulseRegistry>();
        var clientId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var socket = await ConnectAsync(host);
        try
        {
            ProbeFrame frame = SnapshotFrame.FromSnapshot(clientId, sessionId, new MetricSnapshot
            {
                Phase = ClientPhase.Server,
                RttMin = 1,
                RttAvg = 1,
                RttMax = 1,
                Jitter = 0,
                LossPct = 0,
                SampleCount = 1,
            });
            await SendTextAsync(socket, JsonSerializer.Serialize(frame, LinkPulseJsonContext.Default.ProbeFrame));

            // Wait until the session is registered as active.
            var active = await SpinUntilAsync(() =>
                registry.TryGetConnection(clientId, out var v) && v!.ActiveSessions.Count > 0 ? v : null);
            Assert.NotNull(active);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

            // The handler's finally-block deregisters the session; the entry itself is retained.
            var deregistered = await SpinUntilAsync(() =>
                registry.TryGetConnection(clientId, out var v) && v!.ActiveSessions.Count == 0 ? v : null);
            Assert.NotNull(deregistered);
        }
        finally
        {
            socket.Dispose();
        }
    }

    [Fact]
    public async Task The_connection_user_agent_is_captured_on_the_registry_entry()
    {
        using var host = await StartHostAsync();
        var registry = host.Services.GetRequiredService<LinkPulseRegistry>();
        var clientId = Guid.NewGuid();

        using var socket = await ConnectAsync(host, userAgent: "Mozilla/5.0 (probe-test)");
        await SendSnapshotAsync(socket, clientId, Guid.NewGuid());

        var view = await SpinUntilAsync(() =>
            registry.TryGetConnection(clientId, out var v) && v!.UserAgent is not null ? v : null);

        Assert.NotNull(view);
        Assert.Equal("Mozilla/5.0 (probe-test)", view!.UserAgent);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task The_client_ip_is_captured_and_ipv4_mapped_addresses_are_normalised()
    {
        // The client connects over an IPv4-mapped IPv6 address (how a dual-stack socket commonly reports
        // an IPv4 peer); the dashboard value must read as plain dotted IPv4, not the ::ffff: form.
        var mapped = IPAddress.Parse("203.0.113.7").MapToIPv6();
        Assert.True(mapped.IsIPv4MappedToIPv6); // guard: we are actually exercising the normalisation path

        using var host = await StartHostAsync(remoteIp: mapped);
        var registry = host.Services.GetRequiredService<LinkPulseRegistry>();
        var clientId = Guid.NewGuid();

        using var socket = await ConnectAsync(host);
        await SendSnapshotAsync(socket, clientId, Guid.NewGuid());

        var view = await SpinUntilAsync(() =>
            registry.TryGetConnection(clientId, out var v) && v!.ClientIp is not null ? v : null);

        Assert.NotNull(view);
        Assert.Equal("203.0.113.7", view!.ClientIp);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task An_over_long_user_agent_is_truncated_to_the_bound()
    {
        using var host = await StartHostAsync();
        var registry = host.Services.GetRequiredService<LinkPulseRegistry>();
        var clientId = Guid.NewGuid();

        // 300 chars — past the 256 untrusted-input bound (§11); the stored value must be capped.
        using var socket = await ConnectAsync(host, userAgent: new string('a', 300));
        await SendSnapshotAsync(socket, clientId, Guid.NewGuid());

        var view = await SpinUntilAsync(() =>
            registry.TryGetConnection(clientId, out var v) && v!.UserAgent is not null ? v : null);

        Assert.NotNull(view);
        var length = view!.UserAgent!.Length;
        Assert.Equal(256, length);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_client_reconnecting_in_the_wasm_phase_updates_one_entry_in_place()
    {
        // The Blazor Auto transition end to end: a browser reports in its Server phase, drops the socket,
        // then reconnects with a new session in its WebAssembly phase under the SAME ClientId. The probe
        // must fold both into one registry entry whose phase follows the client across the boundary — the
        // measurement-continuity guarantee, exercised over the real WebSocket endpoint.
        using var host = await StartHostAsync();
        var registry = host.Services.GetRequiredService<LinkPulseRegistry>();
        var clientId = Guid.NewGuid();

        using (var serverPhase = await ConnectAsync(host))
        {
            await SendSnapshotAsync(serverPhase, clientId, Guid.NewGuid(), ClientPhase.Server);
            // Assert (not just await) that the Server-phase snapshot landed: otherwise a silent failure to
            // record it would let the test "prove" continuity it never actually exercised.
            var serverView = await SpinUntilAsync(() =>
                registry.TryGetConnection(clientId, out var v) && v!.Phase == ClientPhase.Server ? v : null);
            Assert.NotNull(serverView);
            await serverPhase.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        using (var wasmPhase = await ConnectAsync(host))
        {
            await SendSnapshotAsync(wasmPhase, clientId, Guid.NewGuid(), ClientPhase.Wasm);

            var view = await SpinUntilAsync(() =>
                registry.TryGetConnection(clientId, out var v) && v!.Phase == ClientPhase.Wasm ? v : null);

            Assert.NotNull(view);
            Assert.Equal(ClientPhase.Wasm, view!.Phase);

            // Continuity, not just a phase flip: the Server-phase sample is still the oldest point in the
            // retained history, so the reconnect extended the measurement rather than replacing it.
            Assert.True(view.History.Count >= 2);
            Assert.Equal(ClientPhase.Server, view.History[0].Snapshot!.Phase);

            await wasmPhase.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        // One client throughout — the reconnect updated the existing entry rather than creating a second.
        Assert.Equal(1, registry.Count);
    }

    private static Task SendSnapshotAsync(WebSocket socket, Guid clientId, Guid sessionId, ClientPhase phase = ClientPhase.Server)
    {
        ProbeFrame frame = SnapshotFrame.FromSnapshot(clientId, sessionId, new MetricSnapshot
        {
            Phase = phase,
            RttMin = 10,
            RttAvg = 12,
            RttMax = 15,
            Jitter = 1,
            LossPct = 0,
            SampleCount = 30,
        });
        return SendTextAsync(socket, JsonSerializer.Serialize(frame, LinkPulseJsonContext.Default.ProbeFrame));
    }

    // Polls a thread-safe read until it returns non-null or a short timeout elapses. The probe handler
    // records snapshots asynchronously, so the registry update may lag the send by a scheduling tick.
    private static async Task<T?> SpinUntilAsync<T>(Func<T?> read, int timeoutMs = 5_000) where T : class
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (read() is { } value)
            {
                return value;
            }

            await Task.Delay(25);
        }

        return read();
    }
}
