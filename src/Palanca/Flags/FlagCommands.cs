using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Flags;

/// <summary>
/// Creates, describes, archives, restores, and deletes flags (FLG-010 to FLG-013). Values change through
/// <see cref="FlagValueWriter"/>.
/// </summary>
/// <remarks>
/// Creating, archiving, or restoring a flag changes which flags the evaluation API returns in every environment of
/// the application, so each of them raises every environment's revision, which is the API's ETag.
/// </remarks>
internal sealed class FlagCommands(PalancaDbContext db, TimeProvider timeProvider)
{
    public const int DescriptionMaxLength = 1000;
    public const string InvalidDescription = "Describe the flag in 1 to 1000 characters.";

    public async Task<CommandResult> CreateAsync(
        string applicationKey, string? key, string? description, IReadOnlyCollection<string> onInEnvironments,
        ChangeActor actor, CancellationToken cancellationToken)
    {
        if (!Keys.IsValid(key))
        {
            return CommandResult.Invalid(Applications.ApplicationCommands.InvalidKey);
        }

        if (Text.Clean(description, DescriptionMaxLength, allowLineBreaks: true) is not { } cleanDescription)
        {
            return CommandResult.Invalid(InvalidDescription);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Applications.SingleOrDefaultAsync(a => a.Key == applicationKey && a.ArchivedAt == null, cancellationToken) is not { } application)
        {
            return CommandResult.NotFound("That application does not exist or is archived.");
        }

        var existing = await db.Flags.SingleOrDefaultAsync(f => f.ApplicationId == application.Id && f.Key == key, cancellationToken);
        if (existing is not null)
        {
            return CommandResult.Conflict(existing.ArchivedAt is null
                ? $"{application.Name} already has a flag with the key {key}."
                : $"{application.Name} already has an archived flag with the key {key}. Restore it instead.");
        }

        var environments = await ActiveEnvironmentsAsync(application.Id, cancellationToken);
        var unknown = onInEnvironments.Except(environments.Select(e => e.Key)).FirstOrDefault();
        if (unknown is not null)
        {
            return CommandResult.Invalid($"{application.Name} has no environment with the key {unknown}.");
        }

        var now = Now;
        var flag = new Flag { ApplicationId = application.Id, Key = key!, Description = cleanDescription, CreatedAt = now };
        db.Flags.Add(flag);
        await db.SaveChangesAsync(cancellationToken);

        db.Record(actor, ChangeAction.FlagCreated, now, application, flag: flag, newValue: cleanDescription);
        foreach (var environment in environments.Where(e => onInEnvironments.Contains(e.Key)))
        {
            db.FlagValues.Add(new FlagValue { FlagId = flag.Id, EnvironmentId = environment.Id, Enabled = true, UpdatedAt = now, UpdatedBy = actor.Name });
            db.Record(actor, ChangeAction.FlagValueSet, now, application, environment, flag, oldValue: FlagValueWriter.Off, newValue: FlagValueWriter.On);
        }

        RaiseRevisions(environments);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    public async Task<CommandResult> EditDescriptionAsync(
        string applicationKey, string flagKey, string? description, ChangeActor actor, CancellationToken cancellationToken)
    {
        if (Text.Clean(description, DescriptionMaxLength, allowLineBreaks: true) is not { } cleanDescription)
        {
            return CommandResult.Invalid(InvalidDescription);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindAsync(applicationKey, flagKey, cancellationToken) is not { } flag)
        {
            return FlagNotFound;
        }

        if (flag.Description != cleanDescription)
        {
            db.Record(actor, ChangeAction.FlagDescriptionChanged, Now, flag.Application, flag: flag, oldValue: flag.Description, newValue: cleanDescription);
            flag.Description = cleanDescription;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    /// <summary>Removes a flag from API responses and keeps its history (FLG-013).</summary>
    public Task<CommandResult> ArchiveAsync(string applicationKey, string flagKey, ChangeActor actor, CancellationToken cancellationToken) =>
        SetArchivedAsync(applicationKey, flagKey, archived: true, actor, confirmed: true, cancellationToken);

    /// <summary>
    /// Puts a flag back in API responses with the values it had. When it is on in a protected environment, that
    /// environment starts serving it again, so the restore must be confirmed (FLG-014).
    /// </summary>
    public Task<CommandResult> RestoreAsync(string applicationKey, string flagKey, ChangeActor actor, bool confirmed, CancellationToken cancellationToken) =>
        SetArchivedAsync(applicationKey, flagKey, archived: false, actor, confirmed, cancellationToken);

    /// <summary>Deletes an archived flag and its values. Its change records stay, since they keep the flag's key (FLG-013).</summary>
    public async Task<CommandResult> DeleteAsync(string applicationKey, string flagKey, ChangeActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindAsync(applicationKey, flagKey, cancellationToken) is not { } flag)
        {
            return FlagNotFound;
        }

        if (flag.ArchivedAt is null)
        {
            return CommandResult.Conflict($"Archive {flag.Key} before deleting it.");
        }

        db.Record(actor, ChangeAction.FlagDeleted, Now, flag.Application, flag: flag, oldValue: flag.Description);
        db.Flags.Remove(flag);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    private async Task<CommandResult> SetArchivedAsync(
        string applicationKey, string flagKey, bool archived, ChangeActor actor, bool confirmed, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindAsync(applicationKey, flagKey, cancellationToken) is not { } flag)
        {
            return FlagNotFound;
        }

        if (!archived && !confirmed && await db.FlagValues.AnyAsync(
                v => v.FlagId == flag.Id && v.Enabled && v.Environment.IsProtected && v.Environment.ArchivedAt == null, cancellationToken))
        {
            return CommandResult.ConfirmationRequired($"{flag.Key} is on in a protected environment, which would serve it again. Confirm to restore it.");
        }

        if (flag.ArchivedAt is not null != archived)
        {
            var now = Now;
            flag.ArchivedAt = archived ? now : null;
            db.Record(actor, archived ? ChangeAction.FlagArchived : ChangeAction.FlagRestored, now, flag.Application, flag: flag);
            RaiseRevisions(await ActiveEnvironmentsAsync(flag.ApplicationId, cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CommandResult.Done;
    }

    private static CommandResult FlagNotFound => CommandResult.NotFound("That flag does not exist.");

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private static void RaiseRevisions(List<AppEnvironment> environments)
    {
        foreach (var environment in environments)
        {
            environment.Revision++;
        }
    }

    private Task<List<AppEnvironment>> ActiveEnvironmentsAsync(int applicationId, CancellationToken cancellationToken) =>
        db.Environments.Where(e => e.ApplicationId == applicationId && e.ArchivedAt == null).OrderBy(e => e.SortOrder).ToListAsync(cancellationToken);

    // Archived flags are found too, so they can be restored or deleted; archived applications are not.
    private Task<Flag?> FindAsync(string applicationKey, string flagKey, CancellationToken cancellationToken) =>
        db.Flags.Include(f => f.Application)
            .SingleOrDefaultAsync(f => f.Key == flagKey && f.Application.Key == applicationKey && f.Application.ArchivedAt == null, cancellationToken);
}
