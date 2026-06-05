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

    private static async Task<IHost> StartHostAsync(LinkPulseOptions? options = null)
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
                        app.UseWebSockets();
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapLinkPulseProbe(ProbePath));
                    });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<WebSocket> ConnectAsync(IHost host, string? origin = SameOrigin)
    {
        var server = host.GetTestServer();
        var client = server.CreateWebSocketClient();
        if (origin is not null)
        {
            client.ConfigureRequest = request => request.Headers.Origin = origin;
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
