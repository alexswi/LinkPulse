using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;

namespace LinkPulseDemo.Authentication;

/// <summary>
/// Flows the cookie-authenticated user into Blazor's authorization components for the interactive Server
/// circuit. Subclassing <see cref="RevalidatingServerAuthenticationStateProvider"/> means the framework
/// seeds the authentication state from the connecting (authenticated) request and keeps it for the
/// circuit's lifetime — so an interactive component's <c>AuthorizeView</c> keeps seeing the signed-in
/// user. (A provider that read <c>IHttpContextAccessor</c> instead would see a null <c>HttpContext</c>
/// once the circuit is established and wrongly flip authorized content to its denied state.)
/// </summary>
internal sealed class DemoAuthenticationStateProvider(ILoggerFactory loggerFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <summary>The demo's cookie does not change mid-circuit, so revalidation can be infrequent.</summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    /// <summary>The demo has no revocation store, so a signed-in user stays valid until the cookie expires.</summary>
    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
