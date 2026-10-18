using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;
using Palanca.Security;

namespace Palanca.ManagementTokens;

/// <summary>A token just created, on its way to the page that shows it once.</summary>
internal sealed record NewToken(string Name, string Token);

/// <summary>
/// Creates and revokes management tokens. Only the dashboard does this; the management API cannot (FLG-083, FLG-085).
/// Each change is recorded in its transaction (FLG-042).
/// </summary>
internal sealed class ManagementTokenCommands(PalancaDbContext db, TimeProvider timeProvider)
{
    public const int NameMaxLength = 100;

    /// <summary>The lifetimes the dashboard offers, in days. The longest, 12 months, is the default (FLG-083).</summary>
    public static readonly IReadOnlyList<int> LifetimeDays = [30, 90, 180, ManagementToken.MaxLifetimeDays];

    /// <param name="applicationKeys">The applications in the token's scope, unless <paramref name="allApplications"/> is set.</param>
    /// <returns>The result, and on success the token itself, which is never stored.</returns>
    public async Task<(CommandResult Result, NewToken? Created)> CreateAsync(
        string? name, TokenPermission? permission, bool allApplications, IReadOnlyCollection<string> applicationKeys, int lifetimeDays,
        ChangeActor actor, CancellationToken cancellationToken)
    {
        if (Text.Clean(name, NameMaxLength) is not { } cleanName)
        {
            return (CommandResult.Invalid("Name the token in 1 to 100 characters, for example after the agent or script that will use it."), null);
        }

        if (permission is not { } chosenPermission || !Enum.IsDefined(chosenPermission))
        {
            return (CommandResult.Invalid("Choose whether the token only reads flags or can also change them."), null);
        }

        if (!LifetimeDays.Contains(lifetimeDays))
        {
            return (CommandResult.Invalid("Choose when the token expires: after 30, 90, 180 or 365 days."), null);
        }

        if (!allApplications && applicationKeys.Count == 0)
        {
            return (CommandResult.Invalid("Choose the applications the token can reach, or all of them."), null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The history names the token that made each API change, so two active tokens never share a name.
        if (await db.ManagementTokens.AnyAsync(t => t.Name == cleanName && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken))
        {
            return (CommandResult.Conflict($"An active token is already named {cleanName}. Its changes are recorded under that name, so choose another."), null);
        }

        var applications = allApplications
            ? []
            : await db.Applications.Where(a => applicationKeys.Contains(a.Key) && a.ArchivedAt == null).OrderBy(a => a.Key).ToListAsync(cancellationToken);
        if (!allApplications && applicationKeys.Except(applications.Select(a => a.Key)).FirstOrDefault() is { } unknown)
        {
            return (CommandResult.Invalid($"There is no application with the key {unknown}, or it is archived."), null);
        }

        var (secret, prefix, hash) = BearerSecrets.ManagementTokens.Create();
        var token = new ManagementToken
        {
            Name = cleanName,
            Prefix = prefix,
            Hash = hash,
            Permission = chosenPermission,
            AllApplications = allApplications,
            CreatedAt = now,
            ExpiresAt = now.AddDays(lifetimeDays),
        };
        token.Applications.AddRange(applications);
        db.ManagementTokens.Add(token);
        db.Record(actor, ChangeAction.ManagementTokenCreated, now, newValue: Described(token));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (CommandResult.Done, new NewToken(cleanName, secret));
    }

    /// <summary>Requests with the token fail from the next one onward.</summary>
    public async Task<CommandResult> RevokeAsync(int tokenId, ChangeActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.ManagementTokens.SingleOrDefaultAsync(t => t.Id == tokenId, cancellationToken) is not { } token)
        {
            return CommandResult.NotFound("That token does not exist.");
        }

        if (token.RevokedAt is null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            token.RevokedAt = now;
            db.Record(actor, ChangeAction.ManagementTokenRevoked, now, oldValue: $"{token.Name} ({token.Prefix})");
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    // The scope never changes, so the record of the creation is where the history shows what the token can do.
    private static string Described(ManagementToken token)
    {
        var permission = token.Permission == TokenPermission.ReadWrite ? "read-write" : "read-only";
        var scope = token.AllApplications ? "all applications" : string.Join(", ", token.Applications.Select(a => a.Key));
        return $"{token.Name} ({token.Prefix}): {permission}, {scope}, expires {token.ExpiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }
}
