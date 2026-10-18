using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Flags;

internal enum SetFlagValueOutcome
{
    Changed,
    Unchanged,
    NotFound,

    /// <summary>The environment is protected and the change was not confirmed (FLG-014); nothing changed.</summary>
    ConfirmationRequired,
}

/// <param name="Revision">The environment's revision after the call.</param>
internal sealed record SetFlagValueResult(SetFlagValueOutcome Outcome, long Revision);

/// <summary>
/// Sets a flag's value in one environment. The dashboard and the management API both write
/// through here, so every change bumps the environment's revision and writes one change record
/// in the same transaction (FLG-015, FLG-042), and setting the current value changes nothing (FLG-089).
/// </summary>
internal sealed class FlagValueWriter(PalancaDbContext db, TimeProvider timeProvider)
{
    public const string On = "on";
    public const string Off = "off";

    /// <param name="confirmed">
    /// Whether a change to a protected environment was confirmed. The dashboard asks the owner first; the
    /// management API's read-write tokens may change protected environments by design.
    /// </param>
    public async Task<SetFlagValueResult> SetAsync(
        string applicationKey, string flagKey, string environmentKey, bool enabled, ChangeActor actor, bool confirmed, CancellationToken cancellationToken)
    {
        // A non-deferred SQLite transaction holds the write lock from the start, so the reads
        // below cannot go stale before the write: concurrent writers queue instead of racing.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var environment = await db.Environments
            .Where(e => e.Key == environmentKey && e.ArchivedAt == null)
            .Where(e => e.Application.Key == applicationKey && e.Application.ArchivedAt == null)
            .SingleOrDefaultAsync(cancellationToken);
        var flag = environment is null
            ? null
            : await db.Flags.SingleOrDefaultAsync(
                f => f.ApplicationId == environment.ApplicationId && f.Key == flagKey && f.ArchivedAt == null, cancellationToken);
        if (environment is null || flag is null)
        {
            return new SetFlagValueResult(SetFlagValueOutcome.NotFound, 0);
        }

        var value = await db.FlagValues.FindAsync([flag.Id, environment.Id], cancellationToken);
        var current = value?.Enabled ?? false;
        if (current == enabled)
        {
            return new SetFlagValueResult(SetFlagValueOutcome.Unchanged, environment.Revision);
        }

        // Checked inside the transaction, so protection added a moment ago still applies.
        if (environment.IsProtected && !confirmed)
        {
            return new SetFlagValueResult(SetFlagValueOutcome.ConfirmationRequired, environment.Revision);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (value is null)
        {
            value = new FlagValue { FlagId = flag.Id, EnvironmentId = environment.Id, UpdatedBy = actor.Name };
            db.FlagValues.Add(value);
        }

        value.Enabled = enabled;
        value.UpdatedAt = now;
        value.UpdatedBy = actor.Name;
        environment.Revision++;
        db.ChangeRecords.Add(new ChangeRecord
        {
            OccurredAt = now,
            Source = actor.Source,
            Actor = actor.Name,
            Action = ChangeAction.FlagValueSet,
            ApplicationId = environment.ApplicationId,
            ApplicationKey = applicationKey,
            EnvironmentId = environment.Id,
            EnvironmentKey = environment.Key,
            FlagId = flag.Id,
            FlagKey = flag.Key,
            OldValue = current ? On : Off,
            NewValue = enabled ? On : Off,
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SetFlagValueResult(SetFlagValueOutcome.Changed, environment.Revision);
    }
}
