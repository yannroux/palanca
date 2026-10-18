using Palanca.Data;

namespace Palanca.Management;

/// <summary>The management token behind a request, once <see cref="ManagementAuthentication"/> has accepted it.</summary>
/// <param name="ApplicationIds">The applications in the token's scope, unless it covers all of them.</param>
internal sealed record ManagementCaller(int TokenId, string Name, TokenPermission Permission, bool AllApplications, IReadOnlySet<int> ApplicationIds)
{
    /// <summary>The token, by name and id, as the actor of the changes it makes (FLG-084).</summary>
    public ChangeActor Actor => new(ChangeSource.Api, Name, TokenId);

    /// <summary>Whether the token's scope covers the application (FLG-082).</summary>
    public bool Reaches(int applicationId) => AllApplications || ApplicationIds.Contains(applicationId);
}

internal static class ManagementCallerExtensions
{
    /// <summary>The caller of a management endpoint. Every management endpoint runs behind the token check, which sets it.</summary>
    public static ManagementCaller ManagementCaller(this HttpContext context) =>
        context.Features.Get<ManagementCaller>() ?? throw new InvalidOperationException("The management API needs an authenticated token.");
}
