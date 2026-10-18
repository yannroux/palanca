using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Palanca.Owners;

/// <summary>
/// Session limits for the dashboard cookie (FLG-048): a session ends 8 hours after sign-in or after
/// 30 minutes without a request, whichever comes first, and every request checks the owner's security stamp (FLG-047).
/// </summary>
internal static class OwnerSession
{
    public const string CookieName = "__Host-Palanca.Session";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);

    // The cookie's issue time moves on every renewal, so the sign-in time travels in the ticket instead.
    private const string SignedInAtKey = "palanca.signed_in_at";

    public static AuthenticationProperties NewProperties(DateTimeOffset signedInAt) => new()
    {
        Items = { [SignedInAtKey] = signedInAt.ToString("O", CultureInfo.InvariantCulture) },
    };

    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var now = context.Options.TimeProvider!.GetUtcNow();
        if (!context.Properties.Items.TryGetValue(SignedInAtKey, out var value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var signedInAt)
            || now - signedInAt >= AbsoluteLifetime)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        // With a zero validation interval this checks the stamp on every request and renews the cookie,
        // which with sliding expiration moves the idle deadline to 30 minutes from now.
        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }

    /// <summary>Sends page requests to sign-in, and answers script requests with 401 so the script can say why.</summary>
    public static Task RedirectToSignInAsync(RedirectContext<CookieAuthenticationOptions> context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || request.Headers["Sec-Fetch-Mode"] == "navigate")
        {
            context.Response.Redirect(context.RedirectUri);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    }
}
