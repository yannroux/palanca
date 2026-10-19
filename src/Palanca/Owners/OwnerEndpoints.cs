using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;
using Palanca.Metrics;
using Palanca.Security;

namespace Palanca.Owners;

/// <summary>
/// The passkey options requests the browser script makes before calling WebAuthn, and sign-out.
/// Each options request checks the antiforgery header, because it starts a ceremony on behalf of the page.
/// </summary>
internal static class OwnerEndpoints
{
    public static IEndpointRouteBuilder MapOwnerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/sign-in/options", SignInOptionsAsync).AllowAnonymous();
        endpoints.MapPost("/enroll/options", EnrollmentOptionsAsync).AllowAnonymous();
        endpoints.MapPost("/passkeys/options", PasskeyOptionsAsync);
        endpoints.MapPost(OwnerAuthentication.SignOutPath, SignOutAsync);
        return endpoints;
    }

    private static async Task<IResult> SignInOptionsAsync(
        HttpContext context, IAntiforgery antiforgery, FailedAuthenticationLimiter limiter, SignInManager<Owner> signInManager)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        if (limiter.IsLimited(context.Connection.RemoteIpAddress))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // No user: the browser offers every passkey it holds for this relying party.
        return Results.Content(await signInManager.MakePasskeyRequestOptionsAsync(null), "application/json");
    }

    private static async Task<IResult> EnrollmentOptionsAsync(
        HttpContext context, [FromBody] EnrollmentOptionsRequest request, IAntiforgery antiforgery,
        FailedAuthenticationLimiter limiter, OwnerAccounts accounts, PalancaDbContext db, SignInManager<Owner> signInManager)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        var clientAddress = context.Connection.RemoteIpAddress;
        if (limiter.IsLimited(clientAddress))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var enrollment = await accounts.FindEnrollmentAsync(request.Token, context.RequestAborted);
        if (enrollment is null)
        {
            limiter.RecordFailure(clientAddress, FailedCredential.EnrollmentLink);
            return Results.BadRequest();
        }

        if (await db.OwnerPasskeys.CountAsync(p => p.OwnerId == enrollment.OwnerId, context.RequestAborted) >= OwnerAccounts.MaxPasskeys)
        {
            return Results.Conflict();
        }

        return Results.Content(await signInManager.MakePasskeyCreationOptionsAsync(OwnerAccounts.UserEntity(enrollment.Owner)), "application/json");
    }

    private static async Task<IResult> PasskeyOptionsAsync(
        HttpContext context, IAntiforgery antiforgery, UserManager<Owner> userManager, PalancaDbContext db, SignInManager<Owner> signInManager)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        var owner = await userManager.GetUserAsync(context.User);
        if (owner is null)
        {
            return Results.Unauthorized();
        }

        if (await db.OwnerPasskeys.CountAsync(p => p.OwnerId == owner.Id, context.RequestAborted) >= OwnerAccounts.MaxPasskeys)
        {
            return Results.Conflict();
        }

        return Results.Content(await signInManager.MakePasskeyCreationOptionsAsync(OwnerAccounts.UserEntity(owner)), "application/json");
    }

    // The antiforgery middleware only flags a failed check; an endpoint that binds no form must refuse the request itself.
    private static async Task<IResult> SignOutAsync(HttpContext context, IAntiforgery antiforgery, SignInManager<Owner> signInManager)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        await signInManager.SignOutAsync();
        return Results.LocalRedirect($"{OwnerAuthentication.SignInPath}?status=signed-out");
    }

    private sealed record EnrollmentOptionsRequest(string? Token);
}
