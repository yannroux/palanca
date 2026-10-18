using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Applications;

internal sealed record EnvironmentColumn(int Id, string Key, string Name, bool IsProtected, long Revision);

internal sealed record ApplicationSummary(string Key, string Name, IReadOnlyList<EnvironmentColumn> Environments, int FlagCount);

/// <summary>One flag's value in one environment. A flag never set there is off, with no change to show.</summary>
internal sealed record FlagCell(EnvironmentColumn Environment, bool Enabled, DateTime? UpdatedAt, string? UpdatedBy);

internal sealed record FlagRow(string Key, string Description, DateTime CreatedAt, DateTime? ArchivedAt, IReadOnlyList<FlagCell> Cells);

/// <summary>A recent value change, for the board's Latest flips list.</summary>
internal sealed record Flip(DateTime OccurredAt, string FlagKey, string EnvironmentName, bool Enabled, ChangeSource Source, string Actor);

internal sealed record KeyRow(int Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

internal sealed record EnvironmentKeys(EnvironmentColumn Environment, IReadOnlyList<KeyRow> Keys);

/// <param name="Applications">The names of the applications in the token's scope; empty when it covers all of them.</param>
internal sealed record TokenRow(
    int Id, string Name, string Prefix, TokenPermission Permission, bool AllApplications, IReadOnlyList<string> Applications,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt);

internal sealed record Board(
    ApplicationSummary Application,
    IReadOnlyList<FlagRow> Flags,
    IReadOnlyList<FlagRow> ArchivedFlags,
    IReadOnlyList<Flip> LatestFlips);

/// <summary>What the dashboard pages show: applications, the flag grid, and recent changes.</summary>
internal sealed class BoardQueries(PalancaDbContext db)
{
    public const int LatestFlipCount = 6;

    public async Task<List<ApplicationSummary>> ApplicationsAsync(CancellationToken cancellationToken)
    {
        var applications = await db.Applications.AsNoTracking()
            .Where(a => a.ArchivedAt == null)
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                a.Key,
                a.Name,
                Environments = a.Environments.Where(e => e.ArchivedAt == null).OrderBy(e => e.SortOrder)
                    .Select(e => new EnvironmentColumn(e.Id, e.Key, e.Name, e.IsProtected, e.Revision)).ToList(),
                FlagCount = a.Flags.Count(f => f.ArchivedAt == null),
            })
            .ToListAsync(cancellationToken);
        return applications.ConvertAll(a => new ApplicationSummary(a.Key, a.Name, a.Environments, a.FlagCount));
    }

    public async Task<ApplicationSummary?> ApplicationAsync(string applicationKey, CancellationToken cancellationToken) =>
        (await ApplicationsAsync(cancellationToken)).Find(a => a.Key == applicationKey);

    public async Task<Board?> BoardAsync(string applicationKey, CancellationToken cancellationToken)
    {
        if (await ApplicationAsync(applicationKey, cancellationToken) is not { } application)
        {
            return null;
        }

        var flags = await db.Flags.AsNoTracking()
            .Where(f => f.Application.Key == applicationKey)
            .Include(f => f.Values)
            .OrderBy(f => f.Key)
            .ToListAsync(cancellationToken);
        var rows = flags.ConvertAll(f => Row(f, application.Environments));

        var names = application.Environments.ToDictionary(e => e.Key, e => e.Name);
        var flips = await db.ChangeRecords.AsNoTracking()
            .Where(r => r.ApplicationKey == applicationKey && r.Action == ChangeAction.FlagValueSet)
            .OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id)
            .Take(LatestFlipCount)
            .ToListAsync(cancellationToken);

        return new Board(
            application,
            [.. rows.Where(r => r.ArchivedAt is null)],
            [.. rows.Where(r => r.ArchivedAt is not null).OrderByDescending(r => r.ArchivedAt)],
            flips.ConvertAll(r => new Flip(
                r.OccurredAt, r.FlagKey!, names.GetValueOrDefault(r.EnvironmentKey!, r.EnvironmentKey!),
                r.NewValue == Flags.FlagValueWriter.On, r.Source, r.Actor)));
    }

    /// <summary>Each environment's SDK keys, newest first, revoked ones included.</summary>
    public async Task<List<EnvironmentKeys>?> KeysAsync(string applicationKey, CancellationToken cancellationToken)
    {
        if (await ApplicationAsync(applicationKey, cancellationToken) is not { } application)
        {
            return null;
        }

        var ids = application.Environments.Select(e => e.Id).ToList();
        var keys = await db.SdkKeys.AsNoTracking()
            .Where(k => ids.Contains(k.EnvironmentId))
            .OrderByDescending(k => k.CreatedAt).ThenByDescending(k => k.Id)
            .Select(k => new { k.EnvironmentId, Row = new KeyRow(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt) })
            .ToListAsync(cancellationToken);
        return application.Environments
            .Select(e => new EnvironmentKeys(e, [.. keys.Where(k => k.EnvironmentId == e.Id).Select(k => k.Row)]))
            .ToList();
    }

    /// <summary>Every management token, newest first, expired and revoked ones included.</summary>
    public Task<List<TokenRow>> TokensAsync(CancellationToken cancellationToken) =>
        db.ManagementTokens.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Select(t => new TokenRow(
                t.Id, t.Name, t.Prefix, t.Permission, t.AllApplications, t.Applications.OrderBy(a => a.Name).Select(a => a.Name).ToList(),
                t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt))
            .ToListAsync(cancellationToken);

    public async Task<FlagRow?> FlagAsync(string applicationKey, string flagKey, CancellationToken cancellationToken)
    {
        if (await ApplicationAsync(applicationKey, cancellationToken) is not { } application)
        {
            return null;
        }

        var flag = await db.Flags.AsNoTracking().Include(f => f.Values)
            .SingleOrDefaultAsync(f => f.Application.Key == applicationKey && f.Key == flagKey, cancellationToken);
        return flag is null ? null : Row(flag, application.Environments);
    }

    private static FlagRow Row(Flag flag, IReadOnlyList<EnvironmentColumn> environments) =>
        new(flag.Key, flag.Description, flag.CreatedAt, flag.ArchivedAt, [.. environments.Select(environment =>
        {
            var value = flag.Values.FirstOrDefault(v => v.EnvironmentId == environment.Id);
            return new FlagCell(environment, value?.Enabled ?? false, value?.UpdatedAt, value?.UpdatedBy);
        })]);
}
