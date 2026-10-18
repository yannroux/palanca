using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.SdkKeys;

/// <summary>Creates and revokes SDK keys (FLG-020 to FLG-022), each change recorded in its transaction (FLG-042).</summary>
internal sealed class SdkKeyCommands(PalancaDbContext db, TimeProvider timeProvider)
{
    public const int NameMaxLength = 100;

    /// <returns>The result, and on success the key itself, which is never stored, and the environment's name.</returns>
    public async Task<(CommandResult Result, string? Key, string? EnvironmentName)> CreateAsync(
        string applicationKey, string environmentKey, string? name, ChangeActor actor, CancellationToken cancellationToken)
    {
        if (Text.Clean(name, NameMaxLength) is not { } cleanName)
        {
            return (CommandResult.Invalid("Name the key in 1 to 100 characters, for example after the application that will use it."), null, null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindEnvironmentAsync(applicationKey, environmentKey, cancellationToken) is not { } environment)
        {
            return (CommandResult.NotFound("That environment does not exist or is archived."), null, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (key, prefix, hash) = SdkKeySecrets.Create();
        db.SdkKeys.Add(new SdkKey { EnvironmentId = environment.Id, Name = cleanName, Prefix = prefix, Hash = hash, CreatedAt = now });
        db.Record(actor, ChangeAction.SdkKeyCreated, now, environment.Application, environment, newValue: $"{cleanName} ({prefix})");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (CommandResult.Done, key, environment.Name);
    }

    /// <summary>Requests with the key fail from the next one onward (FLG-022).</summary>
    public async Task<CommandResult> RevokeAsync(string applicationKey, int keyId, ChangeActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var key = await db.SdkKeys.Include(k => k.Environment).ThenInclude(e => e.Application)
            .SingleOrDefaultAsync(k => k.Id == keyId && k.Environment.Application.Key == applicationKey, cancellationToken);
        if (key is null)
        {
            return CommandResult.NotFound("That key does not exist.");
        }

        if (key.RevokedAt is null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            key.RevokedAt = now;
            db.Record(actor, ChangeAction.SdkKeyRevoked, now, key.Environment.Application, key.Environment, oldValue: $"{key.Name} ({key.Prefix})");
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    private Task<AppEnvironment?> FindEnvironmentAsync(string applicationKey, string environmentKey, CancellationToken cancellationToken) =>
        db.Environments.Include(e => e.Application)
            .SingleOrDefaultAsync(e => e.Key == environmentKey && e.ArchivedAt == null
                && e.Application.Key == applicationKey && e.Application.ArchivedAt == null, cancellationToken);
}
