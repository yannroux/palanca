using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Palanca.Data;
using Palanca.Security;

namespace Palanca.Evaluation;

internal sealed record FlagsResponse(string Application, string Environment, long Revision, IReadOnlyDictionary<string, bool> Flags);

/// <summary>
/// <c>GET /api/v1/flags</c> (FLG-030, FLG-031): every non-archived flag of one environment, for the environment's SDK
/// key. Consumers reach it on the private networks only; Caddy does not route it.
/// </summary>
internal static class EvaluationEndpoints
{
    public static readonly TimeSpan LastUseInterval = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The SDK key is the only credential: the dashboard's session cookie authorizes nothing here.
        endpoints.MapGet("/api/v1/flags", GetFlagsAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> GetFlagsAsync(HttpContext context, PalancaDbContext db, FailedAuthenticationLimiter limiter, TimeProvider timeProvider)
    {
        var cancellationToken = context.RequestAborted;
        var clientAddress = context.Connection.RemoteIpAddress;
        context.Response.Headers.CacheControl = "no-store";
        if (limiter.IsLimited(clientAddress))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var key = await FindKeyAsync(db, BearerToken(context.Request), cancellationToken);
        if (key is null)
        {
            // Missing, unknown, revoked, or archived: the answer is the same, so it says nothing about which.
            limiter.RecordFailure(clientAddress);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (key.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUseInterval)
        {
            await RecordLastUseAsync(db, key.Id, now, cancellationToken);
        }

        // The revision was read with the key, before the flags. A change committed in between makes the flags newer
        // than the ETag, so the client fetches again on its next poll; the reverse order could cache stale values.
        var environment = key.Environment;
        var etag = new EntityTagHeaderValue($"\"{environment.Revision}\"");
        context.Response.Headers.ETag = etag.ToString();
        if (context.Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } ifNoneMatch
            && ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var flags = await db.Flags.AsNoTracking()
            .Where(f => f.ApplicationId == environment.ApplicationId && f.ArchivedAt == null)
            .OrderBy(f => f.Key)
            .Select(f => new { f.Key, Enabled = f.Values.Any(v => v.EnvironmentId == environment.Id && v.Enabled) })
            .ToListAsync(cancellationToken);
        return Results.Json(new FlagsResponse(
            environment.Application.Key, environment.Key, environment.Revision, flags.ToDictionary(f => f.Key, f => f.Enabled)));
    }

    // At most once per minute per key (FLG-023), even when several consumers poll with it at once. A poll never fails
    // because the database is busy with a dashboard change: last use is a convenience, the flags are the answer.
    private static async Task RecordLastUseAsync(PalancaDbContext db, int keyId, DateTime now, CancellationToken cancellationToken)
    {
        var due = now - LastUseInterval;
        try
        {
            await db.SdkKeys.Where(k => k.Id == keyId && (k.LastUsedAt == null || k.LastUsedAt <= due))
                .ExecuteUpdateAsync(set => set.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            // SQLITE_BUSY or SQLITE_LOCKED: skip this minute's write.
        }
    }

    private static async Task<SdkKey?> FindKeyAsync(PalancaDbContext db, string? bearer, CancellationToken cancellationToken)
    {
        if (BearerSecrets.SdkKeys.Parse(bearer) is not { } presented)
        {
            return null;
        }

        var candidates = await db.SdkKeys.AsNoTracking()
            .Include(k => k.Environment).ThenInclude(e => e.Application)
            .Where(k => k.Prefix == presented.Prefix && k.RevokedAt == null
                && k.Environment.ArchivedAt == null && k.Environment.Application.ArchivedAt == null)
            .ToListAsync(cancellationToken);
        return candidates.Find(k => CryptographicOperations.FixedTimeEquals(k.Hash, presented.Hash));
    }

    private static string? BearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string Scheme = "Bearer ";
        return authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? authorization[Scheme.Length..].Trim() : null;
    }
}
