using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;
using Palanca.History;

namespace Palanca.Management;

internal sealed record EnvironmentResource(string Key, string Name, bool Protected, long Revision);

internal sealed record ApplicationResource(string Key, string Name, IReadOnlyList<EnvironmentResource> Environments);

internal sealed record ApplicationsResponse(IReadOnlyList<ApplicationResource> Applications);

/// <summary>A flag's value in one environment. A flag never set there is off, with no change to show.</summary>
internal sealed record FlagValueResource(bool Enabled, DateTime? UpdatedAt, string? UpdatedBy);

/// <param name="Values">The flag's value in each environment, by environment key, in the board's order.</param>
internal sealed record FlagResource(string Key, string Description, DateTime CreatedAt, IReadOnlyDictionary<string, FlagValueResource> Values);

internal sealed record FlagListResponse(string Application, IReadOnlyList<FlagResource> Flags);

/// <param name="Source"><c>dashboard</c>, <c>api</c>, or <c>host</c>.</param>
/// <param name="Summary">The change as one sentence, as the History page words it.</param>
internal sealed record ChangeResource(
    long Id, DateTime OccurredAt, string Source, string Actor, string Action, string Summary,
    string? Environment, string? Flag, string? OldValue, string? NewValue);

/// <param name="Next">The cursor for the next, older page, passed back as <c>before</c>; null on the last page.</param>
internal sealed record HistoryResponse(string Application, IReadOnlyList<ChangeResource> Changes, long? Next);

/// <summary>What the management API reads (FLG-080). Archived applications, environments, and flags are left out.</summary>
internal sealed class ManagementQueries(PalancaDbContext db, HistoryQueries history)
{
    public Task<Application?> ApplicationAsync(string key, CancellationToken cancellationToken) =>
        db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == key && a.ArchivedAt == null, cancellationToken);

    /// <summary>The applications in the token's scope, with their environments.</summary>
    public async Task<ApplicationsResponse> ApplicationsAsync(ManagementCaller caller, CancellationToken cancellationToken)
    {
        var all = caller.AllApplications;
        var ids = caller.ApplicationIds.ToList();
        var applications = await db.Applications.AsNoTracking()
            .Where(a => a.ArchivedAt == null && (all || ids.Contains(a.Id)))
            .OrderBy(a => a.Key)
            .Select(a => new ApplicationResource(a.Key, a.Name, a.Environments.Where(e => e.ArchivedAt == null).OrderBy(e => e.SortOrder)
                .Select(e => new EnvironmentResource(e.Key, e.Name, e.IsProtected, e.Revision)).ToList()))
            .ToListAsync(cancellationToken);
        return new ApplicationsResponse(applications);
    }

    /// <summary>The application's flags, or only the one with <paramref name="flagKey"/>.</summary>
    public async Task<List<FlagResource>> FlagsAsync(Application application, string? flagKey, CancellationToken cancellationToken)
    {
        var environments = await db.Environments.AsNoTracking()
            .Where(e => e.ApplicationId == application.Id && e.ArchivedAt == null)
            .OrderBy(e => e.SortOrder)
            .ToListAsync(cancellationToken);
        var flags = db.Flags.AsNoTracking().Include(f => f.Values).Where(f => f.ApplicationId == application.Id && f.ArchivedAt == null);
        if (flagKey is not null)
        {
            flags = flags.Where(f => f.Key == flagKey);
        }

        return (await flags.OrderBy(f => f.Key).ToListAsync(cancellationToken)).ConvertAll(flag => new FlagResource(
            flag.Key, flag.Description, flag.CreatedAt, environments.ToDictionary(e => e.Key, e =>
                flag.Values.FirstOrDefault(v => v.EnvironmentId == e.Id) is { } value
                    ? new FlagValueResource(value.Enabled, value.UpdatedAt, value.UpdatedBy)
                    : new FlagValueResource(false, null, null))));
    }

    /// <summary>The application's change records, newest first, 50 at a time.</summary>
    public async Task<HistoryResponse> HistoryAsync(
        Application application, string? environmentKey, string? flagKey, long? before, CancellationToken cancellationToken)
    {
        var page = await history.PageAsync(new HistoryFilter(application.Key, environmentKey, flagKey, Source: null, before), cancellationToken);
        return new HistoryResponse(application.Key, [.. page.Records.Select(Resource)], page.Older);
    }

    private static ChangeResource Resource(ChangeRecord record) => new(
        record.Id, record.OccurredAt, Name(record.Source.ToString()), record.Actor, Name(record.Action.ToString()), ChangeWording.Describe(record),
        record.EnvironmentKey, record.FlagKey, record.OldValue, record.NewValue);

    private static string Name(string member) => JsonNamingPolicy.CamelCase.ConvertName(member);
}
