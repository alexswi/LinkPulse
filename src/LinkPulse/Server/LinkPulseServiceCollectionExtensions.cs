using LinkPulse.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LinkPulse.Server;

/// <summary>
/// Dependency-injection wiring for the LinkPulse server side (v1 spec, &#167;12): registers the
/// in-memory registry and its supporting services so an app can light up the probe endpoint with a
/// single <c>builder.Services.AddLinkPulse()</c> call.
/// </summary>
public static class LinkPulseServiceCollectionExtensions
{
    /// <summary>Registers LinkPulse server services with the default <see cref="LinkPulseOptions"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddLinkPulse(this IServiceCollection services) =>
        services.AddLinkPulse(new LinkPulseOptions());

    /// <summary>
    /// Registers LinkPulse server services: the <see cref="LinkPulseRegistry"/> (singleton), the
    /// per-IP connection gate, a <see cref="TimeProvider"/> (if none is registered), and the
    /// background sweep service that ages connections through the stale/retention lifecycle (&#167;5.3).
    /// All registrations are additive and idempotent, so calling it twice is harmless.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="options">The options instance to use; build it with object-initializer syntax (its properties are init-only).</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddLinkPulse(this IServiceCollection services, LinkPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LinkPulseRegistry>();
        services.TryAddSingleton<LinkPulseConnectionGate>();

        // TryAddEnumerable (not AddHostedService) so a second AddLinkPulse call does not register and
        // run a duplicate sweep service — keeping the whole method idempotent.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LinkPulseSweepService>());

        return services;
    }
}
