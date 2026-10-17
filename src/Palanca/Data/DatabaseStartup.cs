using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Palanca.Data;

/// <summary>
/// Brings the database up to date before the server starts taking requests (FLG-063):
/// copies an existing database aside, applies pending migrations, and turns on WAL mode.
/// </summary>
internal sealed partial class DatabaseStartup(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseStartup> logger) : IHostedLifecycleService
{
    // Runs before any hosted service starts, the web server included.
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var database = options.Value;
        var databaseExists = File.Exists(database.FilePath) && new FileInfo(database.FilePath).Length > 0;
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PalancaDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            if (databaseExists)
            {
                await BackUpAsync(db, database.BackupDirectory, pending[0], cancellationToken);
            }

            await db.Database.MigrateAsync(cancellationToken);
            LogMigrated(logger, pending.Count, pending[^1]);
        }

        // The journal mode is stored in the database file, so this changes something only once.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
    }

    private async Task BackUpAsync(PalancaDbContext db, string backupDirectory, string firstPendingMigration, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = Path.Combine(backupDirectory, $"palanca-{timestamp}-before-{firstPendingMigration}.db");

        // VACUUM INTO writes a consistent copy of a live database, WAL contents included.
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {target}", cancellationToken);
        LogBackedUp(logger, target);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up the database to {BackupPath} before migrating")]
    private static partial void LogBackedUp(ILogger logger, string backupPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Count} database migration(s), now at {Migration}")]
    private static partial void LogMigrated(ILogger logger, int count, string migration);
}
