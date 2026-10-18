using Palanca.Data;

namespace Palanca.Components;

internal static class DashboardActor
{
    /// <summary>The signed-in owner, by email, as the actor of a dashboard change (AC-006).</summary>
    public static ChangeActor Of(HttpContext context) =>
        new(ChangeSource.Dashboard, context.User.Identity?.Name ?? throw new InvalidOperationException("The dashboard needs a signed-in owner."));
}
