namespace Palanca.Data;

/// <summary>Who made a change, and through which surface. A change through the API also names its token's id.</summary>
internal sealed record ChangeActor(ChangeSource Source, string Name, int? TokenId = null);

internal static class ChangeLog
{
    /// <summary>
    /// Adds a change record to the context, to be saved in the same transaction as the change it describes (FLG-042).
    /// </summary>
    public static void Record(
        this PalancaDbContext db, ChangeActor actor, ChangeAction action, DateTime occurredAt,
        Application? application = null, AppEnvironment? environment = null, Flag? flag = null,
        string? oldValue = null, string? newValue = null) =>
        db.ChangeRecords.Add(new ChangeRecord
        {
            OccurredAt = occurredAt,
            Source = actor.Source,
            Actor = actor.Name,
            TokenId = actor.TokenId,
            Action = action,
            ApplicationId = application?.Id,
            ApplicationKey = application?.Key,
            EnvironmentId = environment?.Id,
            EnvironmentKey = environment?.Key,
            FlagId = flag?.Id,
            FlagKey = flag?.Key,
            OldValue = oldValue,
            NewValue = newValue,
        });
}
