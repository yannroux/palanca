using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.History;

internal sealed record HistoryFilter(string? Application, string? Environment, string? Flag, ChangeSource? Source, long? Before)
{
    public bool IsEmpty => Application is null && Environment is null && Flag is null && Source is null;
}

/// <param name="Older">The cursor for the next, older page, or null on the last page.</param>
/// <param name="Applications">Every application key, archived ones included, since their history stays.</param>
internal sealed record ChangePage(IReadOnlyList<ChangeRecord> Records, long? Older, IReadOnlyList<string> Applications);

/// <summary>Change records, newest first, filtered by application, environment, flag, and source (History).</summary>
internal sealed class HistoryQueries(PalancaDbContext db)
{
    public const int PageSize = 50;

    public async Task<ChangePage> PageAsync(HistoryFilter filter, CancellationToken cancellationToken)
    {
        var records = db.ChangeRecords.AsNoTracking();
        if (filter.Application is not null)
        {
            records = records.Where(r => r.ApplicationKey == filter.Application);
        }

        if (filter.Environment is not null)
        {
            records = records.Where(r => r.EnvironmentKey == filter.Environment);
        }

        if (filter.Flag is not null)
        {
            records = records.Where(r => r.FlagKey == filter.Flag);
        }

        if (filter.Source is { } source)
        {
            records = records.Where(r => r.Source == source);
        }

        if (filter.Before is { } before)
        {
            records = records.Where(r => r.Id < before);
        }

        // Ids only grow, so they order the history and page it without skipping records added meanwhile.
        var page = await records.OrderByDescending(r => r.Id).Take(PageSize + 1).ToListAsync(cancellationToken);
        var older = page.Count > PageSize ? page[PageSize - 1].Id : (long?)null;
        // Applications are archived, never removed, and their keys never change.
        var applications = await db.Applications.AsNoTracking().OrderBy(a => a.Key).Select(a => a.Key).ToListAsync(cancellationToken);
        return new ChangePage([.. page.Take(PageSize)], older, applications);
    }
}
