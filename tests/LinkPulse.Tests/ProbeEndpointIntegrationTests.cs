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
    public async Task A_foreign_origin_upgrade_is_rejected()
    {
        using var host = await StartHostAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConnectAsync(host, origin: "http://evil.example"));
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
