using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;
using Palanca.Metrics;
using Palanca.Security;

namespace Palanca.Management;

/// <summary>
/// Requires a valid, unexpired, unrevoked management token on every management request (FLG-081). The token is the
/// only credential: the dashboard's session cookie and SDK keys authorize nothing here (FLG-087). Failed attempts count
/// toward the per-address limit shared with the evaluation API and sign-in (FLG-032).
/// </summary>
internal sealed class ManagementAuthentication(PalancaDbContext db, FailedAuthenticationLimiter limiter, TimeProvider timeProvider) : IEndpointFilter
{
    public static readonly TimeSpan LastUseInterval = TimeSpan.FromMinutes(1);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var cancellationToken = context.RequestAborted;
        var clientAddress = context.Connection.RemoteIpAddress;
        context.Response.Headers.CacheControl = "no-store";
        if (limiter.IsLimited(clientAddress))
        {
            return ManagementProblems.TooManyFailures();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var caller = await FindCallerAsync(BearerSecrets.FromRequest(context.Request), now, cancellationToken);
        if (caller is null)
        {
            limiter.RecordFailure(clientAddress, FailedCredential.ManagementToken);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return ManagementProblems.Unauthorized();
        }

        await RecordLastUseAsync(caller.TokenId, now, cancellationToken);
        context.Features.Set(caller);
        return await next(invocation);
    }

    // Revocation and expiry are checked on every request, so either takes effect on the token's next request.
    private async Task<ManagementCaller?> FindCallerAsync(string? bearer, DateTime now, CancellationToken cancellationToken)
    {
        if (BearerSecrets.ManagementTokens.Parse(bearer) is not { } presented)
        {
            return null;
        }

        var candidates = await db.ManagementTokens.AsNoTracking()
            .Where(t => t.Prefix == presented.Prefix && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => new { t.Id, t.Name, t.Hash, t.Permission, t.AllApplications, ApplicationIds = t.Applications.Select(a => a.Id).ToList() })
            .ToListAsync(cancellationToken);
        var token = candidates.Find(t => CryptographicOperations.FixedTimeEquals(t.Hash, presented.Hash));
        return token is null
            ? null
            : new ManagementCaller(token.Id, token.Name, token.Permission, token.AllApplications, token.ApplicationIds.ToHashSet());
    }

    // At most once per minute per token, like SDK keys. A request never fails because the database is busy with a change.
    private async Task RecordLastUseAsync(int tokenId, DateTime now, CancellationToken cancellationToken)
    {
        var due = now - LastUseInterval;
        try
        {
            await db.ManagementTokens.Where(t => t.Id == tokenId && (t.LastUsedAt == null || t.LastUsedAt <= due))
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAt, now), cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            // SQLITE_BUSY or SQLITE_LOCKED: skip this minute's write.
        }
    }
}
