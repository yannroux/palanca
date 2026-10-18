using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Palanca.Client;

/// <summary>
/// Healthy while the flags are fresh, Degraded otherwise (FLG-055). Never Unhealthy: an application serving code
/// defaults or older values still works, so the flag service must not take it out of rotation.
/// </summary>
internal sealed class PalancaHealthCheck(ClientState state, ClientConfiguration configuration, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var current = state.Current;
        if (configuration.Settings is not { } settings)
        {
            return Task.FromResult(HealthCheckResult.Degraded($"Every flag reads its code default. {current.Problem}."));
        }

        if (current is not { Snapshot: { } snapshot, LastSuccess: { } lastSuccess })
        {
            return Task.FromResult(HealthCheckResult.Degraded($"No flags from Palanca yet, so every flag reads its code default. {current.Problem}."));
        }

        var age = time.GetUtcNow() - lastSuccess;
        var data = new Dictionary<string, object>
        {
            ["revision"] = snapshot.Revision,
            ["lastSuccess"] = lastSuccess.ToString("O", CultureInfo.InvariantCulture),
        };
        var lastFailure = current.Problem is null ? "" : $" The last sync failed: {current.Problem}.";
        var summary = $"Serving revision {snapshot.Revision}, synced {Math.Floor(age.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds ago.{lastFailure}";
        return Task.FromResult(age > settings.StaleAfter
            ? HealthCheckResult.Degraded($"The flags are stale. {summary}", data: data)
            : HealthCheckResult.Healthy(summary, data));
    }
}
