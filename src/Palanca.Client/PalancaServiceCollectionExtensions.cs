using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Palanca.Client;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Palanca client.</summary>
public static class PalancaServiceCollectionExtensions
{
    /// <summary>
    /// Adds the client: <see cref="IPalancaFlags"/> for reads and a hosted service that keeps the flags current.
    /// </summary>
    public static IServiceCollection AddPalanca(this IServiceCollection services, Action<PalancaOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        return services.AddPalancaClient();
    }

    /// <summary>Adds the client, bound to a configuration section such as <c>Palanca</c>.</summary>
    public static IServiceCollection AddPalanca(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<PalancaOptions>(configuration);
        return services.AddPalancaClient();
    }

    private static IServiceCollection AddPalancaClient(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ClientConfiguration.Read);
        services.TryAddSingleton<ClientState>();
        services.TryAddSingleton<ClientMetrics>();
        services.TryAddSingleton<IPalancaFlags, PalancaFlags>();
        services.TryAddSingleton<PalancaHealthCheck>();

        // Each sync sets its own deadline, which also covers reading the body. The client retries and logs once per
        // outage itself, so the handlers an application adds to every client, such as retries, and the factory's
        // per-request logs are left out of this one.
        services.AddHttpClient(PalancaSync.HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
            .RemoveAllLoggers();
        services.AddHostedService<PalancaSync>();
        return services;
    }
}

/// <summary>Registers the Palanca client's health check.</summary>
public static class PalancaHealthChecksBuilderExtensions
{
    private static readonly IHealthCheck NotAdded = new NotAddedCheck();

    /// <summary>
    /// Adds a check that is Healthy while the flags are fresh and Degraded when they are stale or missing. It is
    /// never Unhealthy: a check that throws reports Degraded, and so does a check registered without <c>AddPalanca</c>.
    /// </summary>
    public static IHealthChecksBuilder AddPalanca(this IHealthChecksBuilder builder, string name = "palanca", IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The health check service lets an exception from this factory fail the whole report, so it must not throw.
        return builder.Add(new HealthCheckRegistration(
            name, provider => provider.GetService<PalancaHealthCheck>() ?? NotAdded, HealthStatus.Degraded, tags));
    }

    private sealed class NotAddedCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Degraded("The Palanca client is not registered: call services.AddPalanca(...)."));
    }
}
