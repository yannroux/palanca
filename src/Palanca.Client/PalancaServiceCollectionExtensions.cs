using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Palanca.Client;

namespace Microsoft.Extensions.DependencyInjection;

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
        services.TryAddSingleton(provider => ClientConfiguration.From(provider.GetRequiredService<IOptions<PalancaOptions>>().Value));
        services.TryAddSingleton<ClientState>();
        services.TryAddSingleton<ClientMetrics>();
        services.TryAddSingleton<IPalancaFlags, PalancaFlags>();
        services.TryAddSingleton<PalancaHealthCheck>();

        // The client logs once per outage itself, so the factory's per-request logs would only repeat each poll.
        services.AddHttpClient(PalancaSync.HttpClientName, http => http.Timeout = PalancaSync.RequestTimeout).RemoveAllLoggers();
        services.AddHostedService<PalancaSync>();
        return services;
    }
}

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
