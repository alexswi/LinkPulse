using System.Security.Claims;
using LinkPulse.Server;
using LinkPulseDemo.Authentication;
using LinkPulseDemo.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Razor Components with both interactive render modes (Blazor Auto). Authentication is handled entirely
// server-side (the dashboard and badge run InteractiveServer), so there is no need to flow auth state to
// the WebAssembly runtime — the only WASM-capable page is the stock Counter (render mode InteractiveAuto),
// which needs no authorization.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Cookie authentication — no Identity, no database. The demo signs a user in as an "operator" so the
// default-deny dashboard (§11) can be exercised end to end; a real app would plug in its own scheme.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/denied";
    });

// The policy the dashboard is gated on (requires an authenticated operator). The same name is also passed
// to the <LinkPulseDashboard> component so it authorizes against the identical policy.
builder.Services.AddAuthorization(options =>
    options.AddPolicy(DemoAuth.ViewerPolicy, policy =>
        policy.RequireAuthenticatedUser().RequireRole(DemoAuth.OperatorRole)));

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, DemoAuthenticationStateProvider>();

// LinkPulse server side: the registry, the per-IP gate, and the background sweep service.
builder.Services.AddLinkPulse();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Required by the LinkPulse probe endpoint, which upgrades to a WebSocket.
app.UseWebSockets();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// The probe endpoint is intentionally open (§11): it must work before sign-in and during the WASM phase.
app.MapLinkPulseProbe();

// Demo-only auth endpoints. Antiforgery is disabled on these plain-HTML form posts to keep the sample
// minimal; a production app would post from an antiforgery-protected form.
app.MapPost("/account/login", async (HttpContext http, [FromForm] string? username, [FromForm] string? returnUrl) =>
{
    var name = string.IsNullOrWhiteSpace(username) ? "operator" : username.Trim();
    Claim[] claims = [new(ClaimTypes.Name, name), new(ClaimTypes.Role, DemoAuth.OperatorRole)];
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/dashboard" : returnUrl);
}).DisableAntiforgery();

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(LinkPulseDemo.Client._Imports).Assembly);

app.Run();
