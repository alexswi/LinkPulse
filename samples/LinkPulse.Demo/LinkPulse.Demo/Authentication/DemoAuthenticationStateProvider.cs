using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace LinkPulseDemo.Authentication;

/// <summary>
/// A minimal server-side <see cref="AuthenticationStateProvider"/> that surfaces the cookie-authenticated
/// user to Blazor's authorization components. It captures the principal once, at construction (when the
/// circuit's <see cref="HttpContext"/> is still available), and returns that same state for the circuit's
/// lifetime — so an interactive component's <c>AuthorizeView</c> keeps seeing the signed-in user rather
/// than reverting to anonymous after the first render, which a naive per-call <c>IHttpContextAccessor</c>
/// read would do once the circuit is established.
/// </summary>
internal sealed class DemoAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly Task<AuthenticationState> _state;

    public DemoAuthenticationStateProvider(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        _state = Task.FromResult(new AuthenticationState(user));
    }

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;
}
