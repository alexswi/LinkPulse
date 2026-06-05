using System.Net;
using LinkPulse.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LinkPulse.Server;

/// <summary>
/// Maps the LinkPulse WebSocket probe endpoint (v1 spec, &#167;12). The endpoint is intentionally
/// open (it must work pre-auth and during the WebAssembly phase, &#167;11), but hardened: it rejects
/// non-WebSocket and foreign-origin requests and enforces a per-IP concurrent-session cap before
/// accepting the upgrade.
/// </summary>
/// <remarks>
/// <para>
/// Requires the WebSocket middleware (<c>app.UseWebSockets()</c>) and the services registered by
/// <see cref="LinkPulseServiceCollectionExtensions.AddLinkPulse(IServiceCollection)"/>.
/// </para>
/// <para>
/// Behind a TLS-terminating reverse proxy, enable forwarded-headers processing
/// (<c>app.UseForwardedHeaders()</c>) before this endpoint so the origin check sees the external
/// scheme/host and the per-IP cap sees the real client address rather than the proxy's &#8212;
/// otherwise legitimate <c>https</c> browsers may be rejected and all clients may share one IP bucket.
/// </para>
/// </remarks>
public static class LinkPulseEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the probe endpoint at <paramref name="path"/> (default <see cref="LinkPulseProbeDefaults.ProbePath"/>).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="path">The request path for the probe socket; resolved against the app origin client-side.</param>
    /// <returns>A builder for further endpoint conventions on the mapped route.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> or <paramref name="path"/> is <see langword="null"/>.</exception>
    public static IEndpointConventionBuilder MapLinkPulseProbe(
        this IEndpointRouteBuilder endpoints, string path = LinkPulseProbeDefaults.ProbePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(path);

        var services = endpoints.ServiceProvider;
        var registry = services.GetRequiredService<LinkPulseRegistry>();
        var gate = services.GetRequiredService<LinkPulseConnectionGate>();
        var timeProvider = services.GetRequiredService<TimeProvider>();

        return endpoints.Map(path, async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // §11 origin check: block cross-site WebSocket hijacking. A request with no Origin is a
            // non-browser client (native tooling), which is not the cross-site threat this guards.
            if (!IsAllowedOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var ip = context.Connection.RemoteIpAddress ?? IPAddress.None;
            if (!gate.TryAcquire(ip))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await ProbeConnectionHandler.RunAsync(socket, registry, timeProvider, context.RequestAborted);
            }
            finally
            {
                gate.Release(ip);
            }
        });
    }

    private static bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin;
        if (origin.Count == 0)
        {
            return true; // no Origin: not a browser cross-site request
        }

        if (origin.Count != 1 ||
            !Uri.TryCreate(origin[0], UriKind.Absolute, out var originUri))
        {
            return false; // multiple or malformed Origin values are never legitimate
        }

        return string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && OriginPort(originUri) == RequestPort(request);
    }

    private static int OriginPort(Uri origin) =>
        origin.IsDefaultPort ? DefaultPort(origin.Scheme) : origin.Port;

    private static int RequestPort(HttpRequest request) =>
        request.Host.Port ?? DefaultPort(request.Scheme);

    private static int DefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
}
