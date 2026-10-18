using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Applications;

/// <summary>
/// Creates, renames, and archives applications and their environments (FLG-001 to FLG-003). Keys never change,
/// and each change is recorded in the same transaction (FLG-042).
/// </summary>
internal sealed class ApplicationCommands(PalancaDbContext db, TimeProvider timeProvider)
{
    public const int NameMaxLength = 200;
    public const string InvalidKey = "Use lowercase letters, digits, and hyphens, starting with a letter, up to 64 characters.";
    public const string InvalidName = "Give it a name of 1 to 200 characters.";

    public async Task<CommandResult> CreateAsync(string? key, string? name, ChangeActor actor, CancellationToken cancellationToken)
    {
        if (!Keys.IsValid(key))
        {
            return CommandResult.Invalid(InvalidKey);
        }

        if (Text.Clean(name, NameMaxLength) is not { } cleanName)
        {
            return CommandResult.Invalid(InvalidName);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Applications.AnyAsync(a => a.Key == key, cancellationToken))
        {
            return CommandResult.Conflict($"An application with the key {key} already exists.");
        }

        var now = Now;
        var application = new Application { Key = key!, Name = cleanName, CreatedAt = now };
        db.Applications.Add(application);
        await db.SaveChangesAsync(cancellationToken);
        db.Record(actor, ChangeAction.ApplicationCreated, now, application, newValue: cleanName);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    public async Task<CommandResult> RenameAsync(string applicationKey, string? name, ChangeActor actor, CancellationToken cancellationToken)
    {
        if (Text.Clean(name, NameMaxLength) is not { } cleanName)
        {
            return CommandResult.Invalid(InvalidName);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindApplicationAsync(applicationKey, cancellationToken) is not { } application)
        {
            return ApplicationNotFound;
        }

        if (application.Name != cleanName)
        {
            db.Record(actor, ChangeAction.ApplicationRenamed, Now, application, oldValue: application.Name, newValue: cleanName);
            application.Name = cleanName;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    /// <summary>Archives an application with its environments, and revokes their SDK keys (FLG-003).</summary>
    public async Task<CommandResult> ArchiveAsync(string applicationKey, ChangeActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindApplicationAsync(applicationKey, cancellationToken) is not { } application)
        {
            return ApplicationNotFound;
        }

        var now = Now;
        application.ArchivedAt = now;
        db.Record(actor, ChangeAction.ApplicationArchived, now, application);
        var environments = await db.Environments.Where(e => e.ApplicationId == application.Id && e.ArchivedAt == null).ToListAsync(cancellationToken);
        foreach (var environment in environments)
        {
            await RevokeSdkKeysAsync(application, environment, actor, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    /// <summary>Adds an environment after the others. Every flag starts off in it (FLG-010).</summary>
    public async Task<CommandResult> CreateEnvironmentAsync(
        string applicationKey, string? key, string? name, bool isProtected, ChangeActor actor, CancellationToken cancellationToken)
    {
        if (!Keys.IsValid(key))
        {
            return CommandResult.Invalid(InvalidKey);
        }

        if (Text.Clean(name, NameMaxLength) is not { } cleanName)
        {
            return CommandResult.Invalid(InvalidName);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindApplicationAsync(applicationKey, cancellationToken) is not { } application)
        {
            return ApplicationNotFound;
        }

        var siblings = await db.Environments.Where(e => e.ApplicationId == application.Id).ToListAsync(cancellationToken);
        if (siblings.Exists(e => e.Key == key))
        {
            return CommandResult.Conflict($"{application.Name} already has an environment with the key {key}.");
        }

        var now = Now;
        var environment = new AppEnvironment
        {
            ApplicationId = application.Id,
            Key = key!,
            Name = cleanName,
            IsProtected = isProtected,
            SortOrder = siblings.Count == 0 ? 0 : siblings.Max(e => e.SortOrder) + 1,
            CreatedAt = now,
        };
        db.Environments.Add(environment);
        await db.SaveChangesAsync(cancellationToken);
        db.Record(actor, ChangeAction.EnvironmentCreated, now, application, environment, newValue: isProtected ? $"{cleanName} (protected)" : cleanName);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    public async Task<CommandResult> RenameEnvironmentAsync(
        string applicationKey, string environmentKey, string? name, ChangeActor actor, CancellationToken cancellationToken)
    {
        if (Text.Clean(name, NameMaxLength) is not { } cleanName)
        {
            return CommandResult.Invalid(InvalidName);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindEnvironmentAsync(applicationKey, environmentKey, cancellationToken) is not { } environment)
        {
            return EnvironmentNotFound;
        }

        if (environment.Name != cleanName)
        {
            db.Record(actor, ChangeAction.EnvironmentRenamed, Now, environment.Application, environment, oldValue: environment.Name, newValue: cleanName);
            environment.Name = cleanName;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    /// <summary>Moves an environment one column left (<paramref name="offset"/> -1) or right (+1) in the grid.</summary>
    public async Task<CommandResult> MoveEnvironmentAsync(string applicationKey, string environmentKey, int offset, ChangeActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindEnvironmentAsync(applicationKey, environmentKey, cancellationToken) is not { } environment)
        {
            return EnvironmentNotFound;
        }

        var ordered = await db.Environments
            .Where(e => e.ApplicationId == environment.ApplicationId && e.ArchivedAt == null)
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
        var index = ordered.IndexOf(environment);
        var target = index + Math.Sign(offset);
        if (target >= 0 && target < ordered.Count)
        {
            (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
            for (var position = 0; position < ordered.Count; position++)
            {
                ordered[position].SortOrder = position;
            }

            db.Record(actor, ChangeAction.EnvironmentMoved, Now, environment.Application, environment,
                oldValue: $"column {index + 1}", newValue: $"column {target + 1}");
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    /// <summary>Protected environments ask for confirmation before any toggle (FLG-014).</summary>
    public async Task<CommandResult> SetProtectedAsync(
        string applicationKey, string environmentKey, bool isProtected, ChangeActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindEnvironmentAsync(applicationKey, environmentKey, cancellationToken) is not { } environment)
        {
            return EnvironmentNotFound;
        }

        if (environment.IsProtected != isProtected)
        {
            db.Record(actor, ChangeAction.EnvironmentProtectionChanged, Now, environment.Application, environment,
                oldValue: Protection(environment.IsProtected), newValue: Protection(isProtected));
            environment.IsProtected = isProtected;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    /// <summary>Archives an environment and revokes its SDK keys (FLG-003).</summary>
    public async Task<CommandResult> ArchiveEnvironmentAsync(string applicationKey, string environmentKey, ChangeActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindEnvironmentAsync(applicationKey, environmentKey, cancellationToken) is not { } environment)
        {
            return EnvironmentNotFound;
        }

        var now = Now;
        environment.ArchivedAt = now;
        db.Record(actor, ChangeAction.EnvironmentArchived, now, environment.Application, environment);
        await RevokeSdkKeysAsync(environment.Application, environment, actor, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    private static CommandResult ApplicationNotFound => CommandResult.NotFound("That application does not exist or is archived.");

    private static CommandResult EnvironmentNotFound => CommandResult.NotFound("That environment does not exist or is archived.");

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private static string Protection(bool isProtected) => isProtected ? "protected" : "unprotected";

    private Task<Application?> FindApplicationAsync(string key, CancellationToken cancellationToken) =>
        db.Applications.SingleOrDefaultAsync(a => a.Key == key && a.ArchivedAt == null, cancellationToken);

    private Task<AppEnvironment?> FindEnvironmentAsync(string applicationKey, string environmentKey, CancellationToken cancellationToken) =>
        db.Environments.Include(e => e.Application)
            .SingleOrDefaultAsync(e => e.Key == environmentKey && e.ArchivedAt == null
                && e.Application.Key == applicationKey && e.Application.ArchivedAt == null, cancellationToken);

    private async Task RevokeSdkKeysAsync(Application application, AppEnvironment environment, ChangeActor actor, DateTime now, CancellationToken cancellationToken)
    {
        var keys = await db.SdkKeys.Where(k => k.EnvironmentId == environment.Id && k.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var key in keys)
        {
            key.RevokedAt = now;
            db.Record(actor, ChangeAction.SdkKeyRevoked, now, application, environment, oldValue: $"{key.Name} ({key.Prefix})");
        }
    }
}
